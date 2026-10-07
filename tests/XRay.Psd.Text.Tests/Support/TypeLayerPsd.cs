using System.Globalization;
using System.Text;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Text.Tests.Support;

/// <summary>
/// Writes a document with one Photoshop 6+ type layer (<c>TySh</c> with EngineData) whose
/// pixel data is missing, as files written by tools that do not rasterize text are.
/// </summary>
internal static class TypeLayerPsd
{
    public static byte[] Build(string text, string font, double size, (double Tx, double Ty) anchor, int justification = 0, (byte R, byte G, byte B) color = default, int width = 200, int height = 100)
    {
        var raw = text.Replace("\n", "\r", StringComparison.Ordinal) + "\r";
        var engine = EngineData(raw, font, size, justification, color);
        var layer = new BuilderLayer { Name = "Type", Rect = default };
        layer.Blocks.Add(("TySh", TypeToolBlock(text.Replace("\n", "\r", StringComparison.Ordinal), anchor, engine)));
        var builder = new PsdBuilder { Width = width, Height = height };
        builder.Layers.Add(layer);
        return builder.Build();
    }

    private static byte[] TypeToolBlock(string text, (double Tx, double Ty) anchor, byte[] engineData)
    {
        var writer = new PsdBuilder.Writer();
        writer.U16(1);
        foreach (var value in new[] { 1, 0, 0, 1, anchor.Tx, anchor.Ty })
        {
            writer.F64(value);
        }

        writer.U16(50);
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("TxLr");
        writer.U32(3);
        writer.DescriptorId("Txt ");
        writer.Ascii("TEXT");
        writer.UnicodeString(text);
        writer.DescriptorId("Ornt");
        writer.Ascii("enum");
        writer.DescriptorId("Ornt");
        writer.DescriptorId("Hrzn");
        writer.DescriptorId("EngineData");
        writer.Ascii("tdta");
        writer.U32((uint)engineData.Length);
        writer.Bytes(engineData);

        writer.U16(1);
        writer.U32(16);
        writer.UnicodeString(string.Empty);
        writer.DescriptorId("warp");
        writer.U32(1);
        writer.DescriptorId("warpStyle");
        writer.Ascii("enum");
        writer.DescriptorId("warpStyle");
        writer.DescriptorId("warpNone");
        writer.Zeros(16);
        return writer.ToArray();
    }

    private static byte[] EngineData(string raw, string font, double size, int justification, (byte R, byte G, byte B) color)
    {
        string N(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
        var fill = $"/FillColor << /Type 1 /Values [ 1.0 {N(color.R / 255.0)} {N(color.G / 255.0)} {N(color.B / 255.0)} ] >>";
        var template =
            "\n\n<<\n\t/EngineDict << /Editor << /Text $0 >>" +
            $" /ParagraphRun << /RunArray [ << /ParagraphSheet << /Properties << /Justification {justification} >> >> >> ] /RunLengthArray [ {raw.Length} ] >>" +
            $" /StyleRun << /RunArray [ << /StyleSheet << /StyleSheetData << /Font 0 /FontSize {N(size)} /AutoLeading true {fill} >> >> >> ] /RunLengthArray [ {raw.Length} ] >> /AntiAlias 4 >>" +
            " /ResourceDict << /FontSet [ << /Name $1 /FontType 1 >> ] /TheNormalStyleSheet 0 /TheNormalParagraphSheet 0" +
            " /StyleSheetSet [ << /StyleSheetData << /Font 0 /FontSize 12 >> >> ]" +
            " /ParagraphSheetSet [ << /Properties << /Justification 0 >> >> ] >> >>\n";
        var strings = new[] { raw, font };
        var output = new List<byte>();
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '$' && i + 1 < template.Length && char.IsAsciiDigit(template[i + 1]))
            {
                output.AddRange(EngineString(strings[template[i + 1] - '0']));
                i++;
            }
            else
            {
                output.Add((byte)template[i]);
            }
        }

        return [.. output];
    }

    private static byte[] EngineString(string text)
    {
        var output = new List<byte> { (byte)'(' };
        foreach (var b in new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes(text)))
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\')
            {
                output.Add((byte)'\\');
            }

            output.Add(b);
        }

        output.Add((byte)')');
        return [.. output];
    }
}
