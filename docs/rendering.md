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
- Adjustment layers transform a copy of the backdrop row and blend it back in clip mode with the layer's mode, opacity and masks (`Adjustments*.cs`). The transform quantizes the backdrop to bytes and runs the reference's calibrated 8-bit math: exact 256-entry tables for Levels (channel record before composite, unrounded between), Curves (natural cubic, component before composite), Brightness/Contrast (legacy hybrid order; modern gain ray, Hermite shoulder and parabola contrast), Exposure, Invert and Posterize; per pixel for Threshold and Hue/Saturation (1530-step wheel, measured saturation and colorize tables, per-hue-range bands). A parseable `CgEd` descriptor wins over the compatibility `brit`. Grayscale documents copy the gray record to all three channels.
- Shape layers with a `vstk` block whose stroke is on, or whose `fillEnabled` is off, render as a whole in `LayerCompositor.Strokes.cs` (reference `rasterize_vector_shape`): the fill content times the path coverage (skipped when fill is off; density applies here), then the stroke band painted with its own content (`strokeStyleContent` solid, gradient or pattern, falling back to the legacy `vscg` block) at its opacity and blend mode, composited over the fill within the layer. The stroke is not limited by the path, so `LayerMasks` skips the vector mask for these layers. A shape feather blurs fill and stroke together (premultiplied, unclamped at the canvas). Aligned stroke gradients span the stroke's own painted bounds.
- Dissolve uses a splitmix64 threshold of the document coordinate, so results are deterministic.

## Vector strokes (`VectorStroker`)

Ported from the reference stroker in `vector_raster.cpp`:

- Each subpath flattens to a polyline through the same integer de Casteljau flattener the reference uses, with every vertex snapped to the 1/256 pixel lattice (sub-quantum micro-segments otherwise seed miter spikes). Open subpaths stay open; the fill still closes them with a chord.
- Dash entries and the dash offset are stroke-width multiples. The walk toggles at each boundary; a zero-length on-entry emits a dot that carries the path tangent so square and round caps orient. More than 262144 boundaries fall back to a solid stroke.
- Every run emits closed loops with one orientation: a quad per segment, a join wedge on the outer side of each turn (miter tip at `h / cos(alpha/2)` with a bevel fallback past the miter limit, round fans by normalized-midpoint halving, bevel triangles), and caps on open runs.
- Inside and outside strokes rasterize the centered band at twice the width and multiply it by the path coverage or its complement, so the kept half is exactly `width` deep and its path-side edge has the fill's anti-aliasing. Aligned dashes keep original-width caps, one per half band.
- The loops' union is rasterized under the non-zero rule on 16 sub-scanlines with exact horizontal coverage. The reference accumulates exact cell areas instead, which adds overlapping loops inside a pixel: the inner corner of a 3 px mitered rectangle came out fully covered where Photoshop shows about 0.78.
- Photoshop closes open contours when a stroked shape has several subpaths (`.reference/docs/open-path-strokes.md`). Like the reference renderer, this one keeps them open.

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

`RenderingTests` pins every fixture that matches; the survey (`PATCHY_PSD_SURVEY=1`, see CLAUDE.md) prints all of them. Exact or within anti-aliasing tolerance: group opacity, raster and vector masks, boolean shapes, solid, gradient and pattern fills, vector strokes on shape layers, drop shadows, glows, inner shadows, overlays, satin, strokes, effects on groups and clip bases, smooth bevels, emboss, pillow emboss, bevel textures, Levels, Brightness/Contrast (legacy within 1/255), Hue/Saturation master (within 1/255 of the BMP) and colorize, Invert, Threshold, Posterize. Hue/Saturation bands are within 7/255 on feather ramps (mean 0.13), the reference's own residual. Curves and Exposure are pinned by the reference's Photoshop LUT captures (`AdjustmentTests`). Known gaps are listed in `TODO.md`: feathered shape masks, Blend If, channel restrictions, special Fill blend modes, Stroke Emboss, non-linear glow contours, knockout, Color Balance and the adjustments the reference does not model.

Vector strokes: `photoshop-shape-strokes` matches its capture with max 39, mean 0.16 (the reference reached mean 0.3). Butt, square and round caps, miter, round and bevel joins, and inside, center and outside alignment on solid strokes are exact or within 8 levels; Photoshop's band edges that fall on half pixels carry a bias of about 1/32 px that is not modeled. The residual peaks are dash-edge pixels on the dashed curve, where Photoshop's arc-length walk differs by a fraction of a pixel. `patchy-open-path-strokes` reproduces its Patchy-written merged image (max 1).
