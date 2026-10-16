using XRay.Psd.Imaging;
using XRay.Psd.Layers;
using XRay.Psd.Rendering;
using XRay.Psd.Tests.Support;

namespace XRay.Psd.Tests;

/// <summary>
/// Gradient fill layer (<c>GdFl</c>) geometry and dither against Photoshop. The psd-tools
/// files carry Photoshop's own render of each fill as layer pixels; stripping those pixels
/// makes the compositor regenerate the fill from its descriptor, which must reproduce them.
/// </summary>
public sealed class GradientTests
{
    /// <summary>
    /// Photoshop stores these 16 and 32-bit fills without pixels. The 30 degree reflected
    /// fill runs along the 45 degree diagonal of the 4x4 canvas: its line snaps to the pixel
    /// centers (2.5, 2.5) and (4.5, 0.5); before the snapping it came out mirrored (max 42 to
    /// 123). The stops then mix in the document's model: linear light at 32 bits, inks in
    /// CMYK, L*a*b* in Lab, gray in grayscale. CMYK keeps 11 levels in its midtones and Lab
    /// 10 in its stop colors, which convert the same way without a gradient.
    /// </summary>
    [Theory]
    [InlineData("colormodes/4x4_16bit_rgb.psd", 2)]
    [InlineData("colormodes/4x4_32bit_rgb.psd", 3)]
    [InlineData("colormodes/4x4_16bit_grayscale.psd", 3)]
    [InlineData("colormodes/4x4_32bit_grayscale.psd", 2)]
    [InlineData("colormodes/4x4_16bit_cmyk.psd", 11)]
    [InlineData("colormodes/4x4_16bit_lab.psd", 10)]
    public void Gradient_fill_without_pixels_matches_the_merged_image(string path, int maxDelta)
    {
        var document = PsdDocument.Load(Fixtures.PsdTools(path));
        Assert.Contains(document.Layers, l => l.ContentKey == "GdFl" && l.Bounds.IsEmpty);

        var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), document.Render(new RenderOptions { Source = RenderSource.MergedImage }));

        Assert.True(diff.MaxDelta <= maxDelta, $"{path}: {diff}");
    }

    [Theory]
    [InlineData("colormodes/4x4_8bit_rgb.psd", 2)]
    [InlineData("colormodes/4x4_8bit_grayscale.psd", 2)]
    [InlineData("colormodes/4x4_8bit_duotone.psd", 2)]
    [InlineData("colormodes/4x4_8bit_cmyk.psd", 11)]
    [InlineData("colormodes/4x4_8bit_lab.psd", 11)]
    [InlineData("colorprofiles/north_america_newspaper.psd", 2)]
    [InlineData("gradient-sizes.psd", 3)]
    [InlineData("masks3.psd", 7)]
    public void Regenerated_gradient_fills_match_the_merged_image(string path, int maxDelta)
    {
        var document = PsdDocument.Load(Fixtures.PsdTools(path));
        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        Assert.True(StripGradientFillPixels(document) > 0);

        var diff = ImageTools.Compare(document.Render(new RenderOptions { Source = RenderSource.Layers }), merged);

        Assert.True(diff.MaxDelta <= maxDelta, $"{path}: {diff}");
    }

    /// <summary>
    /// psd-tools' gradient-styles.psd: every type at 30 degrees, at 60 degrees reversed, and
    /// at -60 degrees with Scale 50% and no dither, on 32 px squares. Angle gradients keep a
    /// few seam pixels where the ramp wraps; the reversed 60 degree diamond is the one
    /// residual (Photoshop's diamond there is about 5% smaller than its other diamonds).
    /// </summary>
    [Theory]
    [InlineData("Linear", 30, 3, 0)]
    [InlineData("Radial", 30, 3, 0)]
    [InlineData("Angle", 30, 255, 12)]
    [InlineData("Reflected", 30, 3, 0)]
    [InlineData("Diamond", 30, 3, 0)]
    [InlineData("Linear", 60, 3, 0)]
    [InlineData("Radial", 60, 3, 0)]
    [InlineData("Angle", 60, 255, 12)]
    [InlineData("Reflected", 60, 3, 0)]
    [InlineData("Diamond", 60, 17, 650)]
    [InlineData("Linear", -60, 1, 0)]
    [InlineData("Radial", -60, 1, 0)]
    [InlineData("Angle", -60, 255, 12)]
    [InlineData("Reflected", -60, 1, 0)]
    [InlineData("Diamond", -60, 1, 0)]
    public void Every_gradient_type_matches_photoshop(string name, int angle, int maxDelta, int maxOutliers)
    {
        var path = Fixtures.PsdTools("gradient-styles.psd");
        var stored = PsdDocument.Load(path);
        var regenerated = PsdDocument.Load(path);
        var index = FindFill(stored, name, angle);
        var layer = regenerated.Layers[index];
        layer.Channels = layer.Channels.Where(c => c.Id < -1).ToList();

        var expected = stored.Layers[index].Render();
        var actual = layer.Render();

        var (max, outliers) = Deviation(actual, expected, stored.Layers[index].Bounds);
        Assert.True(max <= maxDelta, $"{name} {angle}: max {max}");
        Assert.True(outliers <= maxOutliers, $"{name} {angle}: {outliers} pixels off by more than 3");
    }

    [Fact]
    public void Thirty_two_bit_gradients_mix_in_linear_light()
    {
        // Blue (10, 0, 178) to red at the quarter point of the eased ramp: (188, 0, 130) in
        // Photoshop's 32-bit merged image; an sRGB mix gives (132, 0, 89).
        var document = PsdDocument.Load(Fixtures.PsdTools("colormodes/4x4_32bit_rgb.psd"));
        var fill = document.Layers.Single(l => l.ContentKey == "GdFl");
        var gradient = Gradient.FromDescriptor(fill.FillDescriptor!, defaultAngle: 0)!;
        Assert.Equal(GradientColorModel.LinearRgb, gradient.ColorModel);

        var (r, g, b) = gradient.Color(0.25f, endpointSmoothing: true);
        Assert.InRange((int)MathF.Round(r * 255), 187, 189);
        Assert.InRange((int)MathF.Round(g * 255), 0, 1);
        Assert.InRange((int)MathF.Round(b * 255), 129, 131);
        Assert.Equal(new PsdColor(188, 0, 130), document.Render(new RenderOptions { Source = RenderSource.MergedImage }).GetPixel(1, 0));
    }

    [Fact]
    public void Srgb_documents_keep_the_byte_mix()
    {
        static Gradient Fill(string path) =>
            Gradient.FromDescriptor(PsdDocument.Load(Fixtures.PsdTools(path)).Layers.Single(l => l.ContentKey == "GdFl").FillDescriptor!, defaultAngle: 0)!;

        Assert.Equal(GradientColorModel.Srgb, Fill("colormodes/4x4_8bit_rgb.psd").ColorModel);
        Assert.Equal(GradientColorModel.ProfileRgb, Fill("colorprofiles/north_america_newspaper.psd").ColorModel);
        Assert.Equal(GradientColorModel.Ink, Fill("colormodes/4x4_16bit_cmyk.psd").ColorModel);
        Assert.Equal(GradientColorModel.Lab, Fill("colormodes/4x4_16bit_lab.psd").ColorModel);
        Assert.Equal(GradientColorModel.Gray, Fill("colormodes/4x4_8bit_duotone.psd").ColorModel);
    }

    [Fact]
    public void Gradient_fill_without_a_path_spans_its_mask()
    {
        // A fill layer with a raster mask and no path aligns to the mask rectangle
        // (psd-tools' gradient-fill.psd: 94 mask rows, not the 150 of the canvas).
        var builder = new PsdBuilder { Width = 4, Height = 40 };
        var layer = new BuilderLayer { Name = "Gradient", Rect = default, MaskRect = new PsdRect(0, 10, 4, 30) };
        layer.Channels[-2] = PsdBuilder.Plane8(4, 20, 255);
        layer.Blocks.Add(("GdFl", FillDescriptor(dither: false, angle: 90).Versioned()));
        builder.Layers.Add(layer);
        var image = PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });

        // 90 degrees runs from black at the bottom of the mask to white at its top.
        Assert.True(image.GetPixel(0, 10).R >= 245, $"top {image.GetPixel(0, 10)}");
        Assert.True(image.GetPixel(0, 29).R <= 10, $"bottom {image.GetPixel(0, 29)}");
        Assert.InRange(image.GetPixel(0, 20).R, 105, 150);
    }

    [Fact]
    public void Gradient_fill_spans_its_path_not_the_pixel_hull()
    {
        // A 64 px rectangle path stores its far edge as 63.99999; rounded outward that
        // is 65 px, which stretched the ramp by one pixel (gradient-sizes.psd, max 10 before).
        var document = PsdDocument.Load(Fixtures.PsdTools("gradient-sizes.psd"));
        var layer = document.Layers.First(l => l.ContentKey == "GdFl" && l.Name == "Rectangle 3");
        Assert.Equal(65, layer.VectorMask!.Bounds.Height);

        var stored = layer.Render();
        layer.Channels = layer.Channels.Where(c => c.Id < -1).ToList();
        var (max, _) = Deviation(layer.Render(), stored, layer.Bounds);

        Assert.True(max <= 3, $"max {max}");
    }

    [Fact]
    public void Gradient_fill_frame_snaps_the_line_to_pixel_centers()
    {
        var reflected = new Gradient { Type = GradientType.Reflected, AngleDegrees = 30 };
        reflected.ColorStops.Add(new GradientColorStop(0, PsdColor.Black, 0.5f));
        reflected.ColorStops.Add(new GradientColorStop(1, PsdColor.White, 0.5f));
        var frame = reflected.Frame(new GradientBox(0, 0, 4, 4), GradientSpan.CenterChord);

        // From (2.5, 2.5) to (4.5, 0.5): zero on the main diagonal, a quarter per pixel off it.
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                Assert.Equal(Math.Abs(x - y) / 4f, frame.Position(x, y), 5);
            }
        }

        // Linear at 100% snaps both ends of the chord; at other scales it mirrors the
        // snapped end about the snapped center (gradient-styles.psd, -60 degrees at 50%).
        var linear = new Gradient { Type = GradientType.Linear, AngleDegrees = -60, Scale = 0.5f };
        var scaled = linear.Frame(new GradientBox(0, 100, 32, 32), GradientSpan.CenterChord);
        Assert.Equal(0f, scaled.Position(12, 108), 5);
        Assert.Equal(1f, scaled.Position(20, 124), 5);
        Assert.Equal(0.5f, scaled.Position(16, 116), 5);
    }

    [Fact]
    public void Native_ramps_tolerate_positions_outside_the_ramp()
    {
        var gradient = Gradient.FromDescriptor(PsdDocument.Load(Fixtures.PsdTools("colormodes/4x4_16bit_cmyk.psd")).Layers.Single(l => l.ContentKey == "GdFl").FillDescriptor!, defaultAngle: 0)!;
        Assert.Equal(gradient.Color(0f, true), gradient.Color(float.NaN, true));
        Assert.Equal(gradient.Color(0f, true), gradient.Color(-3f, true));
        Assert.Equal(gradient.Color(1f, true), gradient.Color(float.PositiveInfinity, true));
    }

    [Fact]
    public void Layer_style_frames_snap_the_center_and_span_the_chord()
    {
        // Overlays keep the reference's snapped center and whole-pixel half ramp.
        var gradient = new Gradient { Type = GradientType.Linear, AngleDegrees = 0 };
        var bounds = new PsdRect(0, 0, 10, 4);
        Assert.Equal(0.5f, gradient.Position(bounds, 5, 0, GradientSpan.LayerProjection), 5);
        Assert.Equal(0f, gradient.Position(bounds, 0, 0, GradientSpan.LayerProjection), 5);
        Assert.Equal(0.9f, gradient.Position(bounds, 9, 3, GradientSpan.LayerProjection), 5);

        // At 87 degrees over 600x60 the chord is 60.08 px (half ramp 30); the corner-to-corner
        // projection would be 89 px. The ramp runs bottom to top across the 60 rows.
        var steep = new Gradient { Type = GradientType.Linear, AngleDegrees = 87 };
        var frame = steep.Frame(new GradientBox(0, 0, 600, 60), GradientSpan.LayerProjection);
        Assert.Equal(0.5f, frame.Position(300, 30), 5);
        Assert.InRange(frame.Position(300, 0), 0.97f, 1f);
        Assert.InRange(frame.Position(300, 59), 0f, 0.03f);
    }

    [Fact]
    public void Two_stop_overlay_ramps_ease_like_gradient_fills()
    {
        // Photoshop eases a two-stop overlay ramp (0.5t + 1.5t^2 - t^3) as it does a fill:
        // photoshop-overlay-zorder.psd went from max 13 to max 1 (RenderingTests), and the
        // gradient overlays of psd-tools' stroke-composite.psd from 46% to 4% of their
        // pixels off by more than 2 levels.
        var document = PsdDocument.Load(Fixtures.PsdTools("effects/stroke-composite.psd"));
        var merged = document.Render(new RenderOptions { Source = RenderSource.MergedImage });
        var layers = document.Render(new RenderOptions { Source = RenderSource.Layers });
        var overlaid = document.Layers.Where(l => l.Style is { Visible: true } style && style.ActiveEffects.Any(e => e.Kind == PsdLayerEffectKind.GradientOverlay)).ToList();
        Assert.Equal(2, overlaid.Count);
        foreach (var layer in overlaid)
        {
            var (_, outliers) = Deviation(layers, merged, layer.Bounds, threshold: 2);
            var area = layer.Bounds.Width * layer.Bounds.Height;
            Assert.True(outliers <= area * 0.06, $"{layer.Name} {layer.Bounds}: {outliers} of {area} pixels off by more than 2");
        }
    }

    [Fact]
    public void Dithered_gradient_fills_offset_each_pixel_by_the_document_hash()
    {
        var smooth = RenderFill(dither: false);
        var dithered = RenderFill(dither: true);
        var changed = 0;
        for (var y = 0; y < smooth.Height; y++)
        {
            for (var x = 0; x < smooth.Width; x++)
            {
                var a = smooth.GetPixel(x, y);
                var (r, _, _) = LayerCompositor.DitherGradientColor(a.R / 255f, a.G / 255f, a.B / 255f, x, y);
                Assert.Equal((byte)MathF.Round(r * 255f), dithered.GetPixel(x, y).R);
                changed += a == dithered.GetPixel(x, y) ? 0 : 1;
            }
        }

        Assert.True(changed > 0);
    }

    [Fact]
    public void Dithered_shape_gradient_fills_offset_each_pixel_by_the_document_hash()
    {
        // A stroked shape renders its gradient fill through the shape path.
        var smooth = RenderFill(dither: false, stroked: true);
        var dithered = RenderFill(dither: true, stroked: true);
        var changed = 0;
        for (var y = 6; y < 10; y++)
        {
            for (var x = 6; x < 26; x++)
            {
                var a = smooth.GetPixel(x, y);
                var (r, _, _) = LayerCompositor.DitherGradientColor(a.R / 255f, a.G / 255f, a.B / 255f, x, y);
                Assert.Equal((byte)MathF.Round(r * 255f), dithered.GetPixel(x, y).R);
                changed += a == dithered.GetPixel(x, y) ? 0 : 1;
            }
        }

        Assert.True(changed > 0);
    }

    // ---- Helpers ------------------------------------------------------------------------

    private static int StripGradientFillPixels(PsdDocument document)
    {
        var count = 0;
        foreach (var layer in document.Layers.Where(l => l.ContentKey == "GdFl" && l.Channels.Any(c => c.Id >= -1)))
        {
            layer.Channels = layer.Channels.Where(c => c.Id < -1).ToList();
            count++;
        }

        return count;
    }

    private static int FindFill(PsdDocument document, string name, int angle)
    {
        for (var i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.ContentKey == "GdFl" && layer.Name == name && layer.FillDescriptor is { } fill && (int)fill.GetNumber("Angl", 0) == angle)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"No {name} fill at {angle} degrees.");
    }

    /// <summary>Largest channel difference over white, and the pixels off by more than <paramref name="threshold"/>, inside <paramref name="region"/>.</summary>
    private static (int Max, int Outliers) Deviation(RgbaImage a, RgbaImage b, PsdRect region, int threshold = 3)
    {
        var fa = a.Flatten(PsdColor.White);
        var fb = b.Flatten(PsdColor.White);
        var max = 0;
        var outliers = 0;
        for (var y = Math.Max(0, region.Top); y < Math.Min(a.Height, region.Bottom); y++)
        {
            for (var x = Math.Max(0, region.Left); x < Math.Min(a.Width, region.Right); x++)
            {
                var p = fa.GetPixel(x, y);
                var q = fb.GetPixel(x, y);
                var delta = Math.Max(Math.Abs(p.R - q.R), Math.Max(Math.Abs(p.G - q.G), Math.Abs(p.B - q.B)));
                max = Math.Max(max, delta);
                outliers += delta > threshold ? 1 : 0;
            }
        }

        return (max, outliers);
    }

    /// <summary>A 32x16 document with a black-to-white 0 degree gradient fill (optionally a stroked shape).</summary>
    private static RgbaImage RenderFill(bool dither, bool stroked = false)
    {
        var builder = new PsdBuilder { Width = 32, Height = 16 };
        var layer = new BuilderLayer { Name = "Gradient", Rect = default };
        layer.Blocks.Add(("GdFl", FillDescriptor(dither, angle: 0).Versioned()));
        if (stroked)
        {
            layer.Blocks.Add(("vmsk", RectanglePath(32, 16, new PsdRect(4, 4, 28, 12))));
            var stroke = new DescriptorWriter("strokeStyle")
                .Add("strokeStyleVersion", 2)
                .Add("strokeEnabled", true)
                .Add("fillEnabled", true)
                .Add("strokeStyleLineWidth", DescriptorWriter.Pixels(1))
                .Add("strokeStyleContent", new DescriptorWriter("solidColorLayer").Add("Clr ", DescriptorWriter.Rgb(255, 0, 0)));
            layer.Blocks.Add(("vstk", stroke.Versioned()));
        }

        builder.Layers.Add(layer);
        return PsdDocument.Load(builder.Build()).Render(new RenderOptions { Source = RenderSource.Layers });
    }

    /// <summary>A black-to-white linear <c>GdFl</c> descriptor.</summary>
    private static DescriptorWriter FillDescriptor(bool dither, double angle)
    {
        return new DescriptorWriter("null")
            .Add("Dthr", dither)
            .Add("Angl", new DescriptorWriter.Unit("#Ang", angle))
            .Add("Type", new DescriptorWriter.EnumValue("GrdT", "Lnr "))
            .Add("Grad", new DescriptorWriter("Grdn")
                .Add("Nm  ", "Black, White")
                .Add("GrdF", new DescriptorWriter.EnumValue("GrdF", "CstS"))
                .Add("Intr", 4096.0)
                .Add("Clrs", new object[]
                {
                    new DescriptorWriter("Clrt").Add("Clr ", DescriptorWriter.Rgb(0, 0, 0)).Add("Type", new DescriptorWriter.EnumValue("Clry", "UsrS")).Add("Lctn", 0).Add("Mdpn", 50),
                    new DescriptorWriter("Clrt").Add("Clr ", DescriptorWriter.Rgb(255, 255, 255)).Add("Type", new DescriptorWriter.EnumValue("Clry", "UsrS")).Add("Lctn", 4096).Add("Mdpn", 50),
                })
                .Add("Trns", new object[]
                {
                    new DescriptorWriter("TrnS").Add("Opct", DescriptorWriter.Percent(100)).Add("Lctn", 0).Add("Mdpn", 50),
                    new DescriptorWriter("TrnS").Add("Opct", DescriptorWriter.Percent(100)).Add("Lctn", 4096).Add("Mdpn", 50),
                }));
    }

    /// <summary>A <c>vmsk</c> payload: one closed rectangle subpath in a <paramref name="width"/> x <paramref name="height"/> document.</summary>
    private static byte[] RectanglePath(int width, int height, PsdRect rect)
    {
        static void Record(PsdBuilder.Writer writer, ushort selector, byte[] body)
        {
            writer.U16(selector);
            writer.Bytes(body);
            writer.Zeros(24 - body.Length);
        }

        var writer = new PsdBuilder.Writer();
        writer.U32(3);
        writer.U32(0);
        Record(writer, 6, []);
        Record(writer, 8, [0, 0]);
        Record(writer, 0, [0, 4, 0, 1]);
        foreach (var (x, y) in new[] { (rect.Left, rect.Top), (rect.Right, rect.Top), (rect.Right, rect.Bottom), (rect.Left, rect.Bottom) })
        {
            var knot = new PsdBuilder.Writer();
            for (var i = 0; i < 3; i++)
            {
                knot.I32((int)((double)y / height * 16777216));
                knot.I32((int)((double)x / width * 16777216));
            }

            Record(writer, 2, knot.ToArray());
        }

        return writer.ToArray();
    }
}
