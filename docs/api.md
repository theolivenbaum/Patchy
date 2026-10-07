# Public API notes

The entry points are `PsdDocument` (load, render, extract text), `PsdLayer` (the layer tree) and `RgbaImage` (output). This page covers the typed views added on top of the raw blocks, and the packages.

## Image resources: `PsdDocument.Resources`

`document.Resources` (`XRay.Psd.Resources.PsdImageResources`) parses the common image resources on first access and caches the result. A property is null (or an empty list) when the resource is missing or does not decode; a damaged resource never throws. The raw blocks stay in `PsdDocument.ImageResources` and `GetImageResource(id)`.

| Property | Resource | Notes |
|---|---|---|
| `Resolution` | 1005 | Horizontal and vertical pixels per inch plus the display units. The stored value is always per inch, whatever the unit says (the reference verified this against Photoshop; `print_settings_from_resolution_resource`). Non-positive values read as 300, as in the reference. |
| `GridAndGuides` | 1032 | Grid cycles and guide positions in pixels (stored in 1/32 pixel). Guides keep their sign; the reference clamps negatives to 0 only for its own document model. A grid cycle of 0 or less reads as 18 px. |
| `Thumbnail` | 1036, else 1033 | Format (JPEG or raw), size, row bytes and the encoded payload. 1033 (Photoshop 4) stores BGR; `IsBgr` says so. The core library has no JPEG decoder; `XRay.Psd.Skia` adds `PsdThumbnail.Decode()`. |
| `Slices` | 1050 | Version 6 binary records (with the per-slice descriptor Photoshop 7 and later append, skipped) or the version 7/8 descriptor. Text extraction reads the same resource for slice names. |
| `LayerComps` | 1065 | Name, comment, comp ID and the visibility/position/appearance capture flags, plus the last applied comp. |
| `PrintScale` | 1062 | Style, position and scale. |
| `PixelAspectRatio` | 1064 | Width over height. |
| `VersionInfo` | 1057 | Writer and reader names, file version, and the "real merged data" flag that `HasRealMergedImage` and `RenderSource.Auto` use. |
| `IccProfile` | 1039 | Raw bytes plus the description, color space, class and version from the managed ICC parser. |
| `Xmp` | 1060 | The XMP packet as text. |
| `Iptc` | 1028 | Every IPTC-NAA dataset with its record and dataset numbers; `Text` decodes UTF-8 and falls back to Latin-1. |
| `ChannelNames` | 1045, else 1006 | Alpha channel names. |

The reference reads only 1005 and 1032 (`.reference/src/psd/psd_image_resources.cpp`); the other layouts follow Adobe's file format specification and were checked against Photoshop-saved files.

## Layer effects: `PsdLayer.Style`

`layer.Style` (`XRay.Psd.Layers.PsdLayerStyle`) is a read-only view of the effects descriptor that `PsdLayer.Effects` already exposes raw. `Effects` lists every effect instance in file order, including disabled ones and every instance of a multi-instance effect (`lmfx`); `ActiveEffects` keeps the ones that render; `Visible` is the master switch.

Each `PsdLayerEffect` has its kind, switch, blend mode, opacity (0 to 1), color, and where they apply size, distance, angle (with "Use Global Light" resolved), spread or choke, stroke position, bevel style, technique, altitude and depth, and the bevel shadow mode, color and opacity. The values come from the compositor's own parser (`Rendering/LayerEffects.cs`), so defaults and clamps match what renders. A disabled effect is parsed as if it were enabled, so it reports the same values Photoshop would apply if switched on. Gradient and pattern fills report `HasGradient` and a null `Color`.

## Command line

`psdtool info file.psd --resources` prints the typed resources; `--effects` prints each layer's effects under it.

## Packages

`dotnet pack -c Release` produces `XRay.Psd` and `XRay.Psd.Skia` with their symbol packages (`.snupkg`). Package metadata is shared in `Directory.Build.props`; only the two libraries set `IsPackable`. Source Link comes from the .NET SDK (`PublishRepositoryUrl`, `EmbedUntrackedSources`), and builds under GitHub Actions, Azure Pipelines or `CI=true` set `ContinuousIntegrationBuild` for reproducible paths. The core package carries the repository README; the Skia package has its own (`src/XRay.Psd.Skia/README.md`). There is no package icon yet.
