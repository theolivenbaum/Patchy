using System.Text;
using Patchy.Psd.IO;

namespace Patchy.Psd.Text;

/// <summary>
/// Reader for Photoshop 5.x type records (<c>tySh</c>): a fixed binary layout
/// with no EngineData. Only the text, fonts and transform are recovered.
/// </summary>
internal static class LegacyText
{
    public static TextLayerInfo? Parse(ReadOnlyMemory<byte> payload)
    {
        try
        {
            var reader = new BigEndianReader(payload);
            if (reader.ReadUInt16() != 1)
            {
                return null;
            }

            var transform = new double[6];
            for (var i = 0; i < 6; i++)
            {
                transform[i] = reader.ReadDouble();
            }

            _ = reader.ReadUInt16(); // font info version
            var faceCount = reader.ReadUInt16();
            var fonts = new List<string>();
            for (var i = 0; i < faceCount; i++)
            {
                _ = reader.ReadUInt16(); // mark
                _ = reader.ReadUInt32(); // font type
                var postScriptName = reader.ReadPascalString(1);
                _ = reader.ReadPascalString(1); // family
                _ = reader.ReadPascalString(1); // style
                _ = reader.ReadUInt16(); // script
                var axes = reader.ReadUInt32();
                reader.Skip((long)axes * 4);
                if (postScriptName.Length > 0 && !fonts.Contains(postScriptName))
                {
                    fonts.Add(postScriptName);
                }
            }

            var afterFonts = reader.Position;
            var text = TryReadStylesAndText(reader, skipVersionWord: false);
            if (text is null)
            {
                reader.Position = afterFonts;
                text = TryReadStylesAndText(reader, skipVersionWord: true);
            }

            if (text is null)
            {
                return null;
            }

            return new TextLayerInfo
            {
                Text = TextLayerInfo.NormalizeText(text),
                RawText = text,
                Transform = transform,
                Fonts = fonts,
            };
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }

    private static string? TryReadStylesAndText(BigEndianReader reader, bool skipVersionWord)
    {
        try
        {
            if (skipVersionWord)
            {
                reader.Skip(2);
            }

            var styleCount = reader.ReadUInt16();
            reader.Skip(styleCount * 26L);
            _ = reader.ReadUInt16(); // text type
            _ = reader.ReadUInt32(); // scaling
            var characterCount = reader.ReadUInt32();
            reader.Skip(16); // placement and selection
            var lineCount = reader.ReadUInt16();
            var builder = new StringBuilder();
            long units = 0;
            for (var line = 0; line < lineCount; line++)
            {
                var count = reader.ReadUInt32();
                units += count;
                if (units > characterCount || count > (uint)(reader.Remaining / 4))
                {
                    return null;
                }

                reader.Skip(4); // orientation, alignment
                for (var i = 0u; i < count; i++)
                {
                    builder.Append((char)reader.ReadUInt16());
                    _ = reader.ReadUInt16(); // style mark
                }

                if (line < lineCount - 1 && (builder.Length == 0 || builder[^1] != '\r'))
                {
                    builder.Append('\r');
                }
            }

            return builder.ToString();
        }
        catch (PsdFormatException)
        {
            return null;
        }
    }
}
