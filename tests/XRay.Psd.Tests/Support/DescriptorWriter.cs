namespace XRay.Psd.Tests.Support;

/// <summary>
/// Builds Action Manager descriptors for synthetic tagged blocks. Values are given as
/// C# objects: <c>bool</c>, <c>int</c> (long), <c>double</c> (doub), <c>string</c> (TEXT),
/// <see cref="Unit"/>, <see cref="EnumValue"/>, nested <see cref="DescriptorWriter"/>
/// (Objc) and <c>object[]</c> (VlLs).
/// </summary>
internal sealed class DescriptorWriter(string classId)
{
    private readonly List<(string Key, object Value)> _items = [];

    public sealed record Unit(string Type, double Value);

    public sealed record EnumValue(string Type, string Value);

    public DescriptorWriter Add(string key, object value)
    {
        _items.Add((key, value));
        return this;
    }

    /// <summary>An RGBC color object.</summary>
    public static DescriptorWriter Rgb(byte r, byte g, byte b) =>
        new DescriptorWriter("RGBC").Add("Rd  ", (double)r).Add("Grn ", (double)g).Add("Bl  ", (double)b);

    public static Unit Percent(double value) => new("#Prc", value);

    public static Unit Pixels(double value) => new("#Pxl", value);

    public static EnumValue BlendMode(string value) => new("BlnM", value);

    /// <summary>Descriptor version 16 followed by the descriptor.</summary>
    public byte[] Versioned(params uint[] prefix)
    {
        var writer = new PsdBuilder.Writer();
        foreach (var value in prefix)
        {
            writer.U32(value);
        }

        writer.U32(16);
        Write(writer);
        return writer.ToArray();
    }

    /// <summary>A content key (<c>vscg</c>) followed by a versioned descriptor.</summary>
    public byte[] KeyedVersioned(string key)
    {
        var writer = new PsdBuilder.Writer();
        writer.Ascii(key);
        writer.U32(16);
        Write(writer);
        return writer.ToArray();
    }

    public void Write(PsdBuilder.Writer writer)
    {
        writer.UnicodeString(string.Empty);
        writer.DescriptorId(classId);
        writer.U32((uint)_items.Count);
        foreach (var (key, value) in _items)
        {
            writer.DescriptorId(key);
            WriteValue(writer, value);
        }
    }

    private static void WriteValue(PsdBuilder.Writer writer, object value)
    {
        switch (value)
        {
            case bool flag:
                writer.Ascii("bool");
                writer.U8((byte)(flag ? 1 : 0));
                break;
            case int number:
                writer.Ascii("long");
                writer.I32(number);
                break;
            case double number:
                writer.Ascii("doub");
                writer.F64(number);
                break;
            case string text:
                writer.Ascii("TEXT");
                writer.UnicodeString(text);
                break;
            case Unit unit:
                writer.Ascii("UntF");
                writer.Ascii(unit.Type);
                writer.F64(unit.Value);
                break;
            case EnumValue enumeration:
                writer.Ascii("enum");
                writer.DescriptorId(enumeration.Type);
                writer.DescriptorId(enumeration.Value);
                break;
            case DescriptorWriter nested:
                writer.Ascii("Objc");
                nested.Write(writer);
                break;
            case object[] list:
                writer.Ascii("VlLs");
                writer.U32((uint)list.Length);
                foreach (var item in list)
                {
                    WriteValue(writer, item);
                }

                break;
            default:
                throw new ArgumentException($"Unsupported descriptor value {value.GetType().Name}.");
        }
    }
}
