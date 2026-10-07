namespace Patchy.Psd.Rendering;

/// <summary>
/// Mask operations behind the layer effects, ported from
/// .reference/src/render/layer_style_mask_ops.cpp: Photoshop's spread/choke
/// dilation, the exact separable tent blur, the soft exterior and interior
/// falloffs, and the exact Euclidean distance transform for strokes.
/// </summary>
internal static class EffectMasks
{
    private const float Unreached = 1.0e20f;
    private const int ExactDilationRadiusLimit = 8;

    /// <summary>
    /// Grayscale dilation by an integer radius: max of value times the
    /// area-sampled disc coverage. Radii above 8 use a distance band instead.
    /// </summary>
    public static void DilateByRadius(float[] mask, int width, int height, int radius)
    {
        if (radius <= 0 || width <= 0 || height <= 0)
        {
            return;
        }

        if (radius > ExactDilationRadiusLimit)
        {
            DistanceBandDilate(mask, width, height, radius);
            return;
        }

        var reach = radius + 1;
        var weights = new List<(int Dx, int Dy, float Weight)>();
        for (var dy = -reach; dy <= reach; dy++)
        {
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (dx == 0 && dy == 0)
                {
                    continue;
                }

                var coverage = Math.Clamp(radius + 1.0 - Math.Sqrt((dx * dx) + (dy * dy)), 0.0, 1.0);
                if (coverage > 0)
                {
                    weights.Add((dx, dy, (float)coverage));
                }
            }
        }

        var dilated = (float[])mask.Clone();
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var index = (y * width) + x;
                var value = dilated[index];
                foreach (var (dx, dy, weight) in weights)
                {
                    var sx = x + dx;
                    var sy = y + dy;
                    if ((uint)sx >= (uint)width || (uint)sy >= (uint)height)
                    {
                        continue;
                    }

                    var candidate = mask[(sy * width) + sx] * weight;
                    if (candidate > value)
                    {
                        value = candidate;
                    }
                }

                dilated[index] = value;
            }
        }

        dilated.AsSpan().CopyTo(mask);
    }

    // Large-radius fallback: a hard band radius + 1 px ramp around pixels at or above half coverage.
    private static void DistanceBandDilate(float[] mask, int width, int height, int radius)
    {
        var field = new float[mask.Length];
        for (var i = 0; i < mask.Length; i++)
        {
            field[i] = mask[i] >= 0.5f ? 0f : Unreached;
        }

        SquaredDistanceTransform(field, width, height);
        for (var i = 0; i < mask.Length; i++)
        {
            var band = Math.Clamp(radius + 1f - MathF.Sqrt(field[i]), 0f, 1f);
            mask[i] = Math.Max(mask[i], band);
        }
    }

    /// <summary>The exact separable tent [1..N..1]/N² per axis (Photoshop's Satin and soft-effect blur), via prefix sums.</summary>
    public static void TentBlur(float[] mask, int width, int height, int peak)
    {
        if (peak < 2 || width <= 0 || height <= 0)
        {
            return;
        }

        var horizontal = new float[mask.Length];
        ConvolveLines(mask, horizontal, height, width, width, 1, peak);
        ConvolveLines(horizontal, mask, width, height, 1, width, peak);
    }

    private static void ConvolveLines(float[] input, float[] output, int lineCount, int lineLength, int lineStep, int sampleStep, int peak)
    {
        var prefix = new double[lineLength + 1];
        var weighted = new double[lineLength + 1];
        var divisor = (double)peak * peak;
        for (var line = 0; line < lineCount; line++)
        {
            var baseIndex = line * lineStep;
            for (var p = 0; p < lineLength; p++)
            {
                double value = input[baseIndex + (p * sampleStep)];
                prefix[p + 1] = prefix[p] + value;
                weighted[p + 1] = weighted[p] + (p * value);
            }

            for (var p = 0; p < lineLength; p++)
            {
                var left = Math.Max(0, p - peak + 1);
                var right = Math.Min(lineLength, p + peak);
                var leftSum = prefix[p + 1] - prefix[left];
                var leftWeighted = weighted[p + 1] - weighted[left];
                var rightSum = prefix[right] - prefix[p + 1];
                var rightWeighted = weighted[right] - weighted[p + 1];
                var numerator = ((peak - p) * leftSum) + leftWeighted + ((peak + p) * rightSum) - rightWeighted;
                output[baseIndex + (p * sampleStep)] = (float)(numerator / divisor);
            }
        }
    }

    /// <summary>
    /// Drop shadow and outer glow ("Softer") falloff: spread dilates by
    /// round(spread% × size), then the remaining size blurs with the tent
    /// N = max(2, round(size)) − spread radius.
    /// </summary>
    public static void SoftExterior(float[] mask, int width, int height, float size, float spread)
    {
        var roundedSize = (int)MathF.Round(Math.Max(0f, size), MidpointRounding.AwayFromZero);
        var spreadRadius = (int)MathF.Round(Math.Max(0f, size) * Math.Clamp(spread / 100f, 0f, 1f), MidpointRounding.AwayFromZero);
        DilateByRadius(mask, width, height, spreadRadius);
        if (roundedSize > 0)
        {
            var peak = Math.Max(2, roundedSize) - spreadRadius;
            if (peak >= 2)
            {
                TentBlur(mask, width, height, peak);
            }
        }
    }

    /// <summary>
    /// Inner shadow and inner glow falloff: the inverse matte dilates by the
    /// choke radius and blurs with the remaining tent. 1 at the contour, fading inward.
    /// </summary>
    public static void SoftInterior(float[] mask, int width, int height, float size, float choke)
    {
        for (var i = 0; i < mask.Length; i++)
        {
            mask[i] = 1f - Math.Clamp(mask[i], 0f, 1f);
        }

        SoftExterior(mask, width, height, size, choke);
    }

    /// <summary>Felzenszwalb-Huttenlocher exact squared Euclidean distance transform, in place (0 marks sources).</summary>
    public static void SquaredDistanceTransform(float[] field, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var n = Math.Max(width, height);
        var f = new float[n];
        var d = new float[n];
        var v = new int[n];
        var z = new double[n + 1];
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                f[y] = field[(y * width) + x];
            }

            Transform1D(f, d, v, z, height);
            for (var y = 0; y < height; y++)
            {
                field[(y * width) + x] = d[y];
            }
        }

        for (var y = 0; y < height; y++)
        {
            field.AsSpan(y * width, width).CopyTo(f);
            Transform1D(f, d, v, z, width);
            d.AsSpan(0, width).CopyTo(field.AsSpan(y * width, width));
        }
    }

    private static void Transform1D(float[] f, float[] d, int[] v, double[] z, int n)
    {
        var k = 0;
        v[0] = 0;
        z[0] = -1.0e30;
        z[1] = 1.0e30;
        for (var q = 1; q < n; q++)
        {
            var fq = f[q] + ((double)q * q);
            var intersection = (fq - (f[v[k]] + ((double)v[k] * v[k]))) / ((2.0 * q) - (2.0 * v[k]));
            while (intersection <= z[k])
            {
                k--;
                intersection = (fq - (f[v[k]] + ((double)v[k] * v[k]))) / ((2.0 * q) - (2.0 * v[k]));
            }

            k++;
            v[k] = q;
            z[k] = intersection;
            z[k + 1] = 1.0e30;
        }

        k = 0;
        for (var q = 0; q < n; q++)
        {
            while (z[k + 1] < q)
            {
                k++;
            }

            var dx = (float)(q - v[k]);
            d[q] = (dx * dx) + f[v[k]];
        }
    }

    /// <summary>
    /// Distances from each pixel center to the matte's half-coverage contour,
    /// measured on a 3x supersampled bilinear contour (the reference's subpixel
    /// stroke fields). <paramref name="outside"/> measures to the nearest covered
    /// sample, <paramref name="inside"/> to the nearest uncovered one.
    /// </summary>
    public static void StrokeDistanceFields(float[] matte, int width, int height, bool needOutside, bool needInside, out float[]? outside, out float[]? inside)
    {
        outside = null;
        inside = null;
        const int scale = 3;
        const long maxSupersampled = 1024L * 1024L;
        if ((long)width * height > maxSupersampled)
        {
            var contour = new float[matte.Length];
            for (var i = 0; i < matte.Length; i++)
            {
                contour[i] = matte[i] >= 0.5f ? 1f : 0f;
            }

            if (needOutside)
            {
                outside = DistanceField(contour, width, height, sourcesArePainted: true);
            }

            if (needInside)
            {
                inside = DistanceField(contour, width, height, sourcesArePainted: false);
            }

            return;
        }

        var fineWidth = width * scale;
        var fineHeight = height * scale;
        var fine = new float[fineWidth * fineHeight];
        for (var fy = 0; fy < fineHeight; fy++)
        {
            var cy = ((fy + 0.5f) / scale) - 0.5f;
            var y0 = Math.Clamp((int)MathF.Floor(cy), 0, height - 1);
            var y1 = Math.Min(y0 + 1, height - 1);
            var ty = Math.Clamp(cy - y0, 0f, 1f);
            for (var fx = 0; fx < fineWidth; fx++)
            {
                var cx = ((fx + 0.5f) / scale) - 0.5f;
                var x0 = Math.Clamp((int)MathF.Floor(cx), 0, width - 1);
                var x1 = Math.Min(x0 + 1, width - 1);
                var tx = Math.Clamp(cx - x0, 0f, 1f);
                var top = matte[(y0 * width) + x0] + ((matte[(y0 * width) + x1] - matte[(y0 * width) + x0]) * tx);
                var bottom = matte[(y1 * width) + x0] + ((matte[(y1 * width) + x1] - matte[(y1 * width) + x0]) * tx);
                fine[(fy * fineWidth) + fx] = top + ((bottom - top) * ty) >= 0.5f ? 1f : 0f;
            }
        }

        const float compensation = 0.5f - (0.5f / scale);
        float[] ReadBack(float[] fineDistance)
        {
            var result = new float[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var distance = fineDistance[(((y * scale) + 1) * fineWidth) + (x * scale) + 1];
                    result[(y * width) + x] = distance <= 0f ? 0f : (distance / scale) + compensation;
                }
            }

            return result;
        }

        if (needOutside)
        {
            outside = ReadBack(DistanceField(fine, fineWidth, fineHeight, sourcesArePainted: true));
        }

        if (needInside)
        {
            inside = ReadBack(DistanceField(fine, fineWidth, fineHeight, sourcesArePainted: false));
        }
    }

    /// <summary>Euclidean distance from each pixel to the nearest painted (value &gt; 0) or unpainted pixel.</summary>
    public static float[] DistanceField(float[] contour, int width, int height, bool sourcesArePainted)
    {
        var field = new float[contour.Length];
        for (var i = 0; i < contour.Length; i++)
        {
            field[i] = (contour[i] > 0f) == sourcesArePainted ? 0f : Unreached;
        }

        SquaredDistanceTransform(field, width, height);
        for (var i = 0; i < field.Length; i++)
        {
            field[i] = MathF.Sqrt(field[i]);
        }

        return field;
    }

    /// <summary>Count-normalized running box blur, edge-truncated (reference <c>box_blur_mask_into</c>), repeated <paramref name="passes"/> times.</summary>
    public static void BoxBlur(float[] mask, int width, int height, int radius, int passes)
    {
        if (radius <= 0 || passes <= 0)
        {
            return;
        }

        var horizontal = new float[mask.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            BoxPass(mask, horizontal, width, height, radius);
        }
    }

    /// <summary>The layer-style falloff blur: up to three box passes splitting ceil(size) (reference <c>blur_layer_style_mask_in_place</c>).</summary>
    public static void LayerStyleBlur(float[] mask, int width, int height, float size)
    {
        var support = Math.Max(0, (int)MathF.Ceiling(Math.Max(0f, size)));
        if (support <= 0)
        {
            return;
        }

        var passes = Math.Min(3, support);
        var baseRadius = support / passes;
        var extra = support % passes;
        var horizontal = new float[mask.Length];
        for (var pass = 0; pass < passes; pass++)
        {
            var radius = baseRadius + (pass < extra ? 1 : 0);
            if (radius > 0)
            {
                BoxPass(mask, horizontal, width, height, radius);
            }
        }
    }

    private static void BoxPass(float[] mask, float[] horizontal, int width, int height, int radius)
    {
        for (var y = 0; y < height; y++)
        {
            var sum = 0f;
            var count = 0;
            for (var x = -radius; x <= radius; x++)
            {
                if (x >= 0 && x < width)
                {
                    sum += mask[(y * width) + x];
                    count++;
                }
            }

            for (var x = 0; x < width; x++)
            {
                horizontal[(y * width) + x] = sum / Math.Max(1, count);
                var remove = x - radius;
                var add = x + radius + 1;
                if (remove >= 0 && remove < width)
                {
                    sum -= mask[(y * width) + remove];
                    count--;
                }

                if (add >= 0 && add < width)
                {
                    sum += mask[(y * width) + add];
                    count++;
                }
            }
        }

        for (var x = 0; x < width; x++)
        {
            var sum = 0f;
            var count = 0;
            for (var y = -radius; y <= radius; y++)
            {
                if (y >= 0 && y < height)
                {
                    sum += horizontal[(y * width) + x];
                    count++;
                }
            }

            for (var y = 0; y < height; y++)
            {
                mask[(y * width) + x] = sum / Math.Max(1, count);
                var remove = y - radius;
                var add = y + radius + 1;
                if (remove >= 0 && remove < height)
                {
                    sum -= horizontal[(remove * width) + x];
                    count--;
                }

                if (add >= 0 && add < height)
                {
                    sum += horizontal[(add * width) + x];
                    count++;
                }
            }
        }
    }
}
