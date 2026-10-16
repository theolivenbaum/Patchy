namespace XRay.Psd.Rendering;

/// <summary>
/// Shadow and glow transfer contours (<c>TrnS</c>, <c>AntA</c>) and gradient glows,
/// which the reference does not model (it renders them as Linear and solid color).
/// Calibrated against Photoshop's merged image of psd-tools' <c>layer_params.psd</c>;
/// see docs/rendering.md, "Contours and gradient glows".
/// </summary>
internal sealed partial class LayerCompositor
{
    /// <summary>
    /// Maps a shadow or glow falloff through its contour: after spread, blur and the
    /// glow Range gain, before the falloff becomes the effect's alpha. Anti-aliasing
    /// interpolates between the table's entries.
    /// </summary>
    private static void ApplyContour(float[] plane, StyleContour? contour, bool antiAliased)
    {
        if (contour is null)
        {
            return;
        }

        var lut = contour.BuildLut();
        for (var i = 0; i < plane.Length; i++)
        {
            if (plane[i] > 0f)
            {
                plane[i] = StyleContour.Sample(lut, plane[i], antiAliased);
            }
        }
    }

    /// <summary>
    /// A gradient glow paints the gradient across its falloff f (after the contour):
    /// color and opacity come from position <c>1 - f</c>, so the gradient's first stop
    /// sits where the glow is strongest (the layer edge of an outer glow). The falloff
    /// only marks where the glow reaches (above one 8-bit level), and the
    /// gradient's own opacity stops give the alpha. <paramref name="plane"/> is
    /// replaced by that reach.
    /// </summary>
    private static EffectPaint? GlowPaint(GlowEffect glow, float[] plane, PsdRect domain)
    {
        if (glow.Gradient is not { } gradient)
        {
            return null;
        }

        var falloff = (float[])plane.Clone();
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = plane[i] * 255f > 1f ? 1f : 0f;
        }

        return (x, y) =>
        {
            var position = 1f - Math.Clamp(falloff[((y - domain.Top) * domain.Width) + (x - domain.Left)], 0f, 1f);
            if (gradient.Reverse)
            {
                position = 1f - position;
            }

            var (r, g, b) = gradient.Color(position, endpointSmoothing: false);
            return (r, g, b, gradient.Opacity(position, endpointSmoothing: false));
        };
    }
}
