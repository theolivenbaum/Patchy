# Adjustment layers

What each adjustment layer does at render time, where its math comes from, and how far it is from Photoshop. The compositor side (backdrop copy, clip-mode blend, masks, Blend If) is in [rendering.md](rendering.md).

Every adjustment runs on 8-bit values: the float backdrop is quantized to bytes, mapped, and returned as byte / 255. Channel-separable kinds build three 256-entry tables (`LutAdjustment`); the others map one pixel at a time through a struct `IPixelMap` (`PixelAdjustment<TMap>`, one specialized loop per kind, with the last color cached for flat areas). Every table is built when the adjustment is created and the cache lives in locals of `Apply`, so rows can be adjusted concurrently in any order. Two kinds keep 16-bit precision in 16-bit documents (Threshold and Posterize, below), and CMYK, grayscale and duotone documents run the channel-wise kinds on their own channels (below).

## Status

| Kind | Key | Source of the math | Against Photoshop |
|---|---|---|---|
| Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Invert, Threshold, Posterize | `levl`, `curv`, `hue2`/`hue `, `brit`/`CgEd`, `expA`, `nvrt`, `thrs`, `post` | The reference (`.reference/docs/adjustments-calibration.md`), with the Levels slope cap, Threshold rounding and 16-bit Posterize found here | Per file in the table below and in rendering.md |
| Color Balance | `blnc` | Fitted here | Within 1/255 (midtones) and 2/255 (all ranges) on the two committed captures |
| Photo Filter | `phfl` | Linear-light gel model, checked here | White within 2/255 on psd-tools' `fill_adjustments.psd` |
| Vibrance | `vibA` | Model chosen here | Closer than leaving it out on `fill_adjustments.psd` only |
| Channel Mixer | `mixr` | Photoshop's definition | No capture; exact by definition |
| Gradient Map | `grdm` | Photoshop's definition plus the gradient engine | No capture |
| Black and White | `blwh` | Widely used reverse-engineered formula | No capture |
| Selective Color | `selc` | Reverse-engineered formula | Parsed and modeled, not rendered |
| Color Lookup | `clrL` | The embedded LUT file or ICC profile | Parsed; rendered only when `RenderOptions.RenderColorLookup` (internal) is on |

### What the psd-tools collection can measure

All 356 files of the psd-tools collection were scanned for adjustment layers with something visible below them, so that the merged image records Photoshop's result. The `adjustments/` folder has Levels, Curves, Brightness/Contrast (legacy and modern), Exposure, Invert, Threshold and Posterize over a photograph in RGB, CMYK (SWOP) and grayscale (Dot Gain 20%) documents, some at 16 bits, plus four Hue/Saturation files. Nothing else measures a kind beyond the reference: the `layers/*.psd` single-adjustment files have nothing under the layer; in `fill_adjustments.psd` the Gradient Map and Black and White layers are hidden, the Selective Color record is all zeros and the Color Lookup descriptor is empty. Gradient Map, Black and White, Channel Mixer, Photo Filter without Preserve Luminosity, the Vibrance curve, more Color Balance ranges and Selective Color therefore stay as described below until a capture exists.

Layer composite against Photoshop's merged image (max / mean per channel), before and after this work (`PsdToolsCorpusTests` pins every file when the collection is checked out under `local-test-fixtures/`; the five marked files are committed):

| File | Before | After |
|---|---|---|
| `levels_cmyk.psd` (committed) | 140 / 18.60 | 63 / 0.52 |
| `curves_cmyk.psd` | 251 / 31.09 | 155 / 0.83 |
| `invert_cmyk.psd` | 189 / 49.82 | 26 / 0.24 |
| `brightnesscontrast_cmyk.psd` | 176 / 11.37 | 53 / 0.57 |
| `brightnesscontrast_legacy_cmyk.psd` | 42 / 3.56 | 7 / 0.14 |
| `posterize_cmyk.psd` | 146 / 12.67 | 146 / 0.46 |
| `posterize_16bits_cmyk.psd` | 255 / 18.96 | 255 / 0.58 |
| `threshold_cmyk.psd` | 255 / 35.30 | 224 / 0.32 |
| `levels_grayscale.psd` (committed) | 70 / 8.58 | 12 / 0.26 |
| `curves_grayscale.psd` | 34 / 5.41 | 13 / 0.04 |
| `invert_grayscale.psd` | 43 / 13.88 | 9 / 0.03 |
| `brightnesscontrast_grayscale.psd` | 20 / 5.89 | 9 / 0.03 |
| `brightnesscontrast_legacy_grayscale.psd` | 12 / 2.51 | 2 / 0.13 |
| `exposure_grayscale.psd` (committed) | 20 / 1.51 | 5 / 0.01 |
| `posterize_grayscale.psd` | 127 / 15.15 | 107 / 0.04 |
| `posterize_16bits_grayscale.psd` | 255 / 15.59 | 255 / 0.06 |
| `threshold_grayscale.psd` | 255 / 26.77 | 255 / 0.04 |
| `threshold_16bits_grayscale.psd` | 255 / 26.72 | 255 / 0.01 |
| `threshold_rgb.psd` (committed) | 255 / 0.61 | 255 / 0.01 |
| `threshold_16bits_rgb.psd` | 255 / 0.01 | 0 / 0.00 |
| `posterize_16bits_rgb.psd` (committed) | 128 / 0.89 | 0 / 0.00 |
| `levels_rgb.psd` | 5 / 0.31 | 4 / 0.31 |

The other files of the folder (RGB Curves, Exposure, Brightness/Contrast, Invert, 8-bit Posterize, Hue/Saturation and the nested compositions) are unchanged. A max of 255 on a Threshold or Posterize file is a handful of pixels that sit exactly on a step.

## CMYK, grayscale and duotone documents

Photoshop evaluates an adjustment layer on the document's own channels; the compositor works in sRGB. As in the reference (`apply_adjustment_in_ink_space` in `.reference/src/core/adjustment_layer.cpp`), the channel-wise kinds take the backdrop back to device values through an ink space (`Imaging/Icc/IccInkSpace.cs`, `PsdDocument.InkSpace`; see [color.md](color.md#ink-spaces)), map each channel and return to sRGB through the same tables that convert the pixels (`Adjustments.Inks.cs`). Device values use the stored convention (0 = full ink, 255 = none; gray 0 = black).

- Levels, Curves, Invert, Posterize and Brightness/Contrast build one 256-entry table per channel from the ordinary math. Cyan, magenta and yellow read the records an RGB document calls red, green and blue; black has its own: the `levl` block's fifth record and the `curv` curve with channel index 4 (`LevelsSettings.BlackInk`, `CurvesSettings.BlackInk`), run with the composite record like the others. An adjustment whose tables are all the identity is skipped, so it does not round-trip the colors for nothing.
- Threshold on a gray channel compares the stored value with the level. In CMYK, Photoshop paints black ink alone (C = M = Y = 0, K = 100%, SWOP's (35, 31, 32)) or no ink, by the rounded 30/59/11 luma of the naive mix `R = C' K' / 255` of the stored values: right on 99.9% of 34,000 single-layer pixels of `threshold_cmyk.psd`, where the luma of the converted sRGB color misclassifies 7%. The misses sit within half a level of the step. The reference leaves CMYK Threshold on RGB.
- Exposure on a gray channel works in that channel's linear light: each level is linearized through the document's gray conversion (the luminance its profile gives it), exposed, offset and gamma-corrected as on RGB without the 2.2 power, and mapped back to the nearest level (`GrayExposureTable`). On `exposure_grayscale.psd` that is within 5/255 everywhere and exact on 99% of the pixels; the RGB math is up to 20/255 off in the shadows and the 2.2 power on the stored level 23/255. Photoshop offers no Exposure in CMYK, so a CMYK document's keeps the RGB math.
- Hue/Saturation, Color Balance and the other kinds mix channels and keep their RGB math, as in the reference. Photoshop has CMYK forms of them (Selective Color is native CMYK); no capture shows them.
- Duotone documents get the one-channel form through their ink mix; no Photoshop capture checks it.

Residuals: the round trip from sRGB to the inks cannot recover a color the sRGB gamut clipped, so saturated CMYK yellows and blues under a strong curve miss by up to 155 levels on a few hundred pixels (`curves_cmyk.psd`); the black generation of the profile's `B2A1` table differs from the original separation by a level on a third of the pixels; 16-bit CMYK and gray Levels and Curves use the 8-bit tables.

## Levels gamma near black

A brightening gamma (`x^(1/g)` with `g > 1`) has an infinite slope at black. Photoshop's curve leaves black no steeper than 3 (output range over input range): a straight line of that slope up to the point where it meets the power curve. On `levels_grayscale.psd` (gammas 1.52 and 1.78) the plain power curve put the first 18 levels above the black point up to 11 levels too high; with the cap the file goes from mean 0.46 to 0.26. A line up to a fixed 1/16 of the range fits that file slightly better (0.22) but moves the gamma 1.10 Levels of `photoshop-blend-if-4b-roundtrip.psd` away from its capture, which the cap leaves alone. Two strong gammas are all the data there is, so the constant is a fit.

## Threshold and Posterize precision

The 8-bit Threshold compares the rounded integer luma `(30R + 59G + 11B + 50) / 100` with the level (`ThresholdLevel`): on `threshold_rgb.psd` that misclassifies 1 of 34,000 single-layer pixels, the reference's truncated luma 85. A 16-bit document compares the unrounded luma of its 16-bit values, which classifies every pixel of `threshold_16bits_rgb.psd` (the 8-bit rule misses 86). The committed `photoshop-threshold.psd` is exact either way.

A 16-bit document posterizes at Photoshop's 16-bit precision (0..32768, `Posterize16Value`): equal-width buckets of the 16-bit value, each mapped to the truncated 16-bit step. Three levels give 0, 16384 and 32768, so the middle step reads 128 in 8 bits, where the 8-bit table gives 127. Byte-exact on all four layers of `posterize_16bits_rgb.psd` (3, 7, 13 and 21 levels); the 8-bit table missed 5% of the pixels by up to 128 levels.

## Color Balance

`blnc` holds three i16 triples (shadows, midtones, highlights; each Cyan-Red, Magenta-Green, Yellow-Blue, -100..100) and a Preserve Luminosity byte, 20 bytes in all. The reference renders a flat `round(slider * 2.55)` midtones offset, which is up to 98/255 off. The model here (`BuildColorBalanceLut`) is three per-channel tables; each channel runs:

1. Highlights as a Levels input white point. A slider toward the channel's own color (red for R) lowers that channel's white by 1.26 levels per step; a slider of another channel toward its complement (cyan, magenta, yellow) lowers this channel's white by half that.
2. Midtones as a gamma on the channel: `v' = 255 * (v/255)^(2^(-slider/100))`.
3. Shadows as a Levels input black point. A slider toward the complement raises the channel's own black by 2.18 levels per step; a slider of another channel toward its own color raises this channel's black by half that.

Shadows therefore only deepen and highlights only lift; a shift toward a color moves the other two channels half as far the other way.

Evidence: `photoshop-color-balance.psd` (midtones 45, -25, 35, Preserve Luminosity off; 8 colors) pins the midtones gamma, within 1/255 on all 24 samples (an earlier bell-curve fit, `slider * 0.64 * 4x(1 - x)` with `x = (v/255)^0.631`, was within 1/255 too, but the gamma has no free constants). `photoshop-color-balance-full.psd` (shadows -10, 5, 15; highlights -30, 20, -5; Preserve Luminosity on; same 8 colors) fixes the rest: with the midtones gamma known, each channel's output fits a plain Levels black and white point within 1 to 2 levels, and red's white point does not move although its highlights slider is -30, which rules out the "own channel only" reading (best own-channel fit: max 26) and leads to the complement rule. The step sizes, the half share and the stage order (highlights, midtones, shadows) are the best of a grid search over orders and shares (max 2/255, mean 1.0; with a full share instead of half, max 3; every other order tried, max 4 or more).

Limits: one capture of shadows and highlights is all there is, so the model is a fit, not a derivation. Preserve Luminosity is not modeled separately (the full capture has it on, the midtones one off; neither output preserves luminosity in any standard sense). psd-tools' `fill_adjustments.psd` (small shadows and highlights inside a chain of eleven adjustments) prefers a shadows step near 1.0, which would push the full capture to max 20, so the fixture fit stands. More captures (one range at a time, both Preserve Luminosity states, a gray ramp) would settle it.

## Photo Filter

`phfl`: u16 version; version 3 stores the filter color as three i32 Lab values times 100, version 2 as a binary color (u16 space, four u16 components; Photoshop's own presets write Lab, space 7); then a u32 density percent and a Preserve Luminosity byte. The Lab color converts through `ColorSpaces.LabToSrgb` (D50, Bradford); Warming Filter (85), Lab (67.06, 32, 120), becomes (236, 138, 0), the RGB value Photoshop documents for it.

Math (`PhotoFilterValue`): each channel is multiplied in linear light, `lin(c) * (1 - d + d * lin(filter))`, a gel in front of the lens; with Preserve Luminosity, SetLum (Photoshop's Color-mode luminosity, 0.3/0.59/0.11, with ClipColor) restores the original luminosity. On `fill_adjustments.psd` the warming layer turns the white background, (217, 212, 209) after the adjustments below it, into (228, 208, 199) against Photoshop's (230, 207, 200). Refuted there: the multiply in gamma-encoded values (blue 22 levels low on white), a lerp toward the filter color, a Color-blend lerp, and a shift of a* and b* in Lab. Leaving the layer out makes the chain worse (mean 6.2/255 against 4.15). Preserve Luminosity off is the same multiply without SetLum and has no capture.

## Vibrance

`vibA`: a version-16 descriptor with `vibrance` and `Strt` (saturation), both -100..100. Photoshop's algorithm is not published. The model (`VibranceValue`): every channel moves away from (or toward) the maximum by `vibrance * (1 - s)`, `s = (max - min) / max`, so dull colors change most and neutrals never; then Saturation scales the distance from the luminosity by `1 + saturation/100`. The only capture is `fill_adjustments.psd` (vibrance -6, saturation +2 inside the chain): with the layer the chain's mean error is 4.15/255, without it 4.45. Two other shapes (CamanJS's `|max - avg|` weighting, and weighting by `s` instead of `1 - s`) measured within 0.05 of it, so the data does not pin the curve; this one is kept because it follows Photoshop's documented behavior for positive values.

## Channel Mixer

`mixr`: u16 version 1, u16 monochrome, then five-value i16 records (red, green, blue, a fourth ink used by CMYK documents, constant) for each output channel; a monochrome mixer stores its gray record first. Each output is `(R*r + G*g + B*b)/100 + constant * 2.55`, rounded and clamped; monochrome writes the gray record to all three channels. This is Photoshop's definition of the dialog; there is no capture with a non-identity mixer.

## Gradient Map

`grdm`: u16 version (1, or 3 with a four-byte interpolation key after the flags: `Gcls`, `Perc`, `Lnr `), reverse and dither bytes, the gradient name, color stops (i32 location 0..4096, i32 midpoint percent, binary color, u16 stop type), transparency stops (i32, i32, u16 opacity 0..255), then the expansion count, smoothness (0..4096), length, form (1 = noise), seed, transparency and vector-color flags, roughness, color model (3 RGB, 4 HSB, 6 Lab) and four noise minimums and maximums on a 0..32768 scale. Layouts checked against psd-tools' `gradient-map*.psd`.

The pixel's luminosity, Threshold's integer `(30R + 59G + 11B) / 100`, indexes a 256-entry ramp sampled from the gradient with the eased segment interpolation of gradient fill layers (`endpointSmoothing`), reversed when the layer says so. Transparency stops and dither are ignored. Noise forms use the noise gradient below. No capture shows a visible Gradient Map, so the luminosity weights and the easing are unverified.

## Black and White

`blwh`: a version-16 descriptor with `Rd  `, `Yllw`, `Grn `, `Cyn `, `Bl  `, `Mgnt` (percents, defaults 40, 60, 40, 60, 20, 80), `useTint` and `tintColor`. Gray is `min + (mid - min) * secondary + (max - mid) * primary`, where the primary weight belongs to the largest channel (reds, greens, blues) and the secondary to the pair of the two largest (yellows when blue is smallest, cyans when red is, magentas when green is). Neutrals keep their value and the defaults give pure red 102, yellow 153, blue 51, as Photoshop does. A tint takes the tint color's hue and saturation at the gray's luminosity (SetLum). No capture; the tint in particular is a guess.

## Selective Color

`selc`: u16 version 1, u16 method (0 relative, 1 absolute), ten records of four i16 percents (cyan, magenta, yellow, black); the first record is unused, the others are reds, yellows, greens, cyans, blues, magentas, whites, neutrals and blacks. `SelectiveColorValue` implements the common reverse-engineered model (range weights from the channel order, `((-1 - ink) * black - ink)` per channel, times `1 - v` in relative mode), but with no capture to measure it the layer stays unrendered.

## Color Lookup

`clrL`: u16 version 1, then a version-16 descriptor (`ParseColorLookup`). Photoshop stores the lookup two ways: `profile` (raw data) holds an ICC profile, always for the `abstractProfile` and `deviceLinkProfile` lookup types and alongside the file for `3DLUT`; for 3D LUT files `LUT3DFileData` holds the file and `LUTFormat` names its format. The profile is used when it parses, since it is what Photoshop renders from; otherwise the file is read:

- `.cube` (Adobe's Cube LUT specification, `LUTFormatCUBE`): `LUT_3D_SIZE` (2..256) and/or `LUT_1D_SIZE`, `DOMAIN_MIN`/`DOMAIN_MAX` or Resolve's `LUT_1D_INPUT_RANGE`/`LUT_3D_INPUT_RANGE`, rows with red varying fastest; a 1D table in front of a lattice acts as its shaper.
- `.3dl` (`LUTFormat3DL`): optional `3DMESH` and `Mesh inBits outBits` lines, the input positions (0..1023 for 10 bits), then integer rows with blue varying fastest. The input scale is the smallest `2^k - 1` at or above the last position; the output scale comes from the `Mesh` line or the largest value.
- `.csp` (cineSpace, `LUTFormatCSP`): `CSPLUTV100`, `1D` or `3D`, an optional metadata block, three per-channel pre-LUTs, then the lattice size and rows (red fastest).
- ICC device links (RGB to RGB) and abstract profiles (Lab or XYZ to Lab or XYZ) are sampled on a 33-node lattice; abstract profiles take the document's sRGB through the D50 PCS and back.

`LUTFormatLOOK` (SpeedGrade XML) is not read. The lookup applies per pixel on the float backdrop with trilinear interpolation (`ColorLookupTable.Apply`), clamped to [0,1]; dither (`Dthr`) is ignored. No file in the psd-tools collection carries a LUT (its only Color Lookup layer has an empty descriptor), so whether Photoshop interpolates trilinearly or tetrahedrally, and how it orders `LUT3DFileData` against `profile`, is unmeasured. Rendering therefore stays off by default (`RenderOptions.RenderColorLookup`, internal); `ColorAdjustmentTests` pins the parsers and the interpolation on synthetic files.

## Noise gradients

Noise (`ClNs`) gradients in gradient fills, overlays, strokes and Gradient Maps evaluate `GradientNoise`, a port of the reference's `smooth_noise` and `gradient_noise_channel` (`.reference/src/core/blend_math.cpp`): four octaves of smoothstep-interpolated value noise from splitmix64 hashes of the seed (`RndS`), channel and lattice cell, frequency `4 + 60 * roughness`, pulled toward 0.5 as roughness (`Smth`, 0..4096) falls, then mapped into the channel's `Mnm `/`Mxm ` percent range. RGB is native, HSB converts, Lab goes through the reference's OKLab-shaped approximation; with `ShTr` the fourth channel is opacity. Photoshop's own random sequence is unknown, so bands differ from Photoshop's; the result is deterministic and has the right character. On psd-tools' `effect-stroke-gradient.psd` (a noise stroke) the layer composite went from max 245, mean 14.8 to max 152, mean 9.0 against Photoshop. Gradient fill and stroke content without an `Angl` key runs at 0 degrees, as in the reference's `parse_fill_content`; effects keep 90.

## Captures used

`photoshop-color-balance.psd` and `photoshop-color-balance-full.psd` are committed (`AdjustmentTests` pins them). So are psd-tools' `fill_adjustments.psd` and `effect-stroke-gradient.psd` (MIT, in `tests/fixtures/psd-tools/`), which `ColorAdjustmentTests` pins for the adjustment chain and the noise-gradient stroke, and five files of psd-tools' `adjustments/` folder (`levels_cmyk.psd`, `levels_grayscale.psd`, `exposure_grayscale.psd`, `threshold_rgb.psd`, `posterize_16bits_rgb.psd`), which `AdjustmentTests` pins. The rest of that folder is too large to commit; `PsdToolsCorpusTests` checks it when the collection is checked out locally. psd-tools' single-adjustment files (`layers/*.psd`) have no pixels under the adjustment, so their composites are empty and only their payloads are useful.
