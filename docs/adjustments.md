# Adjustment layers

What each adjustment layer does at render time, where its math comes from, and how far it is from Photoshop. The compositor side (backdrop copy, clip-mode blend, masks, Blend If) is in [rendering.md](rendering.md).

Every adjustment runs on 8-bit values: the float backdrop is quantized to bytes, mapped, and returned as byte / 255. Channel-separable kinds build three 256-entry tables (`LutAdjustment`); the others map one pixel at a time through a struct `IPixelMap` (`PixelAdjustment<TMap>`, one specialized loop per kind, with the last color cached for flat areas). Every table is built when the adjustment is created and the cache lives in locals of `Apply`, so rows can be adjusted concurrently in any order.

## Status

| Kind | Key | Source of the math | Against Photoshop |
|---|---|---|---|
| Levels, Curves, Hue/Saturation, Brightness/Contrast, Exposure, Invert, Threshold, Posterize | `levl`, `curv`, `hue2`/`hue `, `brit`/`CgEd`, `expA`, `nvrt`, `thrs`, `post` | The reference (`.reference/docs/adjustments-calibration.md`) | See rendering.md |
| Color Balance | `blnc` | Fitted here | Within 1/255 (midtones) and 2/255 (all ranges) on the two committed captures |
| Photo Filter | `phfl` | Linear-light gel model, checked here | White within 2/255 on psd-tools' `fill_adjustments.psd` |
| Vibrance | `vibA` | Model chosen here | Closer than leaving it out on `fill_adjustments.psd` only |
| Channel Mixer | `mixr` | Photoshop's definition | No capture; exact by definition |
| Gradient Map | `grdm` | Photoshop's definition plus the gradient engine | No capture |
| Black and White | `blwh` | Widely used reverse-engineered formula | No capture |
| Selective Color | `selc` | Reverse-engineered formula | Parsed and modeled, not rendered |
| Color Lookup | `clrL` | None | Renders as a no-op |

CMYK and gray documents run every kind on RGB; the reference evaluates the channel-wise kinds on the inks through the document's ICC profile (TODO.md, Color).

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

`clrL` embeds the LUT file (`.cube`, `.3dl`, `.csp` text or an ICC profile) in its descriptor. Not parsed; the layer renders as a no-op. psd-tools' only sample carries no LUT.

## Noise gradients

Noise (`ClNs`) gradients in gradient fills, overlays, strokes and Gradient Maps evaluate `GradientNoise`, a port of the reference's `smooth_noise` and `gradient_noise_channel` (`.reference/src/core/blend_math.cpp`): four octaves of smoothstep-interpolated value noise from splitmix64 hashes of the seed (`RndS`), channel and lattice cell, frequency `4 + 60 * roughness`, pulled toward 0.5 as roughness (`Smth`, 0..4096) falls, then mapped into the channel's `Mnm `/`Mxm ` percent range. RGB is native, HSB converts, Lab goes through the reference's OKLab-shaped approximation; with `ShTr` the fourth channel is opacity. Photoshop's own random sequence is unknown, so bands differ from Photoshop's; the result is deterministic and has the right character. On psd-tools' `effect-stroke-gradient.psd` (a noise stroke) the layer composite went from max 245, mean 14.8 to max 152, mean 9.0 against Photoshop. Gradient fill and stroke content without an `Angl` key runs at 0 degrees, as in the reference's `parse_fill_content`; effects keep 90.

## Captures used

`photoshop-color-balance.psd` and `photoshop-color-balance-full.psd` are committed (`AdjustmentTests` pins them). So are psd-tools' `fill_adjustments.psd` and `effect-stroke-gradient.psd` (MIT, in `tests/fixtures/psd-tools/`), which `ColorAdjustmentTests` pins for the adjustment chain and the noise-gradient stroke. psd-tools' single-adjustment files (`layers/*.psd`) have no pixels under the adjustment, so their composites are empty and only their payloads are useful.
