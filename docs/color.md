# Color management

How decoded pixels become sRGB, and how close that is to the reference. The code is in `src/Patchy.Psd/Imaging/` (`ColorSpaces.cs` and the `Icc/` folder). The reference converts through vendored Little CMS (lcms2) in `.reference/src/color/color_management.cpp`; this port reads ICC profiles itself and follows the same lcms2 semantics without porting lcms2.

## What converts

`ColorSpaces.ToRgb(document, ...)` runs for every layer decode and for the merged image. When `PsdLoadOptions.ColorManagement` is on (the default) and the image resources hold an ICC profile (resource 1039) that fits the color mode, the document's cached `IccSrgbTransform` converts the planes:

| Mode | Profile | Depths | Notes |
|---|---|---|---|
| CMYK | CMYK LUT profile (`A2Bx`) | 8, 16 | PSD samples are inverted (1 = no ink); ICC device values are ink amounts. |
| Grayscale, Duotone | Gray TRC (`kTRC`) or gray LUT | 8, 16 | Duotone uses its gray profile, as the reference does. |
| RGB | Matrix/TRC or RGB LUT | 8, 16, 32 | 32-bit data is linear light: only the colorant matrix applies (no TRC). |
| Indexed | RGB profile | 8 | The palette lookup runs first, then the RGB transform. |
| Lab | none | all | Lab is already the D50 PCS; the fixed D50 to sRGB transform is what lcms2 produces with a built-in Lab profile (black point compensation is a no-op there). |

Everything else keeps the built-in formulas: no profile, an unusable profile (garbage, wrong color space, device link, no conversion tags), color management turned off, bitmap and multichannel documents, and 32-bit gray. Without a profile CMYK uses the naive `R = C' * K'` mix and gray and RGB are taken as sRGB.

An RGB profile that reproduces sRGB within one 8-bit level over a 9x9x9 grid and the shadow ramp (`IccSrgbTransform.IsSrgbEquivalent`) is skipped, so documents tagged with the HP "sRGB IEC61966-2.1" profile Photoshop embeds render byte-identically to before. That profile maps pure green to an encoded red of 0.5 levels because its colorants differ from the D65-derived ones in the fourth decimal.

Effect, fill and text colors read from descriptors and EngineData are not color managed yet (see TODO.md).

## Pipeline

1. **Device to PCS.** Little CMS's tag choice: the `A2Bx` LUT for the intent (absolute uses `A2B1`), falling back to `A2B0`; without any LUT, the matrix/TRC model (RGB: `rTRC/gTRC/bTRC` then `rXYZ/gXYZ/bXYZ`; gray: `kTRC` times the D50 white, or `L* = 100 * kTRC` with a Lab PCS). `D2Bx` float tags are not read.
2. **PCS decoding.** `mft2` outputs use the v2 16-bit Lab encoding (L* 100 at 0xFF00, a and b zero at 0x8000); `mft1`, `mAB` and `mBA` use the v4 encoding; XYZ is u1Fixed15 (`v * 65535 / 32768`). Lab converts to XYZ with Little CMS's D50 white (0.9642, 1, 0.8249).
3. **Black point compensation** (on by default, as in the reference). Source black detection follows `cmsDetectBlackPoint`: v4 profiles under perceptual or saturation use the ICC perceptual black (or the darkest colorant for matrix/shaper profiles); a CMYK output profile under relative colorimetric round-trips Lab black through `B2A0` and back through the relative LUT, caps L* at 50 and forces a = b = 0; everything else converts the darkest colorant (all inks, or device zero) and forces it neutral. The destination (sRGB) black is zero, so the scaling is `XYZ' = D50 / (D50 - bp) * (XYZ - bp)` per component. Little CMS also forces compensation on for v4 profiles under the perceptual and saturation intents.
4. **PCS to sRGB.** The inverse of Little CMS's built-in sRGB colorant matrix (Rec. 709 primaries, white (0.3127, 0.3290), Bradford-adapted to D50), clamped to [0,1], then the IEC 61966-2.1 transfer curve.

The intent is relative colorimetric with black point compensation: the reference's `INTENT_RELATIVE_COLORIMETRIC | cmsFLAGS_BLACKPOINTCOMPENSATION`, and Photoshop's Convert to Profile default. `IccTransformSettings` can ask for another intent (perceptual selects `A2B0`), but documents always use the default.

## Parser

`IccProfile.Parse` reads v2 and v4 headers and the tag table, then the tags the transforms need: `desc` (v2 `desc`, `text` or v4 `mluc`), `wtpt`, `chad`, the colorants, the four TRCs (`curv` identity, gamma or table; `para` types 0 to 4) and `A2B0..2`/`B2A0..2` in `mft1`, `mft2`, `mAB ` or `mBA ` form. Every offset is checked against the tag; a damaged tag is dropped, so parsing returns a profile or null and never throws. CLUTs are capped at 16M values.

LUTs evaluate as a pipeline of curve, matrix and CLUT stages over normalized values. CLUT interpolation matches Little CMS: tetrahedral over the last three inputs, linear across leading inputs (CMYK is linear in C between two tetrahedral M, Y, K lookups), bilinear for two inputs. As in lcms2, an `mft1`/`mft2` matrix applies only to three-channel input and only when it is not the identity. A CLUT-free three-channel `mAB` (curves then a matrix, the v4 matrix/shaper form) runs as an exact matrix/shaper.

## Lookup tables

`IccSrgbTransform.Apply` converts whole planes through tables built once per input depth and cached on the transform, which the document caches in turn:

- Gray TRC: one table per input level (256 or 65536 entries).
- Matrix/shaper RGB: three per-level linearization tables, one SIMD 3x3 matrix plus offset (black point compensation folded in) over the planes, then a 4096-segment sRGB encoding table (error under 2e-5).
- LUT profiles: the whole conversion sampled on a grid in PSD sample space, 17 nodes per axis for CMYK (Little CMS's default for four channels) and 33 for three-channel LUTs, with the same interpolation as above. Gray LUTs get a per-level table.

Large planes (256K pixels and up) convert in parallel 64K-pixel chunks; pixels are independent, so the output does not depend on chunking. Grid sampling also runs in parallel. On a 4-core container a 4-megapixel CMYK plane converts in about 100 ms after a one-off grid build of about 100 ms (including JIT); matrix/shaper RGB takes about 35 ms.

## Accuracy

- SWOP (`photoshop-cmyk-style-colors.psd` embeds "U.S. Web Coated (SWOP) v2"): the reference's Photoshop pins all match (paper white, K100 (35,31,32), all inks black, C0 M100 Y100 (237,28,36), the C43 Y98 fill (158,204,62), the C42 M45 Y67 K13 overlay color (143,123,92)). Against lcms 2.19 on 20,000 random 8-bit colors through the planar path, 99.4% of pixels are byte-identical and none is more than one level off. The detected black point matches lcms to 1e-5.
- The reference measured lcms2 against Photoshop's own conversion (`.reference/docs/ps-compat.md`, "CMYK import color conversion"): 14 of 20 SWOP patches byte-exact, max delta 2. Expect the same residue here.
- The double-precision `Evaluate` path differs from the grid by up to 13 levels where heavy ink clips at the sRGB gamut edge; the grid is what lcms2 and the reference produce, so pixel decoding uses it.
- Synthetic matrix/TRC, gray and CMYK LUT profiles agree with lcms 2.19 within one level (95.5% to 99.7% byte-exact on 20,000 random colors). For the v4 matrix/shaper `mAB` profile this code is exact while lcms2 samples it and drifts up to 21 levels at the gamut edge.

The pins in `ColorManagementTests` come from Pillow's `ImageCms` (lcms 2.19) with `renderingIntent=1` and `flags = BLACKPOINTCOMPENSATION | NOCACHE` on the same profile bytes, and from `cmsDetectBlackPoint` through ctypes. Re-run that comparison after any change to the transforms. Adobe profiles may only be distributed embedded in images, so tests read the SWOP profile out of the committed fixture at run time.

## Gaps

- Absolute colorimetric is treated as relative (no media white scaling); `D2Bx`/`mpet` float LUTs, named-color and device-link profiles are not supported.
- 32-bit gray and 32-bit LUT-only RGB profiles are not managed (the data is linear while the profile describes encoded values).
- Photoshop's 32-bit HDR toning and Little CMS's 16-bit quantization between stages are not modeled.
- No real fixture yet for a non-sRGB RGB profile or for Dot Gain gray.
