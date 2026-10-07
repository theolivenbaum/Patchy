# Rendering

How `PsdDocument.Render` produces pixels, and how close each part is to Photoshop. Read this before changing anything under `src/Patchy.Psd/Rendering/`.

## Sources

`RenderSource.Auto` decodes the merged image Photoshop saved when the version-info resource (1057) marks it real, or when the document has no layers. Otherwise, and always for `RenderSource.Layers` or a `LayerVisibility` override, the layer compositor runs. Files saved without "Maximize Compatibility" carry a placeholder merged image; the survey labels those `PLCH`.

`MergedImageDecoder` reads the image data section (raw, RLE with per-row counts for all channels first, or one ZIP stream for all channels). The first extra channel is transparency only when the layer count was negative. Partially transparent merged pixels are un-matted from white.

## Pixel model

Everything composites as straight-alpha float32 planes (`PlanarImage`: R, G, B, A arrays positioned in document space). Layer channels decode lazily (`PsdLayer.DecodePixels`) and convert to sRGB in `ColorSpaces`. Planar storage keeps every hot loop a contiguous `Vector<float>` walk:

- `BlendKernels.CompositeRow` applies source-over with blend `B(Cs, Cb)`: `co = as(1-ab)Cs + as*ab*B + (1-as)ab*Cb`, `ao = as + ab - as*ab`, stored as `co/ao`. Clip mode treats the destination as opaque inside existing coverage and never grows alpha (clipping groups, adjustment layers, folded overlays).
- Each blend mode is a struct implementing `IChannelBlend` or `IBlendOp` with static abstract members; `Row<TOp>` is specialized by the JIT, so there is no per-pixel dispatch. Tails run through the same vector step on a padded copy.
- Constants follow Photoshop's 8-bit kernels from `.reference/src/core/blend_math.cpp` (Linear Light and Pin Light use the `-256/255` offsets; Burn, Dodge and Divide keep the destination-led 0/0 corners). The float pipeline can differ from Photoshop's integer rounding by one level per layer.

## Compositor rules (`LayerCompositor`)

- Siblings composite bottom to top. A layer followed by clipped layers starts a clipping run: the run renders into a buffer seeded by the base, members blend at full strength where the base has coverage (clip mode), and the buffer merges with the base's blend mode. A hidden or zero-opacity base hides the run. Groups are never clipped.
- Pass Through groups composite their children straight into the backdrop, with the group's masks applied to each child's alpha, then fade toward a pre-group snapshot by group opacity (premultiplied lerp). Every other group, and any pass-through group with Fill below 100%, isolates its children and merges the result with its blend mode, opacity times Fill, and masks.
- Masks: raster masks sample their rectangle with the default color outside; density lifts the floor (`m*d + 1 - d`); feather is a gaussian approximated by three box passes per axis (the reference's "boxes for gauss" split). Vector masks use Photoshop's baked plane when the mask flags say it was rendered, otherwise `PathRasterizer` (16 sub-scanlines, exact horizontal coverage, even-odd within a subpath group, Add/Subtract/Intersect/Xor between groups, subtract-first starts from full coverage).
- Fill layers synthesize content when the file stores none: solid color, gradient (`GdFl`: center-chord span, eased ramp) or pattern (`PtFl`). The vector mask then shapes it.
- Adjustment layers transform a copy of the backdrop row and blend it back in clip mode with the layer's mode, opacity and masks (`Adjustments.cs`).
- Dissolve uses a splitmix64 threshold of the document coordinate, so results are deterministic.

## Layer effects (`LayerCompositor.Effects.cs`, `LayerCompositor.Bevel.cs`)

Effects come from `lfx2` (layers), `lfxs` (groups) or `lmfx` (multiple instances, authoritative). Each styled layer builds a matte over an effect domain (layer bounds plus the largest effect reach): pixel alpha times the layer masks, unless "Layer Mask Hides Effects" is on, in which case the masks multiply the effect output instead. Effects scale with layer opacity, not Fill.

Order, following the reference `composite_pixel_layer`:

1. Drop shadows (offset matte, spread dilation, tent blur `N = max(2, round(size)) - spread radius`, knocked out by the layer shape) and outer glows (same pipeline plus the Range gain).
2. Stroke underlays: the part of a stroke band where the content is missing.
3. The layer content. Pattern, gradient and color overlays and satin fold into the layer's own color when the layer is Normal (or "Blend Interior Effects as Group" is on) at full Fill; otherwise they paint as separate passes after the content. Strokes without Overprint knock the content out of their inner band.
4. Inner glows and inner shadows (the interior mirror: inverse matte, choke dilation, tent blur), stroke inner bands, then bevel and emboss.

Burn and dodge effect modes fold their alpha into the color (toward white for Linear and Color Burn, toward black for Color Dodge) over opaque destination pixels.

Strokes measure an exact Euclidean distance band from a 3x supersampled half-coverage contour (`EffectMasks.StrokeDistanceFields`); solid, gradient and Shape Burst paints are supported. Effects on a clipping base render around the merged base-plus-members content. Styled groups isolate unless Pass Through, in which case exterior effects paint from the children's silhouette before them and interior effects after them.

## Status against Photoshop

`RenderingTests` pins every fixture that matches; the survey (`PATCHY_PSD_SURVEY=1`, see CLAUDE.md) prints all of them. Exact or within anti-aliasing tolerance: group opacity, raster and vector masks, boolean shapes, solid, gradient and pattern fills, drop shadows, glows, inner shadows, overlays, satin, strokes, effects on groups and clip bases, smooth bevels, emboss, pillow emboss, bevel textures. Known gaps are listed in `TODO.md`: vector strokes on shape layers, feathered shape masks, Blend If, channel restrictions, special Fill blend modes, Stroke Emboss, non-linear glow contours, knockout.
