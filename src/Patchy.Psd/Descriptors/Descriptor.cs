using System.Text;
using Patchy.Psd.IO;

namespace Patchy.Psd.Descriptors;

/// <summary>Value types an Action Manager descriptor can hold.</summary>
public enum DescriptorValueType
{
    Boolean,
    Integer,
    LargeInteger,
    Double,
    UnitFloat,
    UnitFloats,
    String,
    Enum,
    Object,
    List,
    ObjectArray,
    Class,
    Reference,
    RawData,
    Alias,
}

/// <summary>One reference item of an <c>obj </c> descriptor value.</summary>
public sealed record DescriptorReferenceItem(string Form, string ClassName, string ClassId, string? Key, string? Value, uint Number);

/// <summary>A value inside a <see cref="Descriptor"/>.</summary>
public sealed class DescriptorValue
{
    public DescriptorValueType Type { get; init; }

    public bool Boolean { get; init; }

    public long Integer { get; init; }

    public double Double { get; init; }

    /// <summary>Unit key for unit floats (<c>#Pxl</c>, <c>#Prc</c>, <c>#Ang</c>, <c>#Pnt</c> and so on).</summary>
    public string? Unit { get; init; }

    public double[]? Doubles { get; init; }

    public string? String { get; init; }

    /// <summary>Enum type ID (for <see cref="DescriptorValueType.Enum"/>) or class ID.</summary>
    public string? EnumType { get; init; }

    public string? EnumValue { get; init; }

    public Descriptor? Object { get; init; }

    public IReadOnlyList<DescriptorValue>? List { get; init; }

    public IReadOnlyList<DescriptorReferenceItem>? Reference { get; init; }

    public ReadOnlyMemory<byte> Raw { get; init; }

    /// <summary>Numeric view: integers, doubles and unit floats.</summary>
    public double AsNumber() => Type switch
    {
        DescriptorValueType.Integer or DescriptorValueType.LargeInteger => Integer,
        DescriptorValueType.Double or DescriptorValueType.UnitFloat => Double,
        DescriptorValueType.Boolean => Boolean ? 1 : 0,
        _ => double.NaN,
    };

    public override string ToString() => Type switch
    {
        DescriptorValueType.Boolean => Boolean ? "true" : "false",
        DescriptorValueType.Integer or DescriptorValueType.LargeInteger => Integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DescriptorValueType.Double => Double.ToString(System.Globalization.CultureInfo.InvariantCulture),
        DescriptorValueType.UnitFloat => $"{Double.ToString(System.Globalization.CultureInfo.InvariantCulture)} {Unit}",
        DescriptorValueType.String => $"\"{String}\"",
        DescriptorValueType.Enum => $"{EnumType}.{EnumValue}",
        DescriptorValueType.Object => Object?.ToString() ?? "{}",
        DescriptorValueType.List => $"[{List?.Count ?? 0} items]",
        _ => Type.ToString(),
    };
}

/// <summary>
/// A parsed Photoshop Action Manager descriptor (<c>Objc</c>). Descriptors hold
/// text-layer data, layer effects, fill settings, smart-object placement and
/// most other modern PSD metadata. Keys keep file order.
/// </summary>
public sealed class Descriptor
{
    private readonly List<KeyValuePair<string, DescriptorValue>> _items = [];

    public string Name { get; init; } = string.Empty;

    public string ClassId { get; init; } = string.Empty;

    public IReadOnlyList<KeyValuePair<string, DescriptorValue>> Items => _items;

    public DescriptorValue? this[string key] => TryGet(key, out var value) ? value : null;

    internal void Add(string key, DescriptorValue value) => _items.Add(new(key, value));

    public bool TryGet(string key, out DescriptorValue value)
    {
        foreach (var item in _items)
        {
            if (item.Key == key)
            {
                value = item.Value;
                return true;
            }
        }

        value = null!;
        return false;
    }

    public string? GetString(string key) => TryGet(key, out var v) && v.Type == DescriptorValueType.String ? v.String : null;

    public Descriptor? GetObject(string key) => TryGet(key, out var v) ? v.Object : null;

    public IReadOnlyList<DescriptorValue>? GetList(string key) => TryGet(key, out var v) ? v.List : null;

    public double GetNumber(string key, double fallback = double.NaN)
    {
        if (!TryGet(key, out var v))
        {
            return fallback;
        }

        var number = v.AsNumber();
        return double.IsNaN(number) ? fallback : number;
    }

    public bool GetBoolean(string key, bool fallback = false) => TryGet(key, out var v) && v.Type == DescriptorValueType.Boolean ? v.Boolean : fallback;

    public string? GetEnum(string key) => TryGet(key, out var v) && v.Type == DescriptorValueType.Enum ? v.EnumValue : null;

    /// <summary>Reads an RGB color object (<c>RGBC</c> with <c>Rd  </c>/<c>Grn </c>/<c>Bl  </c>), returning null for other models.</summary>
    public PsdColor? GetColor(string key)
    {
        var color = GetObject(key);
        return color is null ? null : ColorFromDescriptor(color);
    }

    internal static PsdColor? ColorFromDescriptor(Descriptor color)
    {
        static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

        if (color.TryGet("Rd  ", out _) || color.TryGet("redFloat", out _))
        {
            if (color.TryGet("redFloat", out var rf))
            {
                return new PsdColor(Clamp(rf.AsNumber() * 255), Clamp(color.GetNumber("greenFloat", 0) * 255), Clamp(color.GetNumber("blueFloat", 0) * 255));
            }

            return new PsdColor(Clamp(color.GetNumber("Rd  ", 0)), Clamp(color.GetNumber("Grn ", 0)), Clamp(color.GetNumber("Bl  ", 0)));
        }

        if (color.TryGet("Gry ", out var gray))
        {
            var g = Clamp(255 - (gray.AsNumber() * 2.55));
            return new PsdColor(g, g, g);
        }

        if (color.TryGet("Cyn ", out _))
        {
            var c = color.GetNumber("Cyn ", 0) / 100.0;
            var m = color.GetNumber("Mgnt", 0) / 100.0;
            var y = color.GetNumber("Ylw ", 0) / 100.0;
            var k = color.GetNumber("Blck", 0) / 100.0;
            return new PsdColor(Clamp(255 * (1 - c) * (1 - k)), Clamp(255 * (1 - m) * (1 - k)), Clamp(255 * (1 - y) * (1 - k)));
        }

        if (color.TryGet("H   ", out _))
        {
            var h = color.GetNumber("H   ", 0) / 360.0;
            var s = color.GetNumber("Strt", 0) / 100.0;
            var b = color.GetNumber("Brgh", 0) / 100.0;
            var (r, g, bl) = HsbToRgb(h, s, b);
            return new PsdColor(Clamp(r * 255), Clamp(g * 255), Clamp(bl * 255));
        }

        if (color.TryGet("Lmnc", out _))
        {
            var (r, g, b) = Imaging.ColorSpaces.LabToSrgb(color.GetNumber("Lmnc", 0), color.GetNumber("A   ", 0), color.GetNumber("B   ", 0));
            return new PsdColor(Clamp(r * 255), Clamp(g * 255), Clamp(b * 255));
        }

        return null;
    }

    private static (double R, double G, double B) HsbToRgb(double h, double s, double v)
    {
        h = (h - Math.Floor(h)) * 6;
        var i = (int)Math.Floor(h) % 6;
        var f = h - Math.Floor(h);
        var p = v * (1 - s);
        var q = v * (1 - (s * f));
        var t = v * (1 - (s * (1 - f)));
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        Append(builder, 0);
        return builder.ToString();
    }

    private void Append(StringBuilder builder, int indent)
    {
        builder.Append(ClassId).Append(" {").AppendLine();
        foreach (var (key, value) in _items)
        {
            builder.Append(' ', (indent + 1) * 2).Append(key).Append(": ");
            AppendValue(builder, value, indent + 1);
            builder.AppendLine();
        }

        builder.Append(' ', indent * 2).Append('}');
    }

    private static void AppendValue(StringBuilder builder, DescriptorValue value, int indent)
    {
        if (value.Type is DescriptorValueType.Object or DescriptorValueType.ObjectArray && value.Object is not null)
        {
            value.Object.Append(builder, indent);
        }
        else if (value.Type == DescriptorValueType.List && value.List is not null)
        {
            builder.Append('[').AppendLine();
            foreach (var item in value.List)
            {
                builder.Append(' ', (indent + 1) * 2);
                AppendValue(builder, item, indent + 1);
                builder.AppendLine();
            }

            builder.Append(' ', indent * 2).Append(']');
        }
        else
        {
            builder.Append(value);
        }
    }

    /// <summary>Reads a descriptor body (no version prefix).</summary>
    internal static Descriptor Read(BigEndianReader reader) => DescriptorReader.ReadDescriptor(reader, 0);

    /// <summary>Reads a u32 descriptor version (normally 16) followed by a descriptor.</summary>
    internal static Descriptor ReadVersioned(BigEndianReader reader)
    {
        var version = reader.ReadUInt32();
        if (version != 16)
        {
            throw new PsdFormatException($"Unsupported descriptor version {version}.");
        }

        return Read(reader);
    }
}

internal static class DescriptorReader
{
    // Real Photoshop data nests a handful of levels; a crafted file could nest
    // one level per dozen bytes and exhaust the stack.
    private const int MaxDepth = 64;

    public static Descriptor ReadDescriptor(BigEndianReader reader, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new PsdFormatException("Descriptor nesting is too deep.");
        }

        var name = reader.ReadUnicodeString();
        var classId = ReadId(reader);
        var descriptor = new Descriptor { Name = name, ClassId = classId };
        var count = reader.ReadUInt32();
        CheckCount(count, 8, reader);
        for (var i = 0u; i < count; i++)
        {
            var key = ReadId(reader);
            var type = reader.ReadSignature();
            descriptor.Add(key, ReadValue(reader, type, depth + 1));
        }

        return descriptor;
    }

    public static string ReadId(BigEndianReader reader)
    {
        var length = reader.ReadUInt32();
        if (length == 0)
        {
            return reader.ReadSignature();
        }

        if (length > (uint)reader.Remaining)
        {
            throw new PsdFormatException("Descriptor ID length exceeds the remaining data.");
        }

        return Encoding.Latin1.GetString(reader.ReadSpan((int)length));
    }

    private static void CheckCount(uint count, int minimumItemBytes, BigEndianReader reader)
    {
        if ((ulong)count * (ulong)minimumItemBytes > (ulong)reader.Remaining)
        {
            throw new PsdFormatException("Descriptor item count exceeds the remaining data.");
        }
    }

    public static DescriptorValue ReadValue(BigEndianReader reader, string type, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new PsdFormatException("Descriptor nesting is too deep.");
        }

        switch (type)
        {
            case "bool":
                return new DescriptorValue { Type = DescriptorValueType.Boolean, Boolean = reader.ReadByte() != 0 };
            case "long":
                return new DescriptorValue { Type = DescriptorValueType.Integer, Integer = reader.ReadInt32() };
            case "comp":
                return new DescriptorValue { Type = DescriptorValueType.LargeInteger, Integer = reader.ReadInt64() };
            case "doub":
                return new DescriptorValue { Type = DescriptorValueType.Double, Double = reader.ReadDouble() };
            case "UntF":
                {
                    var unit = reader.ReadSignature();
                    return new DescriptorValue { Type = DescriptorValueType.UnitFloat, Unit = unit, Double = reader.ReadDouble() };
                }

            case "UnFl":
                {
                    var unit = reader.ReadSignature();
                    var count = reader.ReadUInt32();
                    CheckCount(count, 8, reader);
                    var values = new double[count];
                    for (var i = 0; i < values.Length; i++)
                    {
                        values[i] = reader.ReadDouble();
                    }

                    return new DescriptorValue { Type = DescriptorValueType.UnitFloats, Unit = unit, Doubles = values };
                }

            case "TEXT":
                return new DescriptorValue { Type = DescriptorValueType.String, String = reader.ReadUnicodeString() };
            case "enum":
                {
                    var enumType = ReadId(reader);
                    return new DescriptorValue { Type = DescriptorValueType.Enum, EnumType = enumType, EnumValue = ReadId(reader) };
                }

            case "Objc":
            case "GlbO":
                return new DescriptorValue { Type = DescriptorValueType.Object, Object = ReadDescriptor(reader, depth + 1) };
            case "VlLs":
                {
                    var count = reader.ReadUInt32();
                    CheckCount(count, 4, reader);
                    var list = new List<DescriptorValue>((int)Math.Min(count, 4096));
                    for (var i = 0u; i < count; i++)
                    {
                        list.Add(ReadValue(reader, reader.ReadSignature(), depth + 1));
                    }

                    return new DescriptorValue { Type = DescriptorValueType.List, List = list };
                }

            case "ObAr":
                {
                    var count = reader.ReadUInt32();
                    return new DescriptorValue { Type = DescriptorValueType.ObjectArray, Integer = count, Object = ReadDescriptor(reader, depth + 1) };
                }

            case "tdta":
                {
                    var length = reader.ReadUInt32();
                    return new DescriptorValue { Type = DescriptorValueType.RawData, Raw = reader.ReadMemory((long)length) };
                }

            case "alis":
                {
                    var length = reader.ReadUInt32();
                    return new DescriptorValue { Type = DescriptorValueType.Alias, Raw = reader.ReadMemory((long)length) };
                }

            case "Pth ":
                {
                    var length = reader.ReadUInt32();
                    return new DescriptorValue { Type = DescriptorValueType.Alias, Raw = reader.ReadMemory((long)length) };
                }

            case "type":
            case "GlbC":
                {
                    var className = reader.ReadUnicodeString();
                    return new DescriptorValue { Type = DescriptorValueType.Class, String = className, EnumValue = ReadId(reader) };
                }

            case "obj ":
                {
                    var count = reader.ReadUInt32();
                    CheckCount(count, 4, reader);
                    var items = new List<DescriptorReferenceItem>((int)count);
                    for (var i = 0u; i < count; i++)
                    {
                        var form = reader.ReadSignature();
                        var className = reader.ReadUnicodeString();
                        var classId = ReadId(reader);
                        string? key = null;
                        string? value = null;
                        uint number = 0;
                        switch (form)
                        {
                            case "prop":
                                key = ReadId(reader);
                                break;
                            case "Enmr":
                                key = ReadId(reader);
                                value = ReadId(reader);
                                break;
                            case "rele":
                            case "Idnt":
                            case "indx":
                                number = reader.ReadUInt32();
                                break;
                            case "name":
                                value = reader.ReadUnicodeString();
                                break;
                            case "Clss":
                                break;
                            default:
                                throw new PsdFormatException($"Unsupported descriptor reference form '{form}'.");
                        }

                        items.Add(new DescriptorReferenceItem(form, className, classId, key, value, number));
                    }

                    return new DescriptorValue { Type = DescriptorValueType.Reference, Reference = items };
                }

            default:
                throw new PsdFormatException($"Unsupported descriptor value type '{type}'.");
        }
    }
}
