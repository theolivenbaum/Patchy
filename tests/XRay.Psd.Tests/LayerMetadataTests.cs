using XRay.Psd.Layers;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// The layer metadata view (<c>shmd</c>): items, layer time and per-comp state. The real
/// Photoshop file is psd-tools' layer_comps.psd (PsdToolsCorpusTests); these build the
/// block synthetically to cover every value form and damaged input.
/// </summary>
public sealed class LayerMetadataTests
{
    [Fact]
    public void Items_layer_time_and_comp_states_decode()
    {
        var state = new DescriptorWriter("null")
            .Add("enab", false)
            .Add("Ofst", Point(-4, 7))
            .Add("FXRefPoint", Point(1.5, 2.5))
            .Add("vectorMask", new DescriptorWriter("null").Add("Ofst", Point(3, 0)))
            .Add("blendOptions", new DescriptorWriter("null")
                .Add("Md  ", DescriptorWriter.BlendMode("multiply"))
                .Add("Opct", DescriptorWriter.Percent(40))
                .Add("fillOpacity", DescriptorWriter.Percent(250)))
            .Add("Lefx", new DescriptorWriter("null").Add("masterFXSwitch", true))
            .Add("compList", new object[] { 11, 12 });
        var document = new DescriptorWriter("null").Add("enab", true).Add("compList", new object[] { 0 });
        var comps = new DescriptorWriter("null")
            .Add("origFXRefPoint", Point(5, 6))
            .Add("LyrI", 7)
            .Add("layerSettings", new object[] { state, document });
        var block = Metadata(
            ("mdyn", true, [0, 0, 1, 3]),
            ("cust", false, new DescriptorWriter("null").Add("layerTime", 1_700_000_000.25).Versioned()),
            ("cmls", false, comps.Versioned()));

        var metadata = Load(block)!;

        Assert.Equal(["mdyn", "cust", "cmls"], metadata.Items.Select(i => i.Key));
        Assert.True(metadata.Items[0].CopyOnSheetDuplication);
        Assert.Null(metadata.Items[0].Descriptor);
        Assert.Equal(4, metadata.GetItem("mdyn")!.Data.Length);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_250), metadata.LayerTime);
        Assert.Equal(7, metadata.CompLayerId);
        Assert.Equal(new PsdPoint(5, 6), metadata.OriginalEffectsReferencePoint);

        var comp = metadata.GetCompState(12)!;
        Assert.Equal([11, 12], comp.CompIds);
        Assert.False(comp.Visible);
        Assert.Equal((new PsdPoint(-4, 7), new PsdPoint(3, 0), new PsdPoint(1.5, 2.5)), (comp.Offset, comp.VectorMaskOffset, comp.EffectsReferencePoint));
        Assert.Equal((PsdBlendMode.Multiply, 0.4, 1.0), (comp.BlendMode, comp.Opacity, comp.FillOpacity));
        Assert.NotNull(comp.Effects);

        var saved = metadata.GetCompState(0)!;
        Assert.True(saved.Visible);
        Assert.Null(saved.Offset);
        Assert.Null(saved.BlendMode);
        Assert.Null(metadata.GetCompState(99));
    }

    [Fact]
    public void Layers_without_metadata_report_null()
    {
        var builder = new PsdBuilder();
        builder.Layers.Add(new BuilderLayer { Name = "plain", Rect = new PsdRect(0, 0, 1, 1) });

        Assert.Null(PsdDocument.Load(builder.Build()).Layers.Single().Metadata);
    }

    [Fact]
    public void Damaged_metadata_keeps_the_items_that_decode()
    {
        var good = new DescriptorWriter("null").Add("layerTime", 10.0).Versioned();
        var block = Metadata(("cust", false, good), ("cmls", false, [0, 0, 0, 16, 0xFF]));

        // A second item whose descriptor is cut short, then a count promising more items.
        block[3] = 9;
        var metadata = Load(block)!;

        Assert.Equal(["cust", "cmls"], metadata.Items.Select(i => i.Key));
        Assert.Null(metadata.Items[1].Descriptor);
        Assert.Empty(metadata.CompStates);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(10), metadata.LayerTime);

        // Truncation at every length never throws.
        for (var length = 0; length < block.Length; length++)
        {
            _ = Load(block[..length]);
        }

        // A bad signature stops the walk; fewer than four bytes is no metadata at all.
        var badSignature = Metadata(("cust", false, good));
        badSignature[4] = (byte)'X';
        Assert.Empty(Load(badSignature)!.Items);
        Assert.Null(Load([0, 0]));
    }

    private static DescriptorWriter Point(double x, double y) => new DescriptorWriter("Pnt ").Add("Hrzn", x).Add("Vrtc", y);

    private static byte[] Metadata(params (string Key, bool Copy, byte[] Data)[] items)
    {
        var w = new PsdBuilder.Writer();
        w.U32((uint)items.Length);
        foreach (var (key, copy, data) in items)
        {
            w.Ascii("8BIM");
            w.Ascii(key);
            w.U8((byte)(copy ? 1 : 0));
            w.Zeros(3);
            w.U32((uint)data.Length);
            w.Bytes(data);
        }

        return w.ToArray();
    }

    private static PsdLayerMetadata? Load(byte[] block)
    {
        var builder = new PsdBuilder();
        var layer = new BuilderLayer { Name = "meta", Rect = new PsdRect(0, 0, 1, 1) };
        layer.Blocks.Add(("shmd", block));
        builder.Layers.Add(layer);
        return PsdDocument.Load(builder.Build()).Layers.Single().Metadata;
    }
}
