using System.Globalization;
using XRay.Psd;
using XRay.Psd.Layers;

/// <summary>The optional sections of <c>psdtool info</c>: typed image resources and layer styles.</summary>
internal static class InfoDetails
{
    public static void PrintResources(PsdDocument document)
    {
        var r = document.Resources;
        Console.WriteLine($"resources: {string.Join(' ', document.ImageResources.Select(x => x.Id.ToString(CultureInfo.InvariantCulture)))}");
        if (r.VersionInfo is { } version)
        {
            Console.WriteLine($"  version info: writer \"{version.WriterName}\", reader \"{version.ReaderName}\", real merged data: {version.HasRealMergedData}");
        }

        if (r.Resolution is { } resolution)
        {
            Console.WriteLine(Invariant($"  resolution: {resolution.HorizontalPpi:0.##} x {resolution.VerticalPpi:0.##} ppi (shown as {resolution.HorizontalDisplayUnit}, size in {resolution.WidthDisplayUnit})"));
        }

        if (r.PixelAspectRatio is { } aspect)
        {
            Console.WriteLine(Invariant($"  pixel aspect ratio: {aspect:0.####}"));
        }

        if (r.PrintScale is { } print)
        {
            Console.WriteLine(Invariant($"  print scale: {print.Style} {print.Scale:0.###} at ({print.X:0.##}, {print.Y:0.##})"));
        }

        if (r.GridAndGuides is { } grid)
        {
            Console.WriteLine(Invariant($"  grid: {grid.HorizontalGridCycle:0.##} x {grid.VerticalGridCycle:0.##} px, {grid.Guides.Count} guides"));
            foreach (var guide in grid.Guides)
            {
                Console.WriteLine(Invariant($"    guide: {guide.Orientation} at {guide.Position:0.###}"));
            }
        }

        if (r.Thumbnail is { } thumbnail)
        {
            Console.WriteLine($"  thumbnail: {thumbnail.Format} {thumbnail.Width}x{thumbnail.Height}{(thumbnail.IsBgr ? " (BGR, resource 1033)" : string.Empty)}, {thumbnail.Data.Length} bytes");
        }

        if (r.IccProfile is { } icc)
        {
            Console.WriteLine($"  icc profile: \"{icc.Description}\" {icc.ColorSpace.Trim()} {icc.DeviceClass} v{icc.Version}, {icc.Data.Length} bytes");
        }

        if (r.Slices is { } slices)
        {
            Console.WriteLine($"  slices: v{slices.Version} group \"{slices.GroupName}\" {slices.Bounds}, {slices.Slices.Count} slices");
            foreach (var slice in slices.Slices)
            {
                Console.WriteLine($"    slice {slice.Id}: \"{slice.Name}\" {slice.Origin} {slice.Bounds}{(slice.Url.Length > 0 ? " url " + slice.Url : string.Empty)}");
            }
        }

        if (r.LayerComps is { } comps)
        {
            Console.WriteLine($"  layer comps: {comps.Comps.Count}{(comps.LastAppliedCompId is { } last ? $", last applied {last}" : string.Empty)}");
            foreach (var comp in comps.Comps)
            {
                Console.WriteLine($"    comp {comp.Id}: \"{comp.Name}\" visibility={comp.CapturesVisibility} position={comp.CapturesPosition} appearance={comp.CapturesAppearance}{(comp.Comment.Length > 0 ? $" \"{comp.Comment}\"" : string.Empty)}");
            }
        }

        if (r.ChannelNames.Count > 0)
        {
            Console.WriteLine($"  channel names: {string.Join(", ", r.ChannelNames.Select(n => $"\"{n}\""))}");
        }

        if (r.Xmp is { } xmp)
        {
            Console.WriteLine($"  xmp: {xmp.Length} characters");
        }

        foreach (var dataSet in r.Iptc)
        {
            Console.WriteLine($"  iptc {dataSet.Record}:{dataSet.DataSet}: {(dataSet.Record == 2 && dataSet.DataSet != 0 ? dataSet.Text : Convert.ToHexString(dataSet.Value.Span))}");
        }
    }

    public static void PrintStyle(PsdLayerStyle style, string indent)
    {
        if (!style.Visible)
        {
            Console.WriteLine($"{indent}effects hidden");
        }

        foreach (var effect in style.Effects)
        {
            Console.WriteLine($"{indent}fx {effect}");
        }
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
