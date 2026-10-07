# Porting map

Where each part of the C# library came from in the reference tree (`.reference/`), and what was left behind. Paths on the left are under `src/XRay.Psd/`.

| C# | Reference | Notes |
|---|---|---|
| `IO/BigEndianReader.cs` | `src/psd/psd_binary.cpp`, `psd_io_common.cpp` | Pascal strings decode as Latin-1; layers prefer the `luni` Unicode name. |
| `IO/ChannelCodec.cs` | `src/psd/psd_channel_data.cpp` | Damaged rows decode as zeros instead of failing the file. 32-bit prediction splits each row into four byte planes before the delta. |
| `PsdParser.cs` | `src/psd/psd_document_io.cpp`, `psd_layer_records.cpp` | Same mask-data rules: the 36+ byte form carries the real user mask right after the flags; `(flags & 0x18) == 0x10` means parameters only. Length width is chosen by signature (`8B64`) or by key in PSB files. Global blocks are padded to 4 bytes. |
| `PsdParser.ReadLinkedFiles` | `src/psd/psd_smart_objects.cpp` (`parse_link_element`) | Only the fields text extraction and rendering need. |
| `Layers/VectorPath.cs` | `src/psd/psd_vector.cpp` (`parse_records`) | Continuation contours (operation 0xFFFF) inherit the group operation; CS4-era records fall back to Xor. |
| `Rendering/PathRasterizer.cs` | `src/core/vector_raster.cpp` | New scanline rasterizer (16 sub-scanlines, exact horizontal coverage). Group combination and subtract-first rules match the reference. |
| `Layers/VectorStrokeStyle.cs` | `src/psd/psd_vector.cpp` (`parse_vector_stroke_block`) | Widths and dash offsets in points convert through `strokeStyleResolution`; dashes stay in stroke-width multiples. |
| `Rendering/VectorStroker.cs` | `src/core/vector_raster.cpp` (`subpath_polyline`, `apply_dashes`, `append_run_outline`, `rasterize_vector_stroke`) | Same lattice-snapped polylines, dash walk, quads, joins, caps and double-width aligned bands. The band union is sampled on 16 sub-scanlines instead of the reference's area accumulation, which over-covers pixels where outline loops overlap. |
| `Rendering/LayerCompositor.Strokes.cs` | `src/core/vector_raster.cpp` (`rasterize_vector_shape`, `feather_shape_raster`) | Fill under the path, stroke on top with its opacity and blend mode; the vector mask is then skipped for the layer. Split fill/stroke planes and the coverage effect matte (`ShapeRasterResult`) are kept beside the pixels. |
| `Rendering/BlendOps.cs` | `src/core/blend_math.cpp` | Float versions of the 8-bit kernels, including Photoshop's Linear Light (-256) and Pin Light offsets and the Burn/Dodge/Divide 0/0 corners. |
| `Rendering/BlendKernels.cs` | `composite_blended_rgb`, `IsolatedClipGroupTarget` | Clip mode reproduces the frozen clip-group semantics without Blend If. |
| `Rendering/LayerCompositor.cs` | `src/render/layer_compositor.hpp` (`composite_sibling_layers`, `composite_layer`, `composite_pass_through_group`) | Effects live in `LayerCompositor.Effects.cs` and `.Bevel.cs` (from `render_*` and `layer_style_mask_ops.cpp`). No Blend If, channel restrictions or special Fill yet. |
| `Rendering/EffectMasks.cs` | `src/render/layer_style_mask_ops.cpp` | Spread and choke dilation, the tent blur, stroke distance fields, and the Precise glow falloffs (chamfer distance with component strengths, triple box). Effect gradient dither (`apply_gradient_dither`) lives in `LayerCompositor.Effects.cs`. |
| `Rendering/Patterns.cs` | `src/psd/psd_patterns.cpp`, `src/core/pattern_sampler.hpp` | Same sampling rules: nearest at 100%, linear above, box below. |
| `Rendering/Gradient.cs` | `gradient_position`, `gradient_color` in `src/core/blend_math.cpp` | No noise gradients or dither. |
| `Rendering/MaskSampler.cs` | `src/core/layer_render_utils.cpp` (`mask_feather_box_radii`) | Density lifts the floor: `m * d + (1 - d)`. |
| `Rendering/Adjustments*.cs` | `src/core/adjustment_layer.cpp`, `src/psd/psd_adjustments.cpp`, `src/formats/acv_curves_io.cpp` | 8-bit LUTs per channel (Hue/Saturation and Threshold per pixel). Color Balance is parsed, not rendered; the CMYK/gray ink-space evaluation is left out (see TODO.md, Color). |
| `Descriptors/Descriptor.cs` | `src/psd/psd_descriptor.cpp` | Depth-limited to 64 levels like the reference. |
| `Text/EngineData.cs` | `src/psd/engine_data.cpp` | Read-only tree (the reference also round-trips whitespace for writing). |
| `Text/TextLayerInfo.cs`, `Text/EngineStyles.cs` | `src/psd/psd_text_read.cpp` (`extract_engine_text_runs`, `extract_engine_paragraph_runs`, `extract_type_tool_geometry`) | Text from the descriptor `Txt `; runs, paragraphs, fonts, warp and bounds. Fonts stay PostScript names (no platform font resolution). |
| `Text/TextEngineBlock.cs`, `Text/TextEngineResolver.cs` | `src/psd/psd_text_engine_block.cpp`, `docs/txt2.md` | Read-only model; the reference only authors the block. Filling TySh gaps from it is new. |
| `Text/LegacyText.cs` | `src/psd/psd_text_legacy.cpp`, `docs/psd-legacy-text.md` | Same validation and retry rules; kerning and base shift are kept. `ReadColor` is `read_legacy_effect_color`. |
| `Rendering/TextLayerRasterizer.cs`, `Rendering/LayerCompositor.Text.cs` | (new) | The hook for re-rendered type layers. The reference keeps Photoshop's raster for imported text until it is edited (`should_regenerate_imported_text_preview`). |
| `../XRay.Psd.Text/TextLayout.cs` | `src/ui/text_layout.cpp` (`photoshop_text_layout_plan`, `vertical_text_layout_plan`), `docs/text-render-calibration.md` | Same leading, first-baseline, tracking, faux bold/italic, pixel-grid and vertical cell rules on HarfBuzz instead of Qt's text engine. Bidi is a two-level simplification. |
| `../XRay.Psd.Text/FontResolution.cs` | `heuristic_resolved_photoshop_font` in `src/psd/psd_text_read.cpp`, `docs/fonts.md` alias table | No DirectWrite or name-table index; resolution goes through a pluggable `IFontResolver`. |
| `../XRay.Psd.Text/TextWarpMesh.cs` | `src/core/warp_mesh.cpp` (`generate_style_warp_mesh`, `apply_warp_distortion`), `src/core/text_warp.cpp` | Outlines are warped point by point instead of resampling a raster. |
| `Resources/PsdImageResources.cs` | `src/psd/psd_image_resources.cpp` (`print_settings_from_resolution_resource`, `grid_guides_from_resource`) | Resolution and guides follow the reference (per-inch values, 1/32 px units, count bounded by the payload) but keep negative guide positions. The other resources (thumbnail, slices, layer comps, print scale, aspect ratio, version info, IPTC) have no reference reader and follow Adobe's specification. |
| `Layers/PsdLayerStyle.cs` | `src/psd/psd_layer_styles.cpp` via `Rendering/LayerEffects.cs` | A public projection of the compositor's effect records; disabled effects are parsed as if enabled to report their values. |
| `Imaging/Icc/` | `src/color/color_management.cpp` (lcms2 calls), `src/psd/psd_channel_data.cpp` | Managed ICC parser and transforms instead of lcms2, with the same intent and black point compensation; float `D2Bx` (`IccMpet`) follows lcms2's `Type_MPE_Read`. See [color.md](color.md). |
| `Imaging/DocumentColors.cs` | `CmykColorConverter` in `src/psd/psd_io_internal.hpp`, `descriptor_rgb_color`, `rgb_color_from_engine_values`, `parse_single_pattern` | Descriptor, text, legacy and pattern colors through the pixel transform. Also converts RGB colors through an RGB document's profile, since this port converts RGB pixels. |
| `Imaging/Duotone.cs` | none (the reference reads duotone as gray) | Ink curves and a linear-light multiply of the inks; see [color.md](color.md). |

Not ported (out of scope for a reader/renderer, or deferred in TODO.md): the Qt UI, brushes and tools, filters and smart-filter re-rendering, the PSD writer, ASL/ABR/PAT/GRD preset files, other image formats, scripting, plug-ins, and the reference's ink-space tables (sRGB to CMYK for adjustment layers on inks).
