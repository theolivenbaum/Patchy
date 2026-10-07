using System.Globalization;
using XRay.Psd;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Text;

return Cli.Run(args);

internal static class Cli
{
    private const string Usage = """
        psdtool - inspect, render and extract text from PSD/PSB files

        Usage:
          psdtool info <file.psd> [--resources] [--effects]
                                                      Print the header and layer tree, plus the typed image
                                                      resources and each layer's effects when asked
          psdtool text <file.psd> [--content-only]    Print all text (layer names, type layers, metadata...)
          psdtool render <file.psd> <out.png|out.jpg> [--layers|--merged] [--quality N] [--background RRGGBB]
          psdtool layers <file.psd> <out-dir>         Write each content layer as a PNG
        """;

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "info" => Info(args[1], args.Contains("--resources"), args.Contains("--effects")),
                "text" => Text(args[1], args.Contains("--content-only")),
                "render" when args.Length >= 3 => Render(args[1], args[2], args[3..]),
                "layers" when args.Length >= 3 => Layers(args[1], args[2]),
                _ => Fail(Usage),
            };
        }
        catch (PsdFormatException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int Info(string path, bool resources = false, bool effects = false)
    {
        var document = PsdDocument.Load(path);
        Console.WriteLine(document);
        Console.WriteLine($"merged image: {(document.HasRealMergedImage ? "real" : "placeholder")}, transparency: {document.MergedImageHasTransparency}");
        if (resources)
        {
            InfoDetails.PrintResources(document);
        }
        foreach (var layer in document.EnumerateLayersTopDown())
        {
            var depth = 0;
            for (var parent = layer.Parent; parent is not null; parent = parent.Parent)
            {
                depth++;
            }

            var flags = new List<string>();
            if (!layer.IsVisible)
            {
                flags.Add("hidden");
            }

            if (layer.IsClipped)
            {
                flags.Add("clipped");
            }

            if (layer.Mask is not null)
            {
                flags.Add("mask");
            }

            if (layer.VectorMask is not null)
            {
                flags.Add("vmask");
            }

            if (layer.Effects is not null)
            {
                flags.Add("fx");
            }

            if (layer.ContentKey is { } key)
            {
                flags.Add(key.Trim());
            }

            Console.WriteLine($"{new string(' ', depth * 2)}{layer.Kind,-11} \"{layer.Name}\" {layer.Bounds} {layer.BlendMode} op={layer.Opacity} fill={layer.FillOpacity} {string.Join(',', flags)}");
            if (effects && layer.Style is { } style)
            {
                InfoDetails.PrintStyle(style, new string(' ', (depth * 2) + 2));
            }
        }

        Console.WriteLine($"global blocks: {string.Join(' ', document.GlobalTaggedBlocks.Select(b => $"{b.Key}({b.Data.Length})"))}");
        foreach (var file in document.LinkedFiles)
        {
            Console.WriteLine($"linked file: {file.Kind} {file.FileName} ({file.FileType}, {file.Data.Length} bytes)");
        }

        return 0;
    }

    private static int Text(string path, bool contentOnly)
    {
        var document = PsdDocument.Load(path);
        var content = document.ExtractText();
        foreach (var item in content.Items)
        {
            if (contentOnly && item.Kind is not (PsdTextKind.TextLayer or PsdTextKind.TextEngineObject))
            {
                continue;
            }

            Console.WriteLine($"[{item.Kind}] {item.Source}");
            Console.WriteLine(item.Text);
            if (item.TextInfo is { Fonts.Count: > 0 } info)
            {
                Console.WriteLine($"  fonts: {string.Join(", ", info.Fonts)}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int Render(string path, string output, string[] options)
    {
        var source = RenderSource.Auto;
        var quality = 90;
        PsdColor? background = null;
        for (var i = 0; i < options.Length; i++)
        {
            switch (options[i])
            {
                case "--layers":
                    source = RenderSource.Layers;
                    break;
                case "--merged":
                    source = RenderSource.MergedImage;
                    break;
                case "--quality" when i + 1 < options.Length:
                    quality = int.Parse(options[++i], CultureInfo.InvariantCulture);
                    break;
                case "--background" when i + 1 < options.Length:
                    var hex = options[++i].TrimStart('#');
                    var value = Convert.ToInt32(hex, 16);
                    background = new PsdColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
                    break;
                default:
                    return Fail($"unknown option {options[i]}");
            }
        }

        var document = PsdDocument.Load(path);
        var image = document.Render(new RenderOptions { Source = source, Background = background });
        image.Save(output, quality);
        Console.WriteLine($"wrote {output} ({image.Width}x{image.Height})");
        return 0;
    }

    private static int Layers(string path, string directory)
    {
        var document = PsdDocument.Load(path);
        Directory.CreateDirectory(directory);
        var index = 0;
        foreach (var layer in document.Layers)
        {
            if (layer.IsGroup)
            {
                continue;
            }

            var pixels = layer.GetPixels();
            if (pixels is null)
            {
                continue;
            }

            var safe = string.Concat(layer.Name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var file = Path.Combine(directory, $"{index++:D3}_{safe}.png");
            pixels.SavePng(file);
            Console.WriteLine($"{file} {layer.Bounds}");
        }

        return 0;
    }
}
