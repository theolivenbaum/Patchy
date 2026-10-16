using XRay.Psd.Descriptors;
using XRay.Psd.Imaging;
using XRay.Psd.IO;
using XRay.Psd.Text;

namespace XRay.Psd.Rendering;

/// <summary>
/// Photoshop 5 layer effects (<c>lrFX</c>), translated into the <c>lfx2</c> descriptor
/// shape <see cref="LayerEffects"/> reads, so they render and show in
/// <c>PsdLayer.Style</c> like modern effects. Photoshop ignores the block whenever a
/// layer has <c>lfx2</c>, <c>lfxs</c> or <c>lmfx</c> (it is a compatibility mirror
/// there), so the parser only uses it for layers without them
/// (.reference/src/psd/psd_layer_records.cpp; .reference/docs/ps-compat.md, "Legacy lrFX").
/// </summary>
/// <remarks>
/// The block is a u16 version and a u16 count, then records of <c>8BIM</c>, a key and
/// a u32 size whose payload starts at a u32 version (0, or 2 with trailing native
/// colors). Values are 16.16 fixed point, opacities a 0-255 byte, colors the 10-byte
/// record of <see cref="LegacyText.ReadColor"/>. Layouts (Adobe's file format
/// specification, checked against the lfx2 descriptors Photoshop writes beside the
/// block in 208 psd-tools layers):
/// <list type="bullet">
/// <item><c>cmnS</c>: version, visible byte.</item>
/// <item><c>dsdw</c>/<c>isdw</c>: version, size, intensity, angle, distance, color,
/// blend signature and key, enabled, use global light, opacity, native color.</item>
/// <item><c>oglw</c>: version, size, intensity, color, blend, enabled, opacity, native color.
/// <c>iglw</c> adds an edge byte before the native color.</item>
/// <item><c>bevl</c>: version, angle, depth, size, highlight and shadow blends, highlight
/// and shadow colors, style, highlight and shadow opacity, enabled, use global light,
/// direction, native highlight and shadow colors.</item>
/// <item><c>sofi</c>: version, blend, color, opacity, enabled, native color.</item>
/// </list>
/// </remarks>
internal static class LegacyEffects
{
    private const int ColorBytes = 10;

    /// <summary>The translated effects, or null when the block holds none or does not decode.</summary>
    public static Descriptor? ToDescriptor(ReadOnlyMemory<byte> data, DocumentColors? colors)
    {
        try
        {
            var reader = new BigEndianReader(data);
            reader.ReadUInt16(); // version
            var count = reader.ReadUInt16();
            var root = new Descriptor { ClassId = "null" };
            var visible = true;
            var any = false;
            for (var index = 0; index < count && reader.Remaining >= 12; index++)
            {
                var signature = reader.ReadSignature();
                var key = reader.ReadSignature();
                if (signature is not ("8BIM" or "8B64"))
                {
                    break;
                }

                var size = reader.ReadUInt32();
                if (size > reader.Remaining)
                {
                    break;
                }

                var record = new BigEndianReader(reader.ReadMemory((int)size));
                if (key == "cmnS")
                {
                    record.ReadUInt32();
                    visible = record.ReadByte() != 0;
                    continue;
                }

                if (Translate(key, record, colors) is { } effect)
                {
                    root.Add(effect.Key, Object(effect.Value));
                    any = true;
                }
            }

            if (!any)
            {
                return null;
            }

            root.Add("masterFXSwitch", Boolean(visible));
            return root;
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    private static (string Key, Descriptor Value)? Translate(string key, BigEndianReader r, DocumentColors? colors)
    {
        try
        {
            var version = r.ReadUInt32();
            switch (key)
            {
                case "dsdw":
                case "isdw":
                    {
                        var size = r.ReadFixed16();
                        var intensity = r.ReadFixed16();
                        var angle = r.ReadFixed16();
                        var distance = r.ReadFixed16();
                        var color = Color(r, colors);
                        var mode = Blend(r);
                        var enabled = r.ReadByte() != 0;
                        var global = r.ReadByte() != 0;
                        var opacity = r.ReadByte();
                        color = Native(r, version, colors) ?? color;
                        var d = Effect(key == "dsdw" ? "DrSh" : "IrSh", enabled, mode, color, opacity);
                        d.Add("uglg", Boolean(global));
                        d.Add("lagl", Number(Math.Clamp(angle, -360, 360)));
                        d.Add("Dstn", Number(Math.Clamp(distance, 0, 30000)));
                        d.Add("Ckmt", Number(0));
                        d.Add("blur", Number(Math.Clamp(size, 0, 250)));
                        AddIntensity(d, intensity);
                        return (d.ClassId, d);
                    }

                case "oglw":
                case "iglw":
                    {
                        var size = r.ReadFixed16();
                        var intensity = r.ReadFixed16();
                        var color = Color(r, colors);
                        var mode = Blend(r);
                        var enabled = r.ReadByte() != 0;
                        var opacity = r.ReadByte();
                        // Inner glows of version 2 add a source byte: 1 Edge, 0 Center.
                        var edge = key != "iglw" || version < 2 || r.ReadByte() != 0;
                        color = Native(r, version, colors) ?? color;
                        var d = Effect(key == "oglw" ? "OrGl" : "IrGl", enabled, mode, color, opacity);
                        d.Add("GlwT", Enum("BETE", "SfBL"));
                        d.Add("Ckmt", Number(0));
                        d.Add("blur", Number(Math.Clamp(size, 0, 250)));
                        // Photoshop 5 glows have no Range; Photoshop writes its default
                        // (50%) into the lfx2 beside every corpus block.
                        d.Add("Inpr", Number(50));
                        if (key == "iglw")
                        {
                            d.Add("glwS", Enum("IGSr", edge ? "SrcE" : "SrcC"));
                        }

                        AddIntensity(d, intensity);
                        return (d.ClassId, d);
                    }

                case "bevl":
                    {
                        var angle = r.ReadFixed16();
                        var depth = r.ReadFixed16();
                        var size = r.ReadFixed16();
                        var highlightMode = Blend(r);
                        var shadowMode = Blend(r);
                        var highlight = Color(r, colors);
                        var shadow = Color(r, colors);
                        var style = r.ReadByte();
                        var highlightOpacity = r.ReadByte();
                        var shadowOpacity = r.ReadByte();
                        var enabled = r.ReadByte() != 0;
                        var global = r.ReadByte() != 0;
                        var down = r.ReadByte() != 0;
                        if (version >= 2 && r.Remaining >= 2 * ColorBytes)
                        {
                            highlight = LegacyText.ReadColor(r, colors);
                            shadow = LegacyText.ReadColor(r, colors);
                        }

                        var d = new Descriptor { ClassId = "ebbl" };
                        d.Add("enab", Boolean(enabled));
                        d.Add("hglM", Enum("BlnM", highlightMode));
                        d.Add("hglC", Rgb(highlight));
                        d.Add("hglO", Number(highlightOpacity * 100.0 / 255));
                        d.Add("sdwM", Enum("BlnM", shadowMode));
                        d.Add("sdwC", Rgb(shadow));
                        d.Add("sdwO", Number(shadowOpacity * 100.0 / 255));
                        d.Add("bvlT", Enum("bvlT", "SfBL"));
                        d.Add("bvlS", Enum("BESl", style switch { 1 => "OtrB", 3 => "Embs", 4 => "PlEb", 5 => "strokeEmboss", _ => "InrB" }));
                        d.Add("uglg", Boolean(global));
                        d.Add("lagl", Number(Math.Clamp(angle, -360, 360)));
                        d.Add("Lald", Number(30));
                        // Photoshop 5 stores the depth in pixels, at most 20: a 100% depth
                        // reads min(size, 20). Emboss and Pillow Emboss store half the
                        // modern size (both checked against the lfx2 written beside 196
                        // corpus blocks).
                        var embossed = style is 3 or 4;
                        d.Add("srgR", Number(size > 0 ? Math.Clamp(depth / Math.Min(size, 20) * 100, 1, 1000) : 100));
                        d.Add("blur", Number(Math.Clamp(embossed ? size * 2 : size, 0, 250)));
                        d.Add("bvlD", Enum("BESs", down ? "Out " : "In  "));
                        return (d.ClassId, d);
                    }

                case "sofi":
                    {
                        var mode = Blend(r);
                        var color = Color(r, colors);
                        var opacity = r.ReadByte();
                        var enabled = r.ReadByte() != 0;
                        color = Native(r, version, colors) ?? color;
                        var d = Effect("SoFi", enabled, mode, color, opacity);
                        return (d.ClassId, d);
                    }

                default:
                    return null;
            }
        }
        catch (PsdFormatException)
        {
            // A truncated record drops that effect only.
            return null;
        }
    }

    /// <summary>
    /// Legacy Intensity is a transfer contour, not Spread or Choke: coverage times
    /// <c>1 + intensity / 100</c>, clamped (Photoshop 2026 reports 50% as the knee
    /// (170, 255) and 100% as (127, 255); .reference/src/psd/psd_layer_styles.cpp,
    /// <c>parse_lrfx_layer_style</c>).
    /// </summary>
    private static void AddIntensity(Descriptor d, double intensity)
    {
        d.Add("AntA", Boolean(false));
        if (!(intensity > 0))
        {
            return;
        }

        var knee = 255 / (1 + (Math.Min(intensity, 1000) / 100));
        var curve = new List<DescriptorValue>
        {
            Object(Point(0, 0)),
            Object(Point(knee, 255)),
            Object(Point(255, 255)),
        };
        var shape = new Descriptor { ClassId = "ShpC" };
        shape.Add("Nm  ", new DescriptorValue { Type = DescriptorValueType.String, String = "Intensity" });
        shape.Add("Crv ", new DescriptorValue { Type = DescriptorValueType.List, List = curve });
        d.Add("TrnS", Object(shape));
    }

    private static Descriptor Point(double x, double y)
    {
        var point = new Descriptor { ClassId = "CrPt" };
        point.Add("Hrzn", Number(x));
        point.Add("Vrtc", Number(y));
        point.Add("Cnty", Boolean(false));
        return point;
    }

    private static Descriptor Effect(string classId, bool enabled, string mode, PsdColor color, byte opacity)
    {
        var d = new Descriptor { ClassId = classId };
        d.Add("enab", Boolean(enabled));
        d.Add("Md  ", Enum("BlnM", mode));
        d.Add("Clr ", Rgb(color));
        d.Add("Opct", Number(opacity * 100.0 / 255));
        return d;
    }

    private static PsdColor Color(BigEndianReader r, DocumentColors? colors) => LegacyText.ReadColor(r, colors);

    /// <summary>The version 2 native color, which supersedes the first one.</summary>
    private static PsdColor? Native(BigEndianReader r, uint version, DocumentColors? colors) =>
        version >= 2 && r.Remaining >= ColorBytes ? LegacyText.ReadColor(r, colors) : null;

    private static string Blend(BigEndianReader r)
    {
        r.ReadSignature();
        return r.ReadSignature();
    }

    private static DescriptorValue Rgb(PsdColor color)
    {
        var d = new Descriptor { ClassId = "RGBC" };
        d.Add("Rd  ", Number(color.R));
        d.Add("Grn ", Number(color.G));
        d.Add("Bl  ", Number(color.B));
        return Object(d);
    }

    private static DescriptorValue Object(Descriptor d) => new() { Type = DescriptorValueType.Object, Object = d };

    private static DescriptorValue Number(double value) => new() { Type = DescriptorValueType.Double, Double = value };

    private static DescriptorValue Boolean(bool value) => new() { Type = DescriptorValueType.Boolean, Boolean = value };

    private static DescriptorValue Enum(string type, string value) => new() { Type = DescriptorValueType.Enum, EnumType = type, EnumValue = value };
}
