using System.Buffers.Binary;
using XRay.Psd.Layers;

namespace XRay.Psd.Text;

/// <summary>
/// Connects type layers to their objects in the document's Txt2 block at load
/// time and fills the gaps of the layer's own record from them: text missing
/// from the TySh descriptor and EngineData, style and paragraph runs when the
/// EngineData does not decode, paragraph direction and lists (which Photoshop
/// stores only in Txt2), and whole layers whose TySh descriptor is damaged.
/// The layer's own values win wherever both exist.
/// </summary>
internal static class TextEngineResolver
{
    /// <summary>Parses the document's Txt2 block, or returns null when it has none or it does not decode.</summary>
    public static TextEngineBlock? ParseBlock(PsdDocument document)
    {
        var block = document.GetGlobalTaggedBlock("Txt2");
        return block is null ? null : TextEngineBlock.Parse(block.Data.Span, PixelsPerPoint(document), document.Colors);
    }

    public static void Apply(PsdDocument document)
    {
        if (!document.Layers.Any(l => l.Kind == PsdLayerKind.Text) || document.GetGlobalTaggedBlock("Txt2") is null)
        {
            return;
        }

        var block = document.TextEngine;
        if (block is null)
        {
            return;
        }

        foreach (var layer in document.Layers)
        {
            if (layer.Kind != PsdLayerKind.Text)
            {
                continue;
            }

            if (layer.Text is null)
            {
                // A damaged TySh: recover the TextIndex and transform from the raw bytes.
                if (layer.GetTaggedBlock("TySh") is { } tySh)
                {
                    var (transform, textIndex) = TextLayerInfo.Salvage(tySh.Data.Span);
                    if (textIndex is int index && block.GetObject(index) is { } salvaged)
                    {
                        layer.Text = FromObject(salvaged, transform);
                    }
                }

                continue;
            }

            if (layer.Text.TextIndex is int objectIndex && block.GetObject(objectIndex) is { } textObject)
            {
                layer.Text = Merge(layer.Text, textObject);
            }
        }
    }

    /// <summary>A layer's info built from its Txt2 object alone.</summary>
    internal static TextLayerInfo FromObject(TextEngineObject textObject, double[]? transform)
    {
        var frame = textObject.Frame;
        return new TextLayerInfo
        {
            Text = textObject.Text,
            RawText = textObject.RawText,
            Transform = transform ?? [1, 0, 0, 1, 0, 0],
            Orientation = frame?.Orientation ?? TextOrientation.Horizontal,
            IsParagraphText = frame?.ShapeKind == TextShapeKind.Box,
            ShapeKind = frame?.ShapeKind ?? TextShapeKind.Point,
            StyleRuns = textObject.StyleRuns,
            Paragraphs = textObject.Paragraphs,
            Fonts = textObject.Fonts,
            TextIndex = textObject.Index,
            TextEngineObject = textObject,
            BoxBounds = frame?.ShapeKind == TextShapeKind.Box ? frame.Bounds : null,
            Origin = TextDataOrigin.TextEngineBlock,
        };
    }

    private static TextLayerInfo Merge(TextLayerInfo info, TextEngineObject textObject)
    {
        string? rawText = null;
        var sameText = info.Text == textObject.Text;
        if (info.Text.Length == 0 && textObject.Text.Length > 0)
        {
            rawText = textObject.RawText;
            sameText = true;
        }

        IReadOnlyList<TextStyleRun>? runs = null;
        IReadOnlyList<string>? fonts = null;
        IReadOnlyList<TextParagraph>? paragraphs = null;
        if (sameText)
        {
            if (info.StyleRuns.Count == 0 && textObject.StyleRuns.Count > 0)
            {
                runs = textObject.StyleRuns;
                fonts = textObject.Fonts;
            }

            if (info.Paragraphs.Count == 0 && textObject.Paragraphs.Count > 0)
            {
                paragraphs = textObject.Paragraphs;
            }
            else if (info.Paragraphs.Count > 0)
            {
                paragraphs = MergeParagraphs(info.Paragraphs, textObject.Paragraphs);
            }
        }

        var used = rawText is not null || runs is not null || paragraphs is not null;
        return info.With(
            rawText: rawText,
            styleRuns: runs,
            paragraphs: paragraphs,
            fonts: fonts,
            textEngineObject: textObject,
            origin: used ? TextDataOrigin.TextEngineBlock : TextDataOrigin.None);
    }

    /// <summary>Takes direction and list settings, which TySh does not store, from the Txt2 paragraph at the same start.</summary>
    private static IReadOnlyList<TextParagraph>? MergeParagraphs(IReadOnlyList<TextParagraph> own, IReadOnlyList<TextParagraph> engine)
    {
        List<TextParagraph>? merged = null;
        for (var i = 0; i < own.Count; i++)
        {
            var paragraph = own[i];
            var match = engine.FirstOrDefault(p => p.Start == paragraph.Start);
            if (match is null)
            {
                continue;
            }

            var direction = paragraph.Direction == TextDirection.Auto ? match.Direction : paragraph.Direction;
            var listStyle = paragraph.ListStyleIndex ?? match.ListStyleIndex;
            var listTier = paragraph.ListStyleIndex is null ? match.ListTier : paragraph.ListTier;
            if (direction == paragraph.Direction && listStyle == paragraph.ListStyleIndex && listTier == paragraph.ListTier)
            {
                continue;
            }

            merged ??= [.. own];
            merged[i] = paragraph with { Direction = direction, ListStyleIndex = listStyle, ListTier = listTier };
        }

        return merged;
    }

    /// <summary>Document resolution (resource 1005, 16.16 fixed pixels per inch) divided by 72.</summary>
    private static double PixelsPerPoint(PsdDocument document)
    {
        if (document.GetImageResource(ImageResourceIds.ResolutionInfo) is { Data.Length: >= 4 } resolution)
        {
            var ppi = BinaryPrimitives.ReadUInt32BigEndian(resolution.Data.Span) / 65536.0;
            if (ppi is > 1 and < 100000)
            {
                return ppi / 72.0;
            }
        }

        return 1;
    }
}
