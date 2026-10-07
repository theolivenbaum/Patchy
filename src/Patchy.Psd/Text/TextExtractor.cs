using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;
using Patchy.Psd.Descriptors;
using Patchy.Psd.IO;
using Patchy.Psd.Layers;

namespace Patchy.Psd.Text;

/// <summary>Where a piece of extracted text came from.</summary>
public enum PsdTextKind
{
    /// <summary>Name of a pixel, text, shape, adjustment or smart-object layer.</summary>
    LayerName,

    /// <summary>Name of a layer group.</summary>
    GroupName,

    /// <summary>Content of a type layer.</summary>
    TextLayer,

    /// <summary>Name of a saved alpha or spot channel.</summary>
    ChannelName,

    /// <summary>Name of a saved path.</summary>
    PathName,

    /// <summary>Slice name, URL, message, alt text or cell text.</summary>
    Slice,

    /// <summary>Document metadata (XMP or IPTC title, description, keywords, headline, author, caption).</summary>
    Metadata,

    /// <summary>File name of an embedded or linked smart-object source.</summary>
    LinkedFileName,

    /// <summary>
    /// A text object of the document's <c>Txt2</c> block whose text no extracted type
    /// layer reports: one no layer references, or one whose text differs from its
    /// layer's own record (Photoshop displays the <c>Txt2</c> version).
    /// </summary>
    TextEngineObject,
}

/// <summary>One extracted string.</summary>
/// <param name="Kind">What the text is.</param>
/// <param name="Text">The text, paragraph breaks normalized to <c>\n</c>.</param>
/// <param name="Source">
/// Context: the layer path for layer items (<c>Group/Layer</c>), the field name
/// for metadata and slices. Items from embedded documents are prefixed with the
/// smart-object layer path and <c>&gt;</c>.
/// </param>
/// <param name="Layer">The layer the text belongs to, for items of the top-level document.</param>
/// <param name="Depth">0 for the top-level document, 1+ for text inside embedded smart objects.</param>
public sealed record PsdTextItem(PsdTextKind Kind, string Text, string Source, PsdLayer? Layer, int Depth)
{
    /// <summary>Style runs and fonts for <see cref="PsdTextKind.TextLayer"/> items.</summary>
    public TextLayerInfo? TextInfo { get; init; }
}

/// <summary>Options for <see cref="PsdDocument.ExtractText"/>.</summary>
public sealed class TextExtractionOptions
{
    public bool IncludeLayerNames { get; init; } = true;

    public bool IncludeTextLayers { get; init; } = true;

    public bool IncludeChannelNames { get; init; } = true;

    public bool IncludePathNames { get; init; } = true;

    public bool IncludeSlices { get; init; } = true;

    public bool IncludeMetadata { get; init; } = true;

    /// <summary>Include hidden layers (and layers inside hidden groups).</summary>
    public bool IncludeHiddenLayers { get; init; } = true;

    /// <summary>
    /// Report <c>Txt2</c> text objects that no extracted type layer covers as
    /// <see cref="PsdTextKind.TextEngineObject"/> items (requires <see cref="IncludeTextLayers"/>).
    /// </summary>
    public bool IncludeTextEngineObjects { get; init; } = true;

    /// <summary>Recurse into embedded PSD/PSB smart objects up to this depth (0 disables).</summary>
    public int MaxEmbeddedDepth { get; init; } = 4;
}

/// <summary>All text extracted from a document.</summary>
public sealed class PsdTextContent
{
    public PsdTextContent(IReadOnlyList<PsdTextItem> items)
    {
        Items = items;
    }

    public IReadOnlyList<PsdTextItem> Items { get; }

    public IEnumerable<PsdTextItem> OfKind(PsdTextKind kind) => Items.Where(i => i.Kind == kind);

    /// <summary>Type-layer contents in panel order (top to bottom).</summary>
    public IEnumerable<string> TextLayers => OfKind(PsdTextKind.TextLayer).Select(i => i.Text);

    /// <summary>Layer and group names in panel order.</summary>
    public IEnumerable<string> LayerNames => Items.Where(i => i.Kind is PsdTextKind.LayerName or PsdTextKind.GroupName).Select(i => i.Text);

    /// <summary>All text joined with blank lines, for indexing or search.</summary>
    public override string ToString() => string.Join("\n\n", Items.Select(i => i.Text));
}

internal static class TextExtractor
{
    public static PsdTextContent Extract(PsdDocument document, TextExtractionOptions options)
    {
        var items = new List<PsdTextItem>();
        Collect(document, options, items, prefix: string.Empty, depth: 0);
        return new PsdTextContent(items);
    }

    private static void Collect(PsdDocument document, TextExtractionOptions options, List<PsdTextItem> items, string prefix, int depth)
    {
        var top = depth == 0;
        foreach (var layer in document.EnumerateLayersTopDown())
        {
            if (!options.IncludeHiddenLayers && !layer.IsEffectivelyVisible)
            {
                continue;
            }

            var path = prefix + layer.Path;
            if (options.IncludeLayerNames && layer.Name.Length > 0)
            {
                var kind = layer.IsGroup ? PsdTextKind.GroupName : PsdTextKind.LayerName;
                items.Add(new PsdTextItem(kind, layer.Name, path, top ? layer : null, depth));
            }

            if (options.IncludeTextLayers && layer.Text is { } text && text.Text.Length > 0)
            {
                items.Add(new PsdTextItem(PsdTextKind.TextLayer, text.Text, path, top ? layer : null, depth) { TextInfo = text });
            }

            if (layer.SmartObjectFileId is { } fileId)
            {
                var file = document.LinkedFiles.FirstOrDefault(f => f.Id == fileId);
                if (file is not null)
                {
                    if (options.IncludeLayerNames && file.FileName.Length > 0)
                    {
                        items.Add(new PsdTextItem(PsdTextKind.LinkedFileName, file.FileName, path, top ? layer : null, depth));
                    }

                    if (depth < options.MaxEmbeddedDepth && file.IsPhotoshopDocument)
                    {
                        try
                        {
                            var embedded = PsdDocument.Load(file.Data);
                            Collect(embedded, options, items, path + ">", depth + 1);
                        }
                        catch (PsdFormatException)
                        {
                            // A damaged embedded document contributes nothing.
                        }
                    }
                }
            }
        }

        if (options.IncludeTextLayers && options.IncludeTextEngineObjects)
        {
            CollectTextEngineObjects(document, options, items, prefix, depth);
        }

        if (options.IncludeChannelNames)
        {
            foreach (var name in ChannelNames(document))
            {
                items.Add(new PsdTextItem(PsdTextKind.ChannelName, name, prefix + "channels", null, depth));
            }
        }

        if (options.IncludePathNames)
        {
            foreach (var resource in document.ImageResources)
            {
                if (resource.Id is >= ImageResourceIds.PathFirst and <= ImageResourceIds.PathLast && resource.Name.Length > 0)
                {
                    items.Add(new PsdTextItem(PsdTextKind.PathName, resource.Name, prefix + "paths", null, depth));
                }
            }
        }

        if (options.IncludeSlices && document.GetImageResource(ImageResourceIds.Slices) is { } slices)
        {
            foreach (var (field, value) in SliceText(slices.Data))
            {
                items.Add(new PsdTextItem(PsdTextKind.Slice, value, prefix + field, null, depth));
            }
        }

        if (options.IncludeMetadata)
        {
            var seen = new HashSet<string>();
            if (document.GetImageResource(ImageResourceIds.Xmp) is { } xmp)
            {
                foreach (var (field, value) in XmpText(xmp.Data))
                {
                    if (seen.Add(field + "\0" + value))
                    {
                        items.Add(new PsdTextItem(PsdTextKind.Metadata, value, prefix + field, null, depth));
                    }
                }
            }

            if (document.GetImageResource(ImageResourceIds.IptcNaa) is { } iptc)
            {
                foreach (var (field, value) in IptcText(iptc.Data.Span))
                {
                    if (seen.Add(field + "\0" + value))
                    {
                        items.Add(new PsdTextItem(PsdTextKind.Metadata, value, prefix + field, null, depth));
                    }
                }
            }
        }
    }

    private static void CollectTextEngineObjects(PsdDocument document, TextExtractionOptions options, List<PsdTextItem> items, string prefix, int depth)
    {
        if (document.GetGlobalTaggedBlock("Txt2") is null || document.TextEngine is not { } engine)
        {
            return;
        }

        var owners = new Dictionary<int, PsdLayer>();
        foreach (var layer in document.Layers)
        {
            if (layer.Text?.TextIndex is int index)
            {
                owners.TryAdd(index, layer);
            }
        }

        foreach (var textObject in engine.Objects)
        {
            if (textObject.Text.Length == 0)
            {
                continue;
            }

            var source = $"{prefix}Txt2/{textObject.Index}";
            PsdLayer? owner = null;
            if (owners.TryGetValue(textObject.Index, out var layer))
            {
                if (!options.IncludeHiddenLayers && !layer.IsEffectivelyVisible)
                {
                    continue;
                }

                if (layer.Text!.Text == textObject.Text)
                {
                    continue;
                }

                owner = layer;
                source = prefix + layer.Path;
            }

            items.Add(new PsdTextItem(PsdTextKind.TextEngineObject, textObject.Text, source, depth == 0 ? owner : null, depth)
            {
                TextInfo = TextEngineResolver.FromObject(textObject, null),
            });
        }
    }

    internal static List<string> ChannelNames(PsdDocument document)
    {
        var names = new List<string>();
        if (document.GetImageResource(ImageResourceIds.UnicodeAlphaNames) is { } unicode)
        {
            var reader = new BigEndianReader(unicode.Data);
            try
            {
                while (reader.Remaining >= 4)
                {
                    var name = reader.ReadUnicodeString();
                    if (name.Length > 0)
                    {
                        names.Add(name);
                    }
                }
            }
            catch (PsdFormatException)
            {
                // Keep what decoded.
            }

            return names;
        }

        if (document.GetImageResource(ImageResourceIds.AlphaChannelNames) is { } pascal)
        {
            var reader = new BigEndianReader(pascal.Data);
            try
            {
                while (reader.Remaining >= 1)
                {
                    var name = reader.ReadPascalString(1);
                    if (name.Length > 0)
                    {
                        names.Add(name);
                    }
                }
            }
            catch (PsdFormatException)
            {
                // Keep what decoded.
            }
        }

        return names;
    }

    private static IEnumerable<(string Field, string Value)> SliceText(ReadOnlyMemory<byte> data)
    {
        var results = new List<(string, string)>();
        try
        {
            var reader = new BigEndianReader(data);
            var version = reader.ReadUInt32();
            if (version is 7 or 8)
            {
                var descriptor = Descriptor.ReadVersioned(reader);
                CollectSliceDescriptor(descriptor, results);
                return results;
            }

            if (version != 6)
            {
                return results;
            }

            reader.Skip(16); // bounds
            var groupName = reader.ReadUnicodeString();
            if (groupName.Length > 0)
            {
                results.Add(("slices/group", groupName));
            }

            var count = reader.ReadUInt32();
            for (var i = 0u; i < count && reader.Remaining > 0; i++)
            {
                _ = reader.ReadUInt32(); // id
                _ = reader.ReadUInt32(); // group id
                var origin = reader.ReadUInt32();
                if (origin == 1)
                {
                    _ = reader.ReadUInt32(); // associated layer id
                }

                var name = reader.ReadUnicodeString();
                _ = reader.ReadUInt32(); // type
                reader.Skip(16); // bounds
                var url = reader.ReadUnicodeString();
                _ = reader.ReadUnicodeString(); // target
                var message = reader.ReadUnicodeString();
                var alt = reader.ReadUnicodeString();
                _ = reader.ReadByte(); // cell text is HTML
                var cell = reader.ReadUnicodeString();
                reader.Skip(4 + 4 + 4); // alignment, alpha/rgb color
                AddSlice(results, "name", name);
                AddSlice(results, "url", url);
                AddSlice(results, "message", message);
                AddSlice(results, "alt", alt);
                AddSlice(results, "cellText", cell);
            }
        }
        catch (PsdFormatException)
        {
            // Return what decoded.
        }

        return results;
    }

    private static void AddSlice(List<(string, string)> results, string field, string value)
    {
        if (value.Length > 0)
        {
            results.Add(("slices/" + field, value));
        }
    }

    private static void CollectSliceDescriptor(Descriptor descriptor, List<(string, string)> results)
    {
        foreach (var (key, value) in descriptor.Items)
        {
            switch (value.Type)
            {
                case DescriptorValueType.String:
                    var field = key switch
                    {
                        "Nm  " => "name",
                        "url " => "url",
                        "Msge" => "message",
                        "altTag" => "alt",
                        "cellText" => "cellText",
                        "baseName" => "baseName",
                        _ => null,
                    };
                    if (field is not null)
                    {
                        AddSlice(results, field, value.String ?? string.Empty);
                    }

                    break;
                case DescriptorValueType.Object when value.Object is not null:
                    CollectSliceDescriptor(value.Object, results);
                    break;
                case DescriptorValueType.List when value.List is not null:
                    foreach (var item in value.List)
                    {
                        if (item.Object is not null)
                        {
                            CollectSliceDescriptor(item.Object, results);
                        }
                    }

                    break;
            }
        }
    }

    private static IEnumerable<(string Field, string Value)> XmpText(ReadOnlyMemory<byte> data)
    {
        var results = new List<(string, string)>();
        try
        {
            var xml = System.Text.Encoding.UTF8.GetString(data.Span).TrimEnd('\0');
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader);
            XNamespace dc = "http://purl.org/dc/elements/1.1/";
            XNamespace photoshop = "http://ns.adobe.com/photoshop/1.0/";
            foreach (var (name, field) in new[]
            {
                (dc + "title", "xmp/title"),
                (dc + "description", "xmp/description"),
                (dc + "subject", "xmp/keywords"),
                (dc + "creator", "xmp/creator"),
                (dc + "rights", "xmp/rights"),
                (photoshop + "Headline", "xmp/headline"),
            })
            {
                foreach (var element in root.Descendants(name))
                {
                    var values = element.Descendants().Where(e => e.Name.LocalName == "li").Select(e => e.Value).ToList();
                    if (values.Count == 0)
                    {
                        values.Add(element.Value);
                    }

                    foreach (var value in values)
                    {
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            results.Add((field, value.Trim()));
                        }
                    }
                }

                foreach (var attribute in root.Descendants().Attributes(name))
                {
                    if (!string.IsNullOrWhiteSpace(attribute.Value))
                    {
                        results.Add((field, attribute.Value.Trim()));
                    }
                }
            }
        }
        catch (XmlException)
        {
            // Malformed XMP contributes nothing.
        }

        return results;
    }

    private static IEnumerable<(string Field, string Value)> IptcText(ReadOnlySpan<byte> data)
    {
        var results = new List<(string, string)>();
        var offset = 0;
        while (offset + 5 <= data.Length)
        {
            if (data[offset] != 0x1C)
            {
                break;
            }

            var record = data[offset + 1];
            var dataset = data[offset + 2];
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 3)..]);
            offset += 5;
            if ((length & 0x8000) != 0 || offset + length > data.Length)
            {
                break;
            }

            if (record == 2)
            {
                var field = dataset switch
                {
                    5 => "iptc/title",
                    25 => "iptc/keywords",
                    80 => "iptc/byline",
                    105 => "iptc/headline",
                    116 => "iptc/copyright",
                    120 => "iptc/caption",
                    _ => null,
                };
                if (field is not null)
                {
                    var value = System.Text.Encoding.UTF8.GetString(data.Slice(offset, length)).Trim('\0', ' ');
                    if (value.Length > 0)
                    {
                        results.Add((field, value));
                    }
                }
            }

            offset += length;
        }

        return results;
    }
}
