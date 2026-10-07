using XRay.Psd.Imaging;

namespace XRay.Psd.Rendering;

/// <summary>
/// Bevel and emboss, ported from the reference <c>render_bevel_emboss</c> and
/// <c>bevel_technique_height_mask</c>: a height field per technique (Smooth is the
/// tent-blurred matte, the chisels an exact distance roof), optional contour and
/// texture, then Lambert shading against the light with Photoshop's calibrated split
/// (highlight = excess over the flat face, normalized to the headroom; shadow = deficit
/// normalized to the floor; pillow and emboss shadows use the unnormalized deficit).
/// Stroke Emboss is not rendered yet.
/// </summary>
internal sealed partial class LayerCompositor
{
    private void RenderBevels(PlanarImage target, StyledLayer styled, MaskChain? masks, bool clipMode)
    {
        foreach (var bevel in styled.Effects.Bevels)
        {
            if (bevel.Style == BevelStyle.StrokeEmboss || (bevel.HighlightOpacity <= 0 && bevel.ShadowOpacity <= 0))
            {
                continue;
            }

            var (highlight, shadow) = BevelPlanes(styled, bevel);
            DrawEffect(target, styled, new Plane(styled.Domain, highlight), bevel.HighlightOpacity * styled.Opacity, bevel.HighlightColor, bevel.HighlightMode, masks, clipMode, null);
            DrawEffect(target, styled, new Plane(styled.Domain, shadow), bevel.ShadowOpacity * styled.Opacity, bevel.ShadowColor, bevel.ShadowMode, masks, clipMode, null);
        }
    }

    private static int TentPeak(float size) => size <= 0 ? 0 : Math.Max(2, (int)MathF.Round(size, MidpointRounding.AwayFromZero));

    private (float[] Highlight, float[] Shadow) BevelPlanes(StyledLayer styled, BevelEffect bevel)
    {
        var width = styled.Domain.Width;
        var height = styled.Domain.Height;
        var alpha = styled.Matte;
        var pillow = bevel.Style == BevelStyle.PillowEmboss;
        var pillowFamily = pillow || bevel.Style == BevelStyle.Emboss;

        var slopeGain = 1f;
        if (bevel.Contour is { IsLinear: true })
        {
            slopeGain = 1f / Math.Clamp(bevel.ContourRange, 0.01f, 1f);
        }

        var normalScale = pillowFamily
            ? 0.5f * Math.Clamp(bevel.Depth, 0.25f, 10f) * TentPeak(bevel.Size * 0.5f) * slopeGain
            : 0.5f * Math.Clamp(bevel.Depth, 0.01f, 10f) * Math.Max(1f, bevel.Size) * slopeGain;

        var heightField = HeightField(alpha, width, height, bevel, pillowFamily ? bevel.Size * 0.5f : bevel.Size);
        if (bevel.Contour is { IsLinear: false } contour)
        {
            var lut = contour.BuildLut();
            var range = Math.Clamp(bevel.ContourRange, 0.01f, 1f);
            for (var i = 0; i < heightField.Length; i++)
            {
                heightField[i] = StyleContour.Sample(lut, Math.Clamp(heightField[i] / range, 0f, 1f), bevel.ContourAntiAliased);
            }
        }

        if (bevel.Texture is { } texture && _document.Patterns.TryGetValue(texture.PatternId, out var tile))
        {
            ApplyTexture(styled, bevel, texture, tile, heightField, width, height);
        }

        var angle = (180f - bevel.AngleDegrees) * MathF.PI / 180f;
        var altitude = Math.Clamp(bevel.AltitudeDegrees, 0f, 90f) * MathF.PI / 180f;
        var horizontal = MathF.Cos(altitude);
        var lightX = -MathF.Cos(angle) * horizontal;
        var lightY = -MathF.Sin(angle) * horizontal;
        var lightZ = MathF.Sin(altitude);
        var gloss = bevel.Gloss.IsLinear ? null : bevel.Gloss.BuildLut();
        var direction = bevel.DirectionUp ? 1f : -1f;

        var highlights = new float[alpha.Length];
        var shadows = new float[alpha.Length];
        float At(int x, int y) => (uint)x < (uint)width && (uint)y < (uint)height ? heightField[(y * width) + x] : 0f;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var matte = Math.Clamp(alpha[index], 0f, 1f);
                var effectAlpha = bevel.Style switch
                {
                    BevelStyle.InnerBevel => matte,
                    BevelStyle.OuterBevel => 1f - matte,
                    _ => 1f,
                };
                if (effectAlpha <= 0)
                {
                    continue;
                }

                var left = At(x - 1, y);
                var right = At(x + 1, y);
                var top = At(x, y - 1);
                var bottom = At(x, y + 1);
                var baseX = (left - right) * normalScale;
                var baseY = (top - bottom) * normalScale;
                var flat = left == right && top == bottom;

                void Shade(float sign, float weight)
                {
                    if (weight <= 0)
                    {
                        return;
                    }

                    var gx = baseX * sign;
                    var gy = baseY * sign;
                    var length = MathF.Sqrt((gx * gx) + (gy * gy) + 1f);
                    var raw = (gx * lightX) + (gy * lightY) + lightZ;
                    var surface = raw / Math.Max(0.0001f, length);
                    var lighting = Split(surface, lightZ);
                    if (pillowFamily && raw < lightZ)
                    {
                        lighting = -((lightZ - raw) / Math.Max(0.01f, lightZ));
                    }

                    if (gloss is not null)
                    {
                        if (flat)
                        {
                            weight *= matte;
                            if (weight <= 0)
                            {
                                return;
                            }
                        }

                        var remapped = StyleContour.Sample(gloss, Math.Clamp(surface, 0f, 1f), bevel.GlossAntiAliased);
                        lighting = Split(remapped, lightZ);
                        if (pillowFamily && remapped < lightZ)
                        {
                            var remappedRaw = StyleContour.Sample(gloss, Math.Clamp(raw, 0f, 1f), bevel.GlossAntiAliased);
                            if (remappedRaw < lightZ)
                            {
                                lighting = -((lightZ - remappedRaw) / Math.Max(0.01f, lightZ));
                            }
                        }
                    }

                    if (lighting > 0)
                    {
                        highlights[index] = Math.Min(1f, highlights[index] + (Math.Min(1f, lighting) * weight));
                    }
                    else if (lighting < 0)
                    {
                        shadows[index] = Math.Min(1f, shadows[index] + (Math.Min(1f, -lighting) * weight));
                    }
                }

                if (pillow)
                {
                    // Both sides shade an anti-aliased edge: the exterior over the backdrop
                    // fraction, the flipped interior over the content fraction.
                    var baseSign = bevel.DirectionUp ? -1f : 1f;
                    Shade(baseSign, effectAlpha * (1f - matte));
                    Shade(-baseSign, effectAlpha * matte);
                }
                else
                {
                    Shade(direction, effectAlpha);
                }
            }
        }

        return (highlights, shadows);
    }

    private static float Split(float surface, float lightZ) => surface >= lightZ
        ? (surface - lightZ) / Math.Max(0.01f, 1f - lightZ)
        : -((lightZ - surface) / Math.Max(0.01f, lightZ));

    /// <summary>0 outside, 1 inside: Smooth blurs the matte with the tent, the chisels build an exact distance roof.</summary>
    private static float[] HeightField(float[] alpha, int width, int height, BevelEffect bevel, float size)
    {
        size = Math.Max(0.01f, size);
        float[] field;
        if (bevel.Technique == BevelTechnique.Smooth)
        {
            field = (float[])alpha.Clone();
            EffectMasks.TentBlur(field, width, height, TentPeak(size));
        }
        else
        {
            var toPainted = EffectMasks.DistanceField(alpha, width, height, sourcesArePainted: true);
            var toClear = EffectMasks.DistanceField(alpha, width, height, sourcesArePainted: false);
            field = new float[alpha.Length];
            for (var i = 0; i < alpha.Length; i++)
            {
                var a = Math.Clamp(alpha[i], 0f, 1f);
                var inside = 0.5f + (0.5f * Math.Clamp(toClear[i] / size, 0f, 1f));
                var outside = 0.5f - (0.5f * Math.Clamp(toPainted[i] / size, 0f, 1f));
                field[i] = (outside * (1f - a)) + (inside * a);
            }

            if (bevel.Technique == BevelTechnique.ChiselSoft)
            {
                EffectMasks.BoxBlur(field, width, height, 1, 1);
            }
        }

        if (bevel.Soften > 0)
        {
            EffectMasks.LayerStyleBlur(field, width, height, bevel.Soften);
        }

        for (var i = 0; i < field.Length; i++)
        {
            field[i] = Math.Clamp(field[i], 0f, 1f);
        }

        return field;
    }

    /// <summary>
    /// Texture embosses the face: pattern luminance (dark raised by default) is
    /// smoothed by a 3x3 box and added to the height field, weighted by the face.
    /// </summary>
    private static void ApplyTexture(StyledLayer styled, BevelEffect bevel, PatternPlacement texture, PatternTile tile, float[] heightField, int width, int height)
    {
        var sampler = new PatternSampler(tile, texture, styled.Layer);
        var bump = new float[heightField.Length];
        var domain = styled.Domain;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, _) = sampler.Sample(domain.Left + x, domain.Top + y);
                var luminance = (MathF.Round(r * 255f) * 299f + MathF.Round(g * 255f) * 590f + MathF.Round(b * 255f) * 111f) / 255000f;
                bump[(y * width) + x] = bevel.TextureInvert ? luminance - 0.5f : 0.5f - luminance;
            }
        }

        EffectMasks.BoxBlur(bump, width, height, 1, 1);
        var faceFromMatte = bevel.Style is BevelStyle.InnerBevel or BevelStyle.StrokeEmboss;
        for (var i = 0; i < heightField.Length; i++)
        {
            var coverage = faceFromMatte ? styled.Matte[i] : 1f - Math.Abs((Math.Clamp(heightField[i], 0f, 1f) * 2f) - 1f);
            heightField[i] += bump[i] * bevel.TextureDepth * Math.Clamp(coverage, 0f, 1f);
        }
    }
}
