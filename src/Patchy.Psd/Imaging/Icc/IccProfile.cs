using System.Buffers.Binary;
using System.Text;

namespace Patchy.Psd.Imaging.Icc;

/// <summary>A CIE XYZ triple (Y of the PCS white is 1).</summary>
internal readonly record struct IccXyz(double X, double Y, double Z);

/// <summary>ICC rendering intents (header and tag numbering).</summary>
internal enum IccRenderingIntent
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
}

/// <summary>
/// A parsed ICC profile (v2 or v4): the header, the tag table, and the tags a
/// device-to-sRGB conversion needs: colorants, white point, <c>chad</c>, the
/// RGB and gray tone curves, and the <c>A2Bx</c>/<c>B2Ax</c> LUTs. Untrusted
/// input: every offset is bounds-checked and a damaged tag is dropped, so
/// <see cref="Parse"/> either returns a profile or null, never throws.
/// </summary>
internal sealed class IccProfile
{
    public const uint ClassInput = 0x73636E72; // 'scnr'
    public const uint ClassDisplay = 0x6D6E7472; // 'mntr'
    public const uint ClassOutput = 0x70727472; // 'prtr'
    public const uint ClassColorSpace = 0x73706163; // 'spac'
    public const uint SpaceRgb = 0x52474220; // 'RGB '
    public const uint SpaceGray = 0x47524159; // 'GRAY'
    public const uint SpaceCmyk = 0x434D594B; // 'CMYK'
    public const uint SpaceLab = 0x4C616220; // 'Lab '
    public const uint SpaceXyz = 0x58595A20; // 'XYZ '

    private const int MaxTags = 1024;

    private readonly IccLut?[] _aToB = new IccLut?[3];
    private readonly IccLut?[] _bToA = new IccLut?[3];

    private IccProfile()
    {
    }

    public int MajorVersion { get; private set; }

    public int MinorVersion { get; private set; }

    /// <summary>Header version word, e.g. 0x02100000 for 2.1.</summary>
    public uint EncodedVersion { get; private set; }

    public uint DeviceClass { get; private set; }

    public uint ColorSpace { get; private set; }

    public uint Pcs { get; private set; }

    public IccRenderingIntent HeaderIntent { get; private set; }

    public string Description { get; private set; } = "";

    /// <summary>Tag signatures in table order.</summary>
    public IReadOnlyList<string> TagSignatures { get; private set; } = [];

    public IccXyz? MediaWhitePoint { get; private set; }

    /// <summary>The <c>chad</c> chromatic adaptation matrix (row major), when present.</summary>
    public double[]? ChromaticAdaptation { get; private set; }

    public IccXyz? RedColorant { get; private set; }

    public IccXyz? GreenColorant { get; private set; }

    public IccXyz? BlueColorant { get; private set; }

    public IccCurve? RedTrc { get; private set; }

    public IccCurve? GreenTrc { get; private set; }

    public IccCurve? BlueTrc { get; private set; }

    public IccCurve? GrayTrc { get; private set; }

    public bool IsMatrixShaper => ColorSpace == SpaceRgb
        ? RedColorant is not null && GreenColorant is not null && BlueColorant is not null && RedTrc is not null && GreenTrc is not null && BlueTrc is not null
        : ColorSpace == SpaceGray && GrayTrc is not null;

    /// <summary>The device-to-PCS LUT for an intent tag (0..2), without fallback.</summary>
    public IccLut? GetAToB(int index) => (uint)index < 3 ? _aToB[index] : null;

    /// <summary>The PCS-to-device LUT for an intent tag (0..2), without fallback.</summary>
    public IccLut? GetBToA(int index) => (uint)index < 3 ? _bToA[index] : null;

    /// <summary>
    /// The device-to-PCS LUT Little CMS picks for <paramref name="intent"/>:
    /// <c>A2B0/1/2</c> by intent (absolute uses <c>A2B1</c>), falling back to
    /// <c>A2B0</c>; null when the profile has no LUT (matrix/TRC only).
    /// </summary>
    public IccLut? InputLut(IccRenderingIntent intent)
    {
        var index = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
        return _aToB[index] ?? _aToB[0];
    }

    /// <summary>The PCS-to-device LUT for an intent, falling back to <c>B2A0</c>.</summary>
    public IccLut? OutputLut(IccRenderingIntent intent)
    {
        var index = intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent;
        return _bToA[index] ?? _bToA[0];
    }

    public int DeviceChannels => ColorSpace switch
    {
        SpaceGray => 1,
        SpaceRgb or SpaceLab or SpaceXyz => 3,
        SpaceCmyk => 4,
        _ => ColorSpaceChannels(ColorSpace),
    };

    public static string SignatureToString(uint signature)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, signature);
        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>Parses profile bytes; null when they are not a usable ICC profile.</summary>
    public static IccProfile? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 132 || BinaryPrimitives.ReadUInt32BigEndian(data[36..]) != 0x61637370) // 'acsp'
        {
            return null;
        }

        // Trust the declared size only when it fits; some writers pad the resource.
        var declared = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (declared >= 132 && declared < data.Length)
        {
            data = data[..(int)declared];
        }

        var profile = new IccProfile
        {
            EncodedVersion = BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            MajorVersion = data[8],
            MinorVersion = data[9] >> 4,
            DeviceClass = BinaryPrimitives.ReadUInt32BigEndian(data[12..]),
            ColorSpace = BinaryPrimitives.ReadUInt32BigEndian(data[16..]),
            Pcs = BinaryPrimitives.ReadUInt32BigEndian(data[20..]),
            HeaderIntent = (IccRenderingIntent)(BinaryPrimitives.ReadUInt32BigEndian(data[64..]) & 3),
        };

        var count = BinaryPrimitives.ReadUInt32BigEndian(data[128..]);
        if (count > MaxTags || 132 + (count * 12) > data.Length)
        {
            return null;
        }

        var signatures = new List<string>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            var entry = data.Slice(132 + (i * 12), 12);
            var signature = BinaryPrimitives.ReadUInt32BigEndian(entry);
            var offset = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            var size = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            signatures.Add(SignatureToString(signature));
            if (size < 8 || offset > data.Length || size > data.Length - offset)
            {
                continue;
            }

            try
            {
                profile.ReadTag(SignatureToString(signature), data.Slice((int)offset, (int)size));
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                // A tag whose internal offsets point outside it is dropped.
            }
        }

        profile.TagSignatures = signatures;
        return profile;
    }

    private void ReadTag(string signature, ReadOnlySpan<byte> tag)
    {
        switch (signature)
        {
            case "desc":
                Description = ReadText(tag) ?? Description;
                break;
            case "wtpt":
                MediaWhitePoint = ReadXyz(tag);
                break;
            case "rXYZ":
                RedColorant = ReadXyz(tag);
                break;
            case "gXYZ":
                GreenColorant = ReadXyz(tag);
                break;
            case "bXYZ":
                BlueColorant = ReadXyz(tag);
                break;
            case "rTRC":
                RedTrc = ReadCurve(tag, out _);
                break;
            case "gTRC":
                GreenTrc = ReadCurve(tag, out _);
                break;
            case "bTRC":
                BlueTrc = ReadCurve(tag, out _);
                break;
            case "kTRC":
                GrayTrc = ReadCurve(tag, out _);
                break;
            case "chad":
                if (TypeOf(tag) == "sf32" && tag.Length >= 44)
                {
                    var matrix = new double[9];
                    for (var i = 0; i < 9; i++)
                    {
                        matrix[i] = IccLut.ReadS15Fixed16(tag, 8 + (i * 4));
                    }

                    ChromaticAdaptation = matrix;
                }

                break;
            case "A2B0" or "A2B1" or "A2B2":
                _aToB[signature[3] - '0'] = ReadLut(tag, deviceToPcs: true);
                break;
            case "B2A0" or "B2A1" or "B2A2":
                _bToA[signature[3] - '0'] = ReadLut(tag, deviceToPcs: false);
                break;
        }
    }

    private IccLut? ReadLut(ReadOnlySpan<byte> tag, bool deviceToPcs)
    {
        var lut = IccLut.Parse(tag);
        var device = DeviceChannels;
        if (lut is null || device == 0)
        {
            return null;
        }

        // The device side must match the header color space and the PCS side has three channels.
        var (deviceSide, pcsSide) = deviceToPcs ? (lut.InputChannels, lut.OutputChannels) : (lut.OutputChannels, lut.InputChannels);
        return deviceSide == device && pcsSide == 3 ? lut : null;
    }

    private static string TypeOf(ReadOnlySpan<byte> tag) => tag.Length >= 4 ? Encoding.ASCII.GetString(tag[..4]) : "";

    private static IccXyz? ReadXyz(ReadOnlySpan<byte> tag)
    {
        if (TypeOf(tag) != "XYZ " || tag.Length < 20)
        {
            return null;
        }

        return new IccXyz(IccLut.ReadS15Fixed16(tag, 8), IccLut.ReadS15Fixed16(tag, 12), IccLut.ReadS15Fixed16(tag, 16));
    }

    /// <summary>Reads a <c>curv</c> or <c>para</c> element; <paramref name="consumed"/> is its unpadded byte length.</summary>
    internal static IccCurve? ReadCurve(ReadOnlySpan<byte> tag, out int consumed)
    {
        consumed = 0;
        if (tag.Length < 12)
        {
            return null;
        }

        switch (TypeOf(tag))
        {
            case "curv":
                {
                    var count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                    if (count > (tag.Length - 12) / 2)
                    {
                        return null;
                    }

                    consumed = 12 + ((int)count * 2);
                    if (count == 0)
                    {
                        return IccCurve.Identity;
                    }

                    if (count == 1)
                    {
                        return IccCurve.FromGamma(BinaryPrimitives.ReadUInt16BigEndian(tag[12..]) / 256.0);
                    }

                    var table = new float[count];
                    for (var i = 0; i < table.Length; i++)
                    {
                        table[i] = BinaryPrimitives.ReadUInt16BigEndian(tag[(12 + (i * 2))..]) / 65535f;
                    }

                    return IccCurve.FromTable(table);
                }

            case "para":
                {
                    var function = BinaryPrimitives.ReadUInt16BigEndian(tag[8..]);
                    int[] counts = [1, 3, 4, 5, 7];
                    if (function >= counts.Length || 12 + (counts[function] * 4) > tag.Length)
                    {
                        return null;
                    }

                    Span<double> parameters = stackalloc double[7];
                    for (var i = 0; i < counts[function]; i++)
                    {
                        parameters[i] = IccLut.ReadS15Fixed16(tag, 12 + (i * 4));
                    }

                    consumed = 12 + (counts[function] * 4);
                    return IccCurve.FromParametric(function, parameters);
                }

            default:
                return null;
        }
    }

    private static string? ReadText(ReadOnlySpan<byte> tag)
    {
        switch (TypeOf(tag))
        {
            case "desc":
                {
                    if (tag.Length < 12)
                    {
                        return null;
                    }

                    var count = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                    var length = (int)Math.Min(count, (uint)(tag.Length - 12));
                    return Encoding.ASCII.GetString(tag.Slice(12, length)).TrimEnd('\0');
                }

            case "text":
                return Encoding.ASCII.GetString(tag[8..]).TrimEnd('\0');
            case "mluc":
                {
                    if (tag.Length < 16)
                    {
                        return null;
                    }

                    var records = BinaryPrimitives.ReadUInt32BigEndian(tag[8..]);
                    var recordSize = BinaryPrimitives.ReadUInt32BigEndian(tag[12..]);
                    if (recordSize < 12 || records == 0)
                    {
                        return null;
                    }

                    string? first = null;
                    for (var i = 0L; i < records; i++)
                    {
                        var at = 16 + (i * recordSize);
                        if (at + 12 > tag.Length)
                        {
                            break;
                        }

                        var language = Encoding.ASCII.GetString(tag.Slice((int)at, 2));
                        var length = BinaryPrimitives.ReadUInt32BigEndian(tag[(int)(at + 4)..]);
                        var offset = BinaryPrimitives.ReadUInt32BigEndian(tag[(int)(at + 8)..]);
                        if (offset > tag.Length || length > tag.Length - offset)
                        {
                            continue;
                        }

                        var text = Encoding.BigEndianUnicode.GetString(tag.Slice((int)offset, (int)length & ~1)).TrimEnd('\0');
                        first ??= text;
                        if (language == "en")
                        {
                            return text;
                        }
                    }

                    return first;
                }

            default:
                return null;
        }
    }

    private static int ColorSpaceChannels(uint space)
    {
        // 'nCLR' spaces (2CLR..FCLR) carry their channel count in the first character.
        var first = (char)(space >> 24);
        if ((space & 0x00FFFFFF) == 0x00434C52)
        {
            return first is >= '2' and <= '9' ? first - '0' : first is >= 'A' and <= 'F' ? first - 'A' + 10 : 0;
        }

        return 0;
    }
}
