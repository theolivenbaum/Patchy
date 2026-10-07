using System.Collections.Concurrent;
using System.Text;
using SkiaSharp;

namespace XRay.Psd.Text;

/// <summary>
/// A font a style run asks for: the PostScript name Photoshop stored, and the family and
/// style flags derived from it (or, for Photoshop 5 faces, stored next to it).
/// </summary>
/// <param name="PostScriptName">The stored PostScript name (<c>ArialMT</c>, <c>Georgia-BoldItalic</c>), when known.</param>
/// <param name="Family">Display family (<c>Arial</c>, <c>Georgia</c>).</param>
/// <param name="Bold">The face name says bold (or heavier).</param>
/// <param name="Italic">The face name says italic or oblique.</param>
public sealed record FontRequest(string? PostScriptName, string Family, bool Bold, bool Italic)
{
    /// <summary>Builds a request from a PostScript name with the suffix heuristic of <see cref="PostScriptFontNames.Parse"/>.</summary>
    public static FontRequest FromPostScriptName(string? postScriptName)
    {
        var (family, bold, italic) = PostScriptFontNames.Parse(postScriptName);
        return new FontRequest(string.IsNullOrEmpty(postScriptName) ? null : postScriptName, family, bold, italic);
    }

    /// <summary>The request for a style run: its PostScript name, or the Photoshop 5 family and style strings when the record has them.</summary>
    public static FontRequest FromRun(TextStyleRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        var request = FromPostScriptName(run.FontName);
        if (!string.IsNullOrWhiteSpace(run.FontFamily))
        {
            // Photoshop 5 faces carry the names Windows lists them under (docs/font-resolution.md).
            var style = run.FontStyleName ?? string.Empty;
            request = request with
            {
                Family = run.FontFamily.Trim(),
                Bold = request.Bold || ContainsWord(style, "bold") || ContainsWord(style, "black") || ContainsWord(style, "heavy"),
                Italic = request.Italic || ContainsWord(style, "italic") || ContainsWord(style, "oblique"),
            };
        }

        return request;
    }

    internal SKFontStyle ToSkiaStyle() => new(Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, Italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright);

    private static bool ContainsWord(string text, string word) => text.Contains(word, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Maps a <see cref="FontRequest"/> to a typeface. Return null when the font is not available.</summary>
public interface IFontResolver
{
    SKTypeface? Resolve(FontRequest request);
}

/// <summary>PostScript font-name helpers, ported from the reference reader (<c>heuristic_resolved_photoshop_font</c> in <c>.reference/src/psd/psd_text_read.cpp</c>).</summary>
public static class PostScriptFontNames
{
    private static readonly (string Suffix, bool Bold, bool Italic)[] StyleSuffixes =
    [
        ("-bolditalicmt", true, true),
        ("-boldobliquemt", true, true),
        ("-bolditalic", true, true),
        ("-boldoblique", true, true),
        ("-boldital", true, true),
        ("-boldit", true, true),
        ("-semibolditalic", true, true),
        ("-demibolditalic", true, true),
        ("-blackitalic", true, true),
        ("-heavyitalic", true, true),
        ("-extrabold", true, false),
        ("-ultrabold", true, false),
        ("-semibold", true, false),
        ("-demibold", true, false),
        ("-boldmt", true, false),
        ("-bold", true, false),
        ("-black", true, false),
        ("-heavy", true, false),
        ("-italicmt", false, true),
        ("-obliquemt", false, true),
        ("-italic", false, true),
        ("-oblique", false, true),
        ("-ital", false, true),
        ("-it", false, true),
        ("-regular", false, false),
    ];

    /// <summary>
    /// Splits a PostScript name into a display family and bold/italic flags by stripping
    /// style suffixes (<c>-BoldItalicMT</c>, <c>-Bold</c>, <c>-It</c>...), the <c>MT</c>/<c>PS</c>
    /// vendor tails, and inserting spaces at case changes (<c>TimesNewRomanPSMT</c> to <c>Times New Roman</c>).
    /// An empty name resolves to Arial, as in the reference.
    /// </summary>
    public static (string Family, bool Bold, bool Italic) Parse(string? postScriptName)
    {
        var original = string.IsNullOrWhiteSpace(postScriptName) ? "Arial" : postScriptName.Trim();
        var family = original;
        bool bold = false, italic = false;
        var stripped = true;
        while (stripped)
        {
            stripped = false;
            foreach (var (suffix, isBold, isItalic) in StyleSuffixes)
            {
                if (family.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    family = family[..^suffix.Length];
                    bold |= isBold;
                    italic |= isItalic;
                    stripped = true;
                    break;
                }
            }
        }

        family = StripSuffix(family, "-roman");
        family = StripSuffix(family, "mt");
        family = StripSuffix(family, "ps");
        family = family.TrimEnd('-', '_', ' ');
        family = family.Length == 0 ? original : Humanize(family);
        return (family, bold, italic);
    }

    /// <summary>Inserts spaces at lower-to-upper, acronym-to-word and letter-digit boundaries; dashes and underscores become spaces.</summary>
    public static string Humanize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Contains(' ', StringComparison.Ordinal))
        {
            return name;
        }

        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];
            if (i > 0 && char.IsAsciiLetterOrDigit(ch))
            {
                var previous = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                var lowerToUpper = char.IsAsciiLetterLower(previous) && char.IsAsciiLetterUpper(ch);
                var acronymToWord = char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterUpper(ch) && char.IsAsciiLetterLower(next);
                var letterDigit = (char.IsAsciiLetter(previous) && char.IsAsciiDigit(ch)) || (char.IsAsciiDigit(previous) && char.IsAsciiLetter(ch));
                if (lowerToUpper || acronymToWord || letterDigit)
                {
                    builder.Append(' ');
                }
            }

            var output = ch is '_' or '-' ? ' ' : ch;
            if (output == ' ' && (builder.Length == 0 || builder[^1] == ' '))
            {
                continue;
            }

            builder.Append(output);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>Lowercase ASCII letters and digits only, the key the reference compares font names with.</summary>
    public static string CompactKey(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }

    private static string StripSuffix(string value, string suffix) =>
        value.Length > suffix.Length && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? value[..^suffix.Length] : value;
}

/// <summary>
/// Metric-compatible stand-ins for common Photoshop families, the table the reference
/// uses where system fonts are missing (<c>.reference/docs/fonts.md</c>, "Wasm family aliases").
/// </summary>
public static class FontAliases
{
    private static readonly Dictionary<string, string[]> Table = Build(
        ("Arial", ["Liberation Sans", "Arimo", "Helvetica", "Nimbus Sans"]),
        ("Helvetica", ["Arial", "Liberation Sans", "Arimo", "Nimbus Sans"]),
        ("Helvetica Neue", ["Helvetica", "Arial", "Liberation Sans", "Arimo"]),
        ("Times New Roman", ["Liberation Serif", "Tinos", "Times", "Nimbus Roman"]),
        ("Times", ["Times New Roman", "Liberation Serif", "Tinos", "Nimbus Roman"]),
        ("Courier New", ["Liberation Mono", "Cousine", "Courier", "Nimbus Mono PS"]),
        ("Courier", ["Courier New", "Liberation Mono", "Cousine", "Nimbus Mono PS"]),
        ("Calibri", ["Carlito"]),
        ("Cambria", ["Caladea"]),
        ("Segoe UI", ["Noto Sans", "DejaVu Sans"]),
        ("Tahoma", ["Noto Sans", "DejaVu Sans"]),
        ("Verdana", ["DejaVu Sans", "Noto Sans"]),
        ("Georgia", ["Noto Serif", "DejaVu Serif"]),
        ("MS Gothic", ["Noto Sans JP", "Noto Sans CJK JP", "IPAGothic", "TakaoGothic"]),
        ("MS Mincho", ["Noto Serif JP", "Noto Serif CJK JP", "IPAMincho"]),
        ("Meiryo", ["Noto Sans JP", "Noto Sans CJK JP"]),
        ("Yu Gothic", ["Noto Sans JP", "Noto Sans CJK JP"]));

    /// <summary>Substitute families for <paramref name="family"/>, best first; empty when none is known.</summary>
    public static IReadOnlyList<string> SubstitutesFor(string family) =>
        Table.TryGetValue(PostScriptFontNames.CompactKey(family), out var list) ? list : [];

    private static Dictionary<string, string[]> Build(params (string Family, string[] Substitutes)[] entries)
    {
        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var (family, substitutes) in entries)
        {
            table[PostScriptFontNames.CompactKey(family)] = substitutes;
        }

        return table;
    }
}

/// <summary>
/// Fonts registered by the application (files, streams or typefaces), matched by PostScript
/// name, then by family and style, then through <see cref="FontAliases"/>.
/// </summary>
public sealed class FontCollection : IFontResolver, IDisposable
{
    private readonly List<SKTypeface> _typefaces = [];
    private readonly object _gate = new();

    /// <summary>Whether a family that is not registered may resolve to a metric-compatible alias (default true).</summary>
    public bool UseAliases { get; init; } = true;

    public IReadOnlyList<SKTypeface> Typefaces
    {
        get
        {
            lock (_gate)
            {
                return [.. _typefaces];
            }
        }
    }

    /// <summary>Registers a typeface. The collection disposes it.</summary>
    public SKTypeface Add(SKTypeface typeface)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        lock (_gate)
        {
            _typefaces.Add(typeface);
        }

        return typeface;
    }

    /// <summary>Loads and registers a font file (TrueType, OpenType, or one face of a collection).</summary>
    public SKTypeface AddFile(string path, int faceIndex = 0)
    {
        var typeface = SKTypeface.FromFile(path, faceIndex) ?? throw new ArgumentException($"Not a loadable font: {path}", nameof(path));
        return Add(typeface);
    }

    /// <summary>Loads and registers a font from a stream (the stream is copied).</summary>
    public SKTypeface AddStream(Stream stream, int faceIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        using var data = SKData.CreateCopy(copy.ToArray());
        var typeface = SKTypeface.FromData(data, faceIndex) ?? throw new ArgumentException("Not a loadable font.", nameof(stream));
        return Add(typeface);
    }

    public SKTypeface? Resolve(FontRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        SKTypeface[] faces;
        lock (_gate)
        {
            faces = [.. _typefaces];
        }

        var postScript = PostScriptFontNames.CompactKey(request.PostScriptName);
        if (postScript.Length > 0)
        {
            foreach (var face in faces)
            {
                if (PostScriptFontNames.CompactKey(face.PostScriptName) == postScript)
                {
                    return face;
                }
            }
        }

        if (BestInFamily(faces, request.Family, request) is { } direct)
        {
            return direct;
        }

        if (UseAliases)
        {
            foreach (var alias in FontAliases.SubstitutesFor(request.Family))
            {
                if (BestInFamily(faces, alias, request) is { } substitute)
                {
                    return substitute;
                }
            }
        }

        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var face in _typefaces)
            {
                face.Dispose();
            }

            _typefaces.Clear();
        }
    }

    private static SKTypeface? BestInFamily(SKTypeface[] faces, string family, FontRequest request)
    {
        var key = PostScriptFontNames.CompactKey(family);
        SKTypeface? best = null;
        var bestScore = int.MaxValue;
        foreach (var face in faces)
        {
            if (PostScriptFontNames.CompactKey(face.FamilyName) != key)
            {
                continue;
            }

            var score = (face.IsBold != request.Bold ? 2 : 0) + (face.IsItalic != request.Italic ? 1 : 0);
            if (score < bestScore)
            {
                best = face;
                bestScore = score;
            }
        }

        return best;
    }
}

/// <summary>
/// Installed fonts through SkiaSharp's platform font manager (fontconfig, DirectWrite, CoreText).
/// Only a face whose family really matches the request (or one of its <see cref="FontAliases"/>)
/// is accepted, so a missing font is reported as missing instead of silently substituted.
/// </summary>
public sealed class SystemFontResolver : IFontResolver
{
    private readonly SKFontManager _manager;
    private readonly ConcurrentDictionary<FontRequest, SKTypeface?> _cache = new();

    public SystemFontResolver(SKFontManager manager)
    {
        ArgumentNullException.ThrowIfNull(manager);
        _manager = manager;
    }

    /// <summary>The platform default font manager.</summary>
    public static SystemFontResolver Default { get; } = new(SKFontManager.Default);

    /// <summary>Whether a family that is not installed may resolve to a metric-compatible alias (default true).</summary>
    public bool UseAliases { get; init; } = true;

    public SKTypeface? Resolve(FontRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _cache.GetOrAdd(request, ResolveUncached);
    }

    private SKTypeface? ResolveUncached(FontRequest request)
    {
        var style = request.ToSkiaStyle();
        var candidates = new List<string> { request.Family };
        if (request.PostScriptName is { Length: > 0 } postScript)
        {
            // Full names such as "Arial Black" resolve as a family on some platforms.
            candidates.Add(PostScriptFontNames.Humanize(postScript));
        }

        if (UseAliases)
        {
            candidates.AddRange(FontAliases.SubstitutesFor(request.Family));
        }

        foreach (var family in candidates)
        {
            var typeface = _manager.MatchFamily(family, style);
            if (typeface is null)
            {
                continue;
            }

            if (PostScriptFontNames.CompactKey(typeface.FamilyName) == PostScriptFontNames.CompactKey(family))
            {
                return typeface;
            }

            typeface.Dispose();
        }

        return null;
    }
}

/// <summary>Asks each resolver in turn and returns the first match.</summary>
public sealed class CompositeFontResolver : IFontResolver
{
    private readonly IFontResolver[] _resolvers;

    public CompositeFontResolver(params IFontResolver[] resolvers)
    {
        ArgumentNullException.ThrowIfNull(resolvers);
        _resolvers = [.. resolvers];
    }

    public SKTypeface? Resolve(FontRequest request)
    {
        foreach (var resolver in _resolvers)
        {
            if (resolver.Resolve(request) is { } typeface)
            {
                return typeface;
            }
        }

        return null;
    }
}
