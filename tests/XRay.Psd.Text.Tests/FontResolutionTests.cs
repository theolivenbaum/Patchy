using SkiaSharp;
using XRay.Psd.Text.Tests.Support;

namespace XRay.Psd.Text.Tests;

public sealed class FontResolutionTests
{
    [Theory]
    [InlineData("ArialMT", "Arial", false, false)]
    [InlineData("Arial-BoldMT", "Arial", true, false)]
    [InlineData("Arial-BoldItalicMT", "Arial", true, true)]
    [InlineData("TimesNewRomanPSMT", "Times New Roman", false, false)]
    [InlineData("TimesNewRomanPS-ItalicMT", "Times New Roman", false, true)]
    [InlineData("Georgia-BoldItalic", "Georgia", true, true)]
    [InlineData("MyriadPro-Regular", "Myriad Pro", false, false)]
    [InlineData("FranklinGothic-HeavyItalic", "Franklin Gothic", true, true)]
    [InlineData("MS-Gothic", "MS Gothic", false, false)]
    [InlineData("SegoeUI", "Segoe UI", false, false)]
    [InlineData("", "Arial", false, false)]
    public void PostScript_names_split_into_family_and_flags(string name, string family, bool bold, bool italic)
    {
        Assert.Equal((family, bold, italic), PostScriptFontNames.Parse(name));
    }

    [Fact]
    public void Collection_matches_PostScript_name_family_and_alias()
    {
        var fonts = TestFonts.Fonts;
        var face = TestFonts.LiberationSans;

        Assert.Same(face, fonts.Resolve(FontRequest.FromPostScriptName("LiberationSans")));
        Assert.Same(face, fonts.Resolve(new FontRequest(null, "Liberation Sans", false, false)));
        Assert.Same(face, fonts.Resolve(FontRequest.FromPostScriptName("ArialMT")));
        Assert.Same(face, fonts.Resolve(FontRequest.FromPostScriptName("Helvetica-Bold")));
        Assert.Null(fonts.Resolve(FontRequest.FromPostScriptName("MyriadPro-Regular")));
    }

    [Fact]
    public void Aliases_can_be_turned_off()
    {
        using var fonts = new FontCollection { UseAliases = false };
        fonts.AddFile(Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf"));

        Assert.Null(fonts.Resolve(FontRequest.FromPostScriptName("ArialMT")));
        Assert.NotNull(fonts.Resolve(FontRequest.FromPostScriptName("LiberationSans")));
    }

    [Fact]
    public void Composite_resolver_asks_in_order()
    {
        var first = new RecordingResolver(null);
        var second = new RecordingResolver(TestFonts.LiberationSans);
        var composite = new CompositeFontResolver(first, second);

        Assert.Same(TestFonts.LiberationSans, composite.Resolve(FontRequest.FromPostScriptName("ArialMT")));
        Assert.Equal(1, first.Calls);
        Assert.Equal(1, second.Calls);
    }

    [Fact]
    public void Photoshop_5_faces_use_their_stored_family()
    {
        var run = LayoutTests.Run(0, 1, 12) with { FontName = "FuturaBT-BoldCondensed", FontFamily = "Futura BdCn BT", FontStyleName = "Bold" };

        var request = FontRequest.FromRun(run);

        Assert.Equal(("Futura BdCn BT", true, false), (request.Family, request.Bold, request.Italic));
    }

    [Fact]
    public void Renderer_uses_the_resolver_for_each_run()
    {
        var resolver = new RecordingResolver(TestFonts.LiberationSans);
        using var renderer = new TextLayerRenderer(new TextRenderSettings { FontResolver = resolver, FallbackTypeface = TestFonts.LiberationSans, UseSystemGlyphFallback = false });

        Assert.NotNull(renderer.Render(LayoutTests.Model("Hello")));
        Assert.Equal("ArialMT", resolver.LastRequest!.PostScriptName);
    }

    private sealed class RecordingResolver(SKTypeface? answer) : IFontResolver
    {
        public int Calls { get; private set; }

        public FontRequest? LastRequest { get; private set; }

        public SKTypeface? Resolve(FontRequest request)
        {
            Calls++;
            LastRequest = request;
            return answer;
        }
    }
}
