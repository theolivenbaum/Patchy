# Color management

How decoded pixels and stored colors become sRGB, and how close that is to the reference. The code is in `src/XRay.Psd/Imaging/` (`ColorSpaces.cs`, `DocumentColors.cs`, `Duotone.cs` and the `Icc/` folder). The reference converts through vendored Little CMS (lcms2) in `.reference/src/color/color_management.cpp`; this port reads ICC profiles itself and follows the same lcms2 semantics without porting lcms2.

## What converts

`ColorSpaces.ToRgb(document, ...)` runs for every layer decode and for the merged image. When `PsdLoadOptions.ColorManagement` is on (the default) and the image resources hold an ICC profile (resource 1039) that fits the color mode, the document's cached `IccSrgbTransform` converts the planes:

| Mode | Profile | Depths | Notes |
|---|---|---|---|
| CMYK | CMYK LUT profile (`A2Bx` or `D2Bx`) | 8, 16 | PSD samples are inverted (1 = no ink); ICC device values are ink amounts. |
| Grayscale | Gray TRC (`kTRC`) or gray LUT | 8, 16 | 32-bit gray stays linear luminance, sRGB-encoded. |
| Duotone | Gray profile | 8, 16 | Ink mix from the duotone specification (below); without a readable specification, the gray path as in the reference. |
| RGB | Matrix/TRC or RGB LUT | 8, 16, 32 | 32-bit data is linear light: only the colorant matrix applies (no TRC); for LUT-only profiles the matrix comes from the LUT's primaries. |
| Indexed | RGB profile | 8 | The palette lookup runs first, then the RGB transform. |
| Lab | none | all | Lab is already the D50 PCS; the fixed D50 to sRGB transform is what lcms2 produces with a built-in Lab profile (black point compensation is a no-op there). |

Everything else keeps the built-in formulas: no profile, an unusable profile (garbage, wrong color space, device link, no conversion tags), color management turned off, and bitmap and multichannel documents. Without a profile CMYK uses the naive `R = C' * K'` mix and gray and RGB are taken as sRGB. Multichannel reads its first three planes as inverted cyan, magenta and yellow inks straight into red, green and blue (a missing plane is no ink), as the reference's `convert_multichannel_planes_to_rgb` does.

An RGB profile that reproduces sRGB within one 8-bit level over a 9x9x9 grid and the shadow ramp (`IccSrgbTransform.IsSrgbEquivalent`) is skipped, so documents tagged with the HP "sRGB IEC61966-2.1" profile Photoshop embeds render byte-identically to before. That profile maps pure green to an encoded red of 0.5 levels because its colorants differ from the D65-derived ones in the fourth decimal.

## Stored colors

Colors that are not pixels go through `DocumentColors` (`PsdDocument.Colors`), the port of the reference's `CmykColorConverter` (`.reference/src/psd/psd_io_internal.hpp`), so they keep their relationship with the converted pixels. A color first quantizes to the 8-bit channel convention (CMYK inverted, gray 0 = black), then converts through the same 8-bit tables as a pixel plane; without a usable transform the built-in formula applies.

| Source | Conversion |
|---|---|
| Descriptor `CMYC` (ink percentages) | CMYK profile of a CMYK document, else `(1 - C)(1 - K)` |
| Descriptor `Grsc` (`Gry ` is percent black) | Gray profile of a grayscale or duotone document, else a neutral copy |
| Descriptor `RGBC`, `redFloat` | RGB profile of an RGB or indexed document; in 32-bit documents the values are linear light (the reference's `linear_rgb`), so they go through the 32-bit path or the sRGB encoding |
| Descriptor `HSBC`, `LbCl` | Fixed formulas, as in the reference |
| EngineData `/FillColor` and `/StrokeColor` (TySh and `Txt2`) | Type 2 as CMYC, type 0 as gray, type 1 as RGB (no linear handling) |
| PS 5 10-byte colors (`tySh` text; `lrFX` uses the same record) | Space 2 (inverted CMYK) as CMYC and space 0 as RGB; HSB and gray stay fixed, as in the reference |
| `Patt` pattern tiles | CMYK tiles in CMYK documents (the reference's `parse_single_pattern`); because this port converts RGB and gray pixels too, also RGB and indexed tiles in RGB documents and gray tiles in grayscale documents |

Descriptors are tagged with the document's converter when the parser reads them (`Descriptor.AttachColors`): layer effects (`lfx2`, `lfxs`, `lmfx`), fill layers (`SoCo`, `GdFl`, `PtFl`), vector stroke content (`vstk`) and the legacy `vscg` stroke paint. `Descriptor.GetColor` on other descriptors keeps the fixed formulas. Gradient stops convert one by one and then interpolate in sRGB, as in the reference.

The fixture `photoshop-cmyk-style-colors.psd` pins the result: its C42 M45 Y67 K13 color overlay renders (143, 123, 92) and its C0 M100 Y100 text color is (237, 28, 36), the reference's Photoshop values.

## Duotone

The reference reads a duotone document as its gray plane. This port renders the inks from the duotone specification in the color mode data, laid out like Photoshop's Duotone Options file: version 1, ink count 1 to 4, four 10-byte colors, four 64-byte Pascal names, four 28-byte transfer functions (13 points at 0, 5, 10, 20 ... 90, 95, 100% tone with values 0 to 1000 or -1 for unset, then an override flag), dot gain and eleven overprint colors. Anything else falls back to the gray path.

For a pixel with tone `t = 1 - gray`, ink `i` covers `c_i = curve_i(t)`, a natural cubic spline through the set points (0% and 100% default to 0 and 100% ink). Its reflectance `r_i` is the luminance of gray level `1 - c_i` through the document's gray profile, or the sRGB decoding without one, so a monotone black ink with a linear curve renders exactly like the grayscale path. Inks act as filters in linear light, `L = prod(1 - (1 - r_i)(1 - ink_i))` per sRGB channel, then encode. Overprint colors and the dot gain field are ignored. Ink colors in RGB, HSB, CMYK (naive), gray and Lab convert; color-book inks cannot be resolved without the books and render black. 32-bit duotone keeps the gray path. This model is not calibrated against Photoshop.

## Pipeline

1. **Device to PCS.** Little CMS's tag choice (`_cmsReadInputLUT`): the intent's float `D2Bx` tag when present (absolute uses `D2B3`; there is no fallback to `D2B0`), else the `A2Bx` LUT for the intent (absolute uses `A2B1`), falling back to `A2B0`; without any LUT, the matrix/TRC model (RGB: `rTRC/gTRC/bTRC` then `rXYZ/gXYZ/bXYZ`; gray: `kTRC` times the D50 white, or `L* = 100 * kTRC` with a Lab PCS).
2. **PCS decoding.** `mft2` outputs use the v2 16-bit Lab encoding (L* 100 at 0xFF00, a and b zero at 0x8000); `mft1`, `mAB` and `mBA` use the v4 encoding; XYZ is u1Fixed15 (`v * 65535 / 32768`); float `mpet` outputs are real XYZ or L* a* b*. Lab converts to XYZ with Little CMS's D50 white (0.9642, 1, 0.8249).
3. **Black point compensation** (on by default, as in the reference). Source black detection follows `cmsDetectBlackPoint`: v4 profiles under perceptual or saturation use the ICC perceptual black (or the darkest colorant for matrix/shaper profiles); a CMYK output profile under relative colorimetric round-trips Lab black through `B2A0` and back through the relative LUT, caps L* at 50 and forces a = b = 0; everything else converts the darkest colorant (all inks, or device zero) and forces it neutral. Profiles whose only LUTs are `D2Bx` count as not supporting the intent there, so their black is zero, as in lcms2. The destination (sRGB) black is zero, so the scaling is `XYZ' = D50 / (D50 - bp) * (XYZ - bp)` per component. Little CMS also forces compensation on for v4 profiles under the perceptual and saturation intents.
4. **Absolute colorimetric** (`ComputeAbsoluteIntent`, observer fully adapted): no black point compensation, and the relative PCS scales per component by the source media white over D50 (the built-in sRGB stores D50). The media white is `wtpt`, or D50 when it is missing or the profile is a v2 display profile (`_cmsReadMediaWhitePoint`). A gray profile with a non-D50 white is then tinted, so it converts through per-level RGB tables.
5. **PCS to sRGB.** The inverse of Little CMS's built-in sRGB colorant matrix (Rec. 709 primaries, white (0.3127, 0.3290), Bradford-adapted to D50), clamped to [0,1], then the IEC 61966-2.1 transfer curve.

Documents use relative colorimetric with black point compensation: the reference's `INTENT_RELATIVE_COLORIMETRIC | cmsFLAGS_BLACKPOINTCOMPENSATION`, and Photoshop's Convert to Profile default. `IccTransformSettings` can ask for the other intents (perceptual selects `A2B0`).

## Parser

`IccProfile.Parse` reads v2 and v4 headers and the tag table, then the tags the transforms need: `desc` (v2 `desc`, `text` or v4 `mluc`), `wtpt`, `chad`, the colorants, the four TRCs (`curv` identity, gamma or table; `para` types 0 to 4), `A2B0..2`/`B2A0..2` in `mft1`, `mft2`, `mAB ` or `mBA ` form, and `D2B0..3` in `mpet` form. Every offset is checked against the tag; a damaged tag is dropped, so parsing returns a profile or null and never throws. CLUTs are capped at 16M values.

LUTs evaluate as a pipeline of curve, matrix and CLUT stages over normalized values. CLUT interpolation matches Little CMS: tetrahedral over the last three inputs, linear across leading inputs (CMYK is linear in C between two tetrahedral M, Y, K lookups), bilinear for two inputs. As in lcms2, an `mft1`/`mft2` matrix applies only to three-channel input and only when it is not the identity. A CLUT-free three-channel `mAB` (curves then a matrix, the v4 matrix/shaper form) runs as an exact matrix/shaper.

`IccMpet` reads the float elements as Little CMS does (`Type_MPE_Read`): `cvst` curve sets of `curf` segmented curves (segment `i` covers `(x_i, x_i+1]` with minus and plus 1e22 at the ends; `parf` formula types 0 to 2; `samf` samples spread evenly over the segment with the previous segment's value at the breakpoint as the implicit first point), `matf` matrices with offsets (no clamping), and float `clut` tables (16 grid bytes, then the values) with the same interpolation as above. `bACS`/`eACS` are skipped; any other element, a channel count that does not chain, or data past the tag drops the tag.

## Lookup tables

`IccSrgbTransform.Apply` converts whole planes through tables built once per input depth and cached on the transform, which the document caches in turn:

- Gray TRC: one table per input level (256 or 65536 entries).
- Matrix/shaper RGB: three per-level linearization tables, one SIMD 3x3 matrix plus offset (black point compensation and absolute scaling folded in) over the planes, then a 4096-segment sRGB encoding table (error under 2e-5). 32-bit RGB skips the linearization; with a LUT-only profile its matrix is the PCS of each full primary minus that of device black, the closest linear-gamma version of the profile.
- LUT profiles: the whole conversion sampled on a grid in PSD sample space, 17 nodes per axis for CMYK (Little CMS's default for four channels) and 33 for three-channel LUTs, with the same interpolation as above. Gray LUTs get a per-level table.

Single stored colors (above) use the 8-bit tables too, so they match pixels exactly. Large planes (256K pixels and up) convert in parallel 64K-pixel chunks; pixels are independent, so the output does not depend on chunking. Grid sampling also runs in parallel. On a 4-core container a 4-megapixel CMYK plane converts in about 100 ms after a one-off grid build of about 100 ms (including JIT); matrix/shaper RGB takes about 35 ms.

## 32-bit documents

The reference converts 32-bit channels with the plain transfer: color clamps to [0,1] and sRGB-encodes (`linear_to_srgb8`, "matches PS's default Exposure and Gamma conversion on the probe colors", `.reference/docs/file-formats.md`), while masks and transparency scale linearly. This port does the same without a profile, and Photoshop's HDR toning is not modeled. With an RGB profile the linear data goes through the colorant matrix (the reference keeps RGB data in its profile instead). For 32-bit gray the reference sends the encoded bytes through the gray profile; this port treats them as linear luminance, which is how Photoshop's linear-gamma 32-bit mode reads them. Descriptor RGB colors in 32-bit documents are linear too (see Stored colors).

## Accuracy

- SWOP (`photoshop-cmyk-style-colors.psd` embeds "U.S. Web Coated (SWOP) v2"): the reference's Photoshop pins all match (paper white, K100 (35,31,32), all inks black, C0 M100 Y100 (237,28,36), the C43 Y98 fill (158,204,62), the C42 M45 Y67 K13 overlay color (143,123,92)), for pixels, effect colors and text colors alike. Against lcms 2.19 on 20,000 random 8-bit colors through the planar path, 99.4% of pixels are byte-identical and none is more than one level off. The detected black point matches lcms to 1e-5. Absolute colorimetric matches lcms on SWOP (paper white (225,223,216)) and on synthetic paper-white gray and RGB profiles.
- The reference measured lcms2 against Photoshop's own conversion (`.reference/docs/ps-compat.md`, "CMYK import color conversion"): 14 of 20 SWOP patches byte-exact, max delta 2. Expect the same residue here.
- The double-precision `Evaluate` path differs from the grid by up to 13 levels where heavy ink clips at the sRGB gamut edge; the grid is what lcms2 and the reference produce, so pixel decoding and stored colors use it.
- Synthetic matrix/TRC, gray and CMYK LUT profiles agree with lcms 2.19 within one level (95.5% to 99.7% byte-exact on 20,000 random colors). For the v4 matrix/shaper `mAB` profile this code is exact while lcms2 samples it and drifts up to 21 levels at the gamut edge. Float `mpet` profiles (formula curves and a matrix; sampled curves and a 4D Lab CLUT; a sampled gray curve) match lcms within one level.
- Dot Gain 20%: Adobe's profile cannot be redistributed, so `TestProfiles.DotGain20Like` interpolates a gray TRC through the reference's Photoshop ramp pins (128 shows as 149); a grayscale ramp document with it reproduces every pin within one level, and a 50% `Grsc` color matches the 128 pixel.

The pins in `ColorManagementTests` come from Pillow's `ImageCms` (lcms 2.19) with `renderingIntent=1` (or 3 for absolute) and `flags = BLACKPOINTCOMPENSATION | NOCACHE` on the same profile bytes, and from `cmsDetectBlackPoint` through ctypes. Re-run that comparison after any change to the transforms. Little CMS builds no relative transform from a profile whose only float tag is `D2B0`, so the float test profiles carry `D2B0` and `D2B1`. Adobe profiles may only be distributed embedded in images, so tests read the SWOP profile out of the committed fixture at run time.

## Gaps

- Named-color and device-link profiles (neither describes a document's pixels).
- Photoshop's 32-bit HDR toning and Little CMS's 16-bit quantization between stages are not modeled.
- Duotone overprint colors and color-book inks; no real duotone fixture to calibrate against.
- No real fixture yet for a non-sRGB RGB profile or for Dot Gain gray.
