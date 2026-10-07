using System.Numerics;

namespace XRay.Psd.Imaging.Icc;

/// <summary>Settings for building an <see cref="IccSrgbTransform"/>.</summary>
internal readonly record struct IccTransformSettings(IccRenderingIntent Intent, bool BlackPointCompensation)
{
    /// <summary>
    /// What the reference (<c>.reference/src/color/color_management.cpp</c>) asks
    /// lcms2 for, and Photoshop's Convert to Profile default: relative
    /// colorimetric with black point compensation.
    /// </summary>
    public static IccTransformSettings Default => new(IccRenderingIntent.RelativeColorimetric, true);
}

/// <summary>
/// Converts device colors described by an ICC profile (gray, RGB or CMYK) to
/// gamma-encoded sRGB, following Little CMS semantics without porting it:
/// device to PCS through the intent's <c>A2Bx</c> LUT (falling back to
/// <c>A2B0</c>) or the matrix/TRC model, PCS to XYZ (D50), black point
/// compensation, then the Bradford-adapted sRGB matrix and transfer curve.
/// <para>
/// Planar conversion uses tables built once per depth: per-channel 1D tables
/// for gray and matrix/TRC RGB (exact for 8- and 16-bit input), and sampled
/// grids for LUT profiles (17 nodes per axis for CMYK with Little CMS's 4D
/// interpolation, 33 for three-channel LUTs), which is how Little CMS
/// optimizes the same transforms.
/// </para>
/// </summary>
internal sealed class IccSrgbTransform
{
    private const int CmykGridPoints = 17;
    private const int RgbGridPoints = 33;
    private const int ParallelThreshold = 1 << 18;
    private const int ParallelChunk = 1 << 16;

    // v4 perceptual reference black (ICC.1:2010, Little CMS cmsPERCEPTUAL_BLACK_*).
    private static readonly IccXyz PerceptualBlack = new(0.00336, 0.0034731, 0.00287);

    // The LUT evaluated for grids; null for gray TRC and matrix/shaper conversions.
    private readonly IccLut? _lut;

    // Matrix/shaper RGB: per-channel curves, then a 3x3 matrix and offset giving XYZ (D50).
    private readonly IccCurve[]? _shaperCurves;
    private readonly double[] _shaperMatrix = new double[9];
    private readonly double[] _shaperOffset = new double[3];
    private readonly double[] _bpcScale = [1, 1, 1];
    private readonly object _gate = new();

    // Absolute colorimetric with a media white other than D50 tints even a gray TRC.
    private readonly bool _tintedGray;
    private Tables? _tables8;
    private Tables? _tables16;
    private Tables? _tables32;

    private IccSrgbTransform(IccProfile profile, int channels, IccTransformSettings settings)
    {
        Profile = profile;
        Channels = channels;
        Settings = settings;
        _lut = profile.InputLut(settings.Intent);
        UsesLut = _lut is not null;
        if (_lut is null && channels == 3)
        {
            var r = profile.RedColorant!.Value;
            var g = profile.GreenColorant!.Value;
            var b = profile.BlueColorant!.Value;
            double[] colorants = [r.X, g.X, b.X, r.Y, g.Y, b.Y, r.Z, g.Z, b.Z];
            colorants.CopyTo(_shaperMatrix, 0);
            _shaperCurves = [profile.RedTrc!, profile.GreenTrc!, profile.BlueTrc!];
        }
        else if (_lut is not null && channels == 3 && profile.Pcs == IccProfile.SpaceXyz && _lut.TryGetMatrixShaper(out var curves, out var matrix, out var offset))
        {
            // Evaluated exactly rather than sampled on a grid: for the Adobe RGB v4 test profile
            // Little CMS's grid drifts up to 21 levels where colors clip at the sRGB gamut edge.
            const double scale = 65535.0 / 32768.0;
            for (var i = 0; i < 9; i++)
            {
                _shaperMatrix[i] = matrix[i] * scale;
            }

            for (var i = 0; i < 3; i++)
            {
                _shaperOffset[i] = offset[i] * scale;
            }

            _shaperCurves = curves;
            _lut = null;
        }

        var bpc = settings.BlackPointCompensation && settings.Intent != IccRenderingIntent.AbsoluteColorimetric;

        // Little CMS turns BPC on for v4 profiles under the perceptual and saturation intents.
        if (profile.MajorVersion >= 4 && settings.Intent is IccRenderingIntent.Perceptual or IccRenderingIntent.Saturation)
        {
            bpc = true;
        }

        BlackPoint = bpc ? DetectBlackPoint(settings.Intent) : default;
        if (bpc && (BlackPoint.X > 0 || BlackPoint.Y > 0 || BlackPoint.Z > 0))
        {
            // Little CMS ComputeBlackPointCompensation with a zero destination black (sRGB).
            _bpcScale[0] = IccPcs.D50.X / (IccPcs.D50.X - BlackPoint.X);
            _bpcScale[1] = IccPcs.D50.Y / (IccPcs.D50.Y - BlackPoint.Y);
            _bpcScale[2] = IccPcs.D50.Z / (IccPcs.D50.Z - BlackPoint.Z);
        }
        else
        {
            BlackPoint = default;
        }

        if (settings.Intent == IccRenderingIntent.AbsoluteColorimetric)
        {
            // Little CMS ComputeAbsoluteIntent with a fully adapted observer: scale the relative PCS by
            // the source media white over the destination's (the built-in sRGB stores D50).
            var white = MediaWhite(profile);
            _bpcScale[0] *= white.X / IccPcs.D50.X;
            _bpcScale[1] *= white.Y / IccPcs.D50.Y;
            _bpcScale[2] *= white.Z / IccPcs.D50.Z;
            _tintedGray = white != IccPcs.D50;
        }

        IsSrgbEquivalent = channels == 3 && CheckSrgbEquivalence();
    }

    public IccProfile Profile { get; }

    /// <summary>Device channels: 1 (gray), 3 (RGB) or 4 (CMYK).</summary>
    public int Channels { get; }

    public IccTransformSettings Settings { get; }

    /// <summary>The source black point used for black point compensation (zero when none applies).</summary>
    public IccXyz BlackPoint { get; }

    /// <summary>True when the conversion comes from an A2Bx LUT rather than the matrix/TRC tags.</summary>
    public bool UsesLut { get; }

    /// <summary>True for RGB conversions that run as curves plus a matrix (matrix/TRC tags, or a CLUT-free <c>mAB</c>).</summary>
    public bool IsMatrixShaper => _shaperCurves is not null;

    /// <summary>
    /// True for RGB profiles that reproduce sRGB within one 8-bit level
    /// (the common embedded "sRGB IEC61966-2.1"); callers skip the conversion so
    /// those documents render exactly as before.
    /// </summary>
    public bool IsSrgbEquivalent { get; }

    /// <summary>
    /// The transform for a document's embedded profile (resource 1039), or null
    /// when there is none, it does not fit the color mode, it cannot be used, or
    /// it is equivalent to sRGB. Gray covers grayscale and duotone (the reference
    /// converts duotone through its gray profile too); indexed palettes are RGB.
    /// </summary>
    public static IccSrgbTransform? ForDocument(PsdDocument document)
    {
        var channels = document.ColorMode switch
        {
            PsdColorMode.Rgb or PsdColorMode.Indexed => 3,
            PsdColorMode.Cmyk => 4,
            PsdColorMode.Grayscale or PsdColorMode.Duotone => 1,
            _ => 0,
        };
        if (channels == 0 || document.GetImageResource(ImageResourceIds.IccProfile) is not { } resource)
        {
            return null;
        }

        try
        {
            var profile = IccProfile.Parse(resource.Data.Span);
            var transform = profile is null ? null : Create(profile, channels, IccTransformSettings.Default);
            return transform is null || transform.IsSrgbEquivalent ? null : transform;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Builds a transform for <paramref name="channels"/> device channels, or
    /// returns null when the profile cannot convert them (wrong color space,
    /// device link, missing tags).
    /// </summary>
    public static IccSrgbTransform? Create(IccProfile profile, int channels, IccTransformSettings settings)
    {
        var expected = channels switch
        {
            1 => IccProfile.SpaceGray,
            3 => IccProfile.SpaceRgb,
            4 => IccProfile.SpaceCmyk,
            _ => 0u,
        };
        if (expected == 0 || profile.ColorSpace != expected)
        {
            return null;
        }

        if (profile.DeviceClass is not (IccProfile.ClassInput or IccProfile.ClassDisplay or IccProfile.ClassOutput or IccProfile.ClassColorSpace))
        {
            return null;
        }

        if (profile.Pcs is not (IccProfile.SpaceXyz or IccProfile.SpaceLab))
        {
            return null;
        }

        if (profile.InputLut(settings.Intent) is null && !(channels != 4 && profile.IsMatrixShaper))
        {
            return null;
        }

        try
        {
            return new IccSrgbTransform(profile, channels, settings);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Device values (ICC convention: CMYK is ink amount, 1 = full ink) to PCS XYZ (D50), before black point compensation.</summary>
    public IccXyz DeviceToPcs(ReadOnlySpan<double> device) => DeviceToPcs(device, _lut);

    /// <summary>Device values to unclamped linear sRGB (after black point compensation).</summary>
    public (double R, double G, double B) EvaluateLinear(ReadOnlySpan<double> device)
    {
        var xyz = DeviceToPcs(device, _lut);
        xyz = new IccXyz(
            _bpcScale[0] * (xyz.X - BlackPoint.X),
            _bpcScale[1] * (xyz.Y - BlackPoint.Y),
            _bpcScale[2] * (xyz.Z - BlackPoint.Z));
        if (_lut is null && Channels == 1 && !_tintedGray)
        {
            // A gray TRC maps onto the neutral axis; keep it exactly neutral.
            return (xyz.Y, xyz.Y, xyz.Y);
        }

        return IccPcs.XyzToLinearSrgb(xyz);
    }

    /// <summary>Device values to clamped, gamma-encoded sRGB in [0,1].</summary>
    public (double R, double G, double B) Evaluate(ReadOnlySpan<double> device)
    {
        var (r, g, b) = EvaluateLinear(device);
        return (IccPcs.EncodeSrgb(r), IccPcs.EncodeSrgb(g), IccPcs.EncodeSrgb(b));
    }

    /// <summary>
    /// Converts decoded PSD planes (PSD conventions: CMYK stored inverted, 1 = no
    /// ink) at <paramref name="depth"/> into the image's RGB planes. Returns
    /// false when this depth is not color managed (32-bit gray, LUT and CMYK).
    /// </summary>
    public bool Apply(float[][] planes, int depth, PlanarImage image)
    {
        var tables = GetTables(depth);
        if (tables is null)
        {
            return false;
        }

        var count = image.R.Length;
        if (count < ParallelThreshold)
        {
            ApplyRange(tables, planes, image, 0, count);
        }
        else
        {
            // Pixels are independent, so chunking never changes the output.
            var chunks = (count + ParallelChunk - 1) / ParallelChunk;
            Parallel.For(0, chunks, chunk => ApplyRange(tables, planes, image, chunk * ParallelChunk, Math.Min(count, (chunk + 1) * ParallelChunk)));
        }

        return true;
    }

    /// <summary>
    /// The media white Little CMS uses for the absolute intent (<c>_cmsReadMediaWhitePoint</c>):
    /// the <c>wtpt</c> tag, D50 when it is missing, and D50 for v2 display profiles.
    /// </summary>
    private static IccXyz MediaWhite(IccProfile profile)
    {
        if (profile.MediaWhitePoint is not { } white || (profile.MajorVersion < 4 && profile.DeviceClass == IccProfile.ClassDisplay))
        {
            return IccPcs.D50;
        }

        return white.X > 0 && white.Y > 0 && white.Z > 0 && double.IsFinite(white.X + white.Y + white.Z) ? white : IccPcs.D50;
    }

    private IccXyz DeviceToPcs(ReadOnlySpan<double> device, IccLut? lut)
    {
        if (lut is not null)
        {
            Span<double> pcs = stackalloc double[3];
            lut.Evaluate(device, pcs);
            return IccPcs.Decode(Profile.Pcs, lut.LabEncoding, pcs);
        }

        if (Channels == 1)
        {
            var value = Profile.GrayTrc!.Evaluate(device[0]);

            // With a Lab PCS the gray curve gives L*/100 (ICC.1 gray model), otherwise luminance.
            return Profile.Pcs == IccProfile.SpaceLab
                ? IccPcs.LabToXyz(value * 100.0, 0, 0)
                : new IccXyz(IccPcs.D50.X * value, IccPcs.D50.Y * value, IccPcs.D50.Z * value);
        }

        var curves = _shaperCurves!;
        var r = curves[0].Evaluate(device[0]);
        var g = curves[1].Evaluate(device[1]);
        var b = curves[2].Evaluate(device[2]);
        var m = _shaperMatrix;
        var o = _shaperOffset;
        return new IccXyz(
            (m[0] * r) + (m[1] * g) + (m[2] * b) + o[0],
            (m[3] * r) + (m[4] * g) + (m[5] * b) + o[1],
            (m[6] * r) + (m[7] * g) + (m[8] * b) + o[2]);
    }

    /// <summary>
    /// Little CMS <c>cmsDetectBlackPoint</c> for an input profile: the v4
    /// perceptual black, the perceptual-black round trip for v2 CMYK output
    /// profiles under relative colorimetric, or the darkest colorant forced
    /// neutral.
    /// </summary>
    private IccXyz DetectBlackPoint(IccRenderingIntent intent)
    {
        if (Profile.MajorVersion >= 4 && intent is IccRenderingIntent.Perceptual or IccRenderingIntent.Saturation)
        {
            return Profile.IsMatrixShaper ? BlackFromDarkestColorant(IccRenderingIntent.RelativeColorimetric) : PerceptualBlack;
        }

        if (intent == IccRenderingIntent.RelativeColorimetric && Profile.DeviceClass == IccProfile.ClassOutput && Profile.ColorSpace == IccProfile.SpaceCmyk)
        {
            return BlackFromPerceptualRoundTrip();
        }

        return BlackFromDarkestColorant(intent);
    }

    private IccXyz BlackFromDarkestColorant(IccRenderingIntent intent)
    {
        var supported = Profile.GetAToB(intent == IccRenderingIntent.AbsoluteColorimetric ? 1 : (int)intent) is not null || (Channels != 4 && Profile.IsMatrixShaper);
        if (!supported)
        {
            return default;
        }

        Span<double> darkest = stackalloc double[Channels];
        darkest.Fill(Channels == 4 ? 1.0 : 0.0);
        var (l, _, _) = IccPcs.XyzToLab(DeviceToPcs(darkest, Profile.InputLut(intent)));
        if (l is > 50 or < 0 || double.IsNaN(l))
        {
            l = 0;
        }

        return IccPcs.LabToXyz(l, 0, 0);
    }

    /// <summary>Lab black to the device through <c>B2A0</c>, back through the relative LUT, forced neutral and capped at L* 50.</summary>
    private IccXyz BlackFromPerceptualRoundTrip()
    {
        var toDevice = Profile.GetBToA(0);
        var toPcs = Profile.InputLut(IccRenderingIntent.RelativeColorimetric);
        if (toDevice is null || toPcs is null)
        {
            return default;
        }

        Span<double> pcs = stackalloc double[3];
        Span<double> device = stackalloc double[IccLut.MaxChannels];
        IccPcs.Encode(Profile.Pcs, toDevice.LabEncoding, IccPcs.LabToXyz(0, 0, 0), pcs);
        toDevice.Evaluate(pcs, device);
        var (l, _, _) = IccPcs.XyzToLab(DeviceToPcs(device[..Channels], toPcs));
        if (double.IsNaN(l))
        {
            l = 0;
        }

        return IccPcs.LabToXyz(Math.Min(l, 50), 0, 0);
    }

    private bool CheckSrgbEquivalence()
    {
        // One 8-bit level over a 9^3 grid: the HP "sRGB IEC61966-2.1" profile maps
        // pure green to an encoded red of 0.5 levels (its colorants differ from
        // the D65-derived ones in the fourth decimal), while gamma 2.2, Adobe RGB
        // or Display P3 miss by several levels.
        const int steps = 9;
        const double tolerance = 1.0 / 255.0;
        Span<double> device = stackalloc double[3];
        for (var r = 0; r < steps; r++)
        {
            for (var g = 0; g < steps; g++)
            {
                for (var b = 0; b < steps; b++)
                {
                    device[0] = r / (double)(steps - 1);
                    device[1] = g / (double)(steps - 1);
                    device[2] = b / (double)(steps - 1);
                    var (outR, outG, outB) = Evaluate(device);
                    if (Math.Abs(outR - device[0]) > tolerance || Math.Abs(outG - device[1]) > tolerance || Math.Abs(outB - device[2]) > tolerance)
                    {
                        return false;
                    }
                }
            }
        }

        // The grid misses the shadow end of the tone curves, where gamma 2.2 and sRGB differ most.
        for (var level = 1; level < 32; level++)
        {
            device[0] = device[1] = device[2] = level / 255.0;
            var (outR, outG, outB) = Evaluate(device);
            if (Math.Abs(outR - device[0]) > tolerance || Math.Abs(outG - device[1]) > tolerance || Math.Abs(outB - device[2]) > tolerance)
            {
                return false;
            }
        }

        return true;
    }

    private Tables? GetTables(int depth)
    {
        ref var slot = ref depth == 8 ? ref _tables8 : ref depth == 16 ? ref _tables16 : ref _tables32;
        var tables = Volatile.Read(ref slot);
        if (tables is null)
        {
            lock (_gate)
            {
                tables = slot ?? BuildTables(depth);
                Volatile.Write(ref slot, tables);
            }
        }

        return tables.Kind == TableKind.None ? null : tables;
    }

    private Tables BuildTables(int depth)
    {
        var levels = depth switch
        {
            8 => 256,
            16 => 65536,
            _ => 0,
        };
        Span<double> device = stackalloc double[4];
        if (_lut is null && !(Channels == 1 && _tintedGray))
        {
            if (Channels == 1)
            {
                if (levels == 0)
                {
                    return Tables.None;
                }

                var gray = new float[levels];
                for (var i = 0; i < levels; i++)
                {
                    device[0] = i / (double)(levels - 1);
                    gray[i] = (float)Evaluate(device[..1]).R;
                }

                return new Tables { Kind = TableKind.Gray, MaxIndex = levels - 1, Red = gray };
            }

            // Matrix/TRC RGB: linearize per channel (32-bit data is already linear), then one matrix.
            float[]? red = null, green = null, blue = null;
            if (levels > 0)
            {
                var curves = _shaperCurves!;
                red = new float[levels];
                green = new float[levels];
                blue = new float[levels];
                for (var i = 0; i < levels; i++)
                {
                    var x = i / (double)(levels - 1);
                    red[i] = (float)curves[0].Evaluate(x);
                    green[i] = (float)curves[1].Evaluate(x);
                    blue[i] = (float)curves[2].Evaluate(x);
                }
            }

            // linear sRGB = S * diag(scale) * (C * rgb + offset - black)
            var scaled = new double[9];
            var colorants = _shaperMatrix;
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    scaled[(row * 3) + column] = _bpcScale[row] * colorants[(row * 3) + column];
                }
            }

            var matrix = IccPcs.Multiply3x3(IccPcs.XyzD50ToSrgb, scaled);
            var s = IccPcs.XyzD50ToSrgb;
            var shift = new IccXyz(
                _bpcScale[0] * (_shaperOffset[0] - BlackPoint.X),
                _bpcScale[1] * (_shaperOffset[1] - BlackPoint.Y),
                _bpcScale[2] * (_shaperOffset[2] - BlackPoint.Z));
            var coefficients = new float[12];
            for (var row = 0; row < 3; row++)
            {
                for (var column = 0; column < 3; column++)
                {
                    coefficients[(row * 3) + column] = (float)matrix[(row * 3) + column];
                }

                coefficients[9 + row] = (float)((s[row * 3] * shift.X) + (s[(row * 3) + 1] * shift.Y) + (s[(row * 3) + 2] * shift.Z));
            }

            return new Tables { Kind = TableKind.Matrix, MaxIndex = Math.Max(levels - 1, 0), Red = red, Green = green, Blue = blue, Coefficients = coefficients };
        }

        if (depth == 32 && Channels != 4)
        {
            // 32-bit gray and RGB hold linear light; their LUT profiles describe encoded values.
            return Tables.None;
        }

        if (Channels == 1)
        {
            var size = levels > 0 ? levels : 4096;
            var grid = new float[size * 3];
            for (var i = 0; i < size; i++)
            {
                device[0] = i / (double)(size - 1);
                var (r, g, b) = Evaluate(device[..1]);
                grid[i * 3] = (float)r;
                grid[(i * 3) + 1] = (float)g;
                grid[(i * 3) + 2] = (float)b;
            }

            return new Tables { Kind = TableKind.GrayLut, MaxIndex = size - 1, GridPoints = size, Grid = grid };
        }

        if (Channels == 3)
        {
            return new Tables { Kind = TableKind.Grid3, MaxIndex = RgbGridPoints - 1, GridPoints = RgbGridPoints, Grid = SampleGrid(3, RgbGridPoints) };
        }

        return new Tables { Kind = TableKind.Grid4, MaxIndex = CmykGridPoints - 1, GridPoints = CmykGridPoints, Grid = SampleGrid(4, CmykGridPoints) };
    }

    /// <summary>
    /// Samples the whole conversion on a regular grid indexed by PSD sample
    /// values (for CMYK the PSD value is one minus the ink amount).
    /// </summary>
    private float[] SampleGrid(int dimensions, int points)
    {
        var total = 1;
        for (var d = 0; d < dimensions; d++)
        {
            total *= points;
        }

        // One slab per value of the first input; nodes are independent, so the slabs run in parallel.
        var grid = new float[total * 3];
        var slab = total / points;
        Parallel.For(0, points, first =>
        {
            Span<double> device = stackalloc double[dimensions];
            Span<int> index = stackalloc int[dimensions];
            for (var node = first * slab; node < (first + 1) * slab; node++)
            {
                var rest = node;
                for (var d = dimensions - 1; d >= 0; d--)
                {
                    index[d] = rest % points;
                    rest /= points;
                }

                for (var d = 0; d < dimensions; d++)
                {
                    var value = index[d] / (double)(points - 1);
                    device[d] = dimensions == 4 ? 1.0 - value : value;
                }

                var (r, g, b) = Evaluate(device);
                grid[node * 3] = (float)r;
                grid[(node * 3) + 1] = (float)g;
                grid[(node * 3) + 2] = (float)b;
            }
        });

        return grid;
    }

    private static void ApplyRange(Tables tables, float[][] planes, PlanarImage image, int start, int end)
    {
        switch (tables.Kind)
        {
            case TableKind.Gray:
                ApplyGray(tables, planes[0], image, start, end);
                break;
            case TableKind.GrayLut:
                ApplyGrayLut(tables, planes[0], image, start, end);
                break;
            case TableKind.Matrix:
                ApplyMatrix(tables, planes, image, start, end);
                break;
            case TableKind.Grid3:
                ApplyGrid3(tables, planes, image, start, end);
                break;
            case TableKind.Grid4:
                ApplyGrid4(tables, planes, image, start, end);
                break;
        }
    }

    private static void ApplyGray(Tables tables, float[] gray, PlanarImage image, int start, int end)
    {
        var table = tables.Red!;
        var scale = (float)tables.MaxIndex;
        var maxIndex = tables.MaxIndex;
        for (var i = start; i < end; i++)
        {
            var index = Math.Clamp((int)((gray[i] * scale) + 0.5f), 0, maxIndex);
            var value = table[index];
            image.R[i] = value;
            image.G[i] = value;
            image.B[i] = value;
        }
    }

    private static void ApplyMatrix(Tables tables, float[][] planes, PlanarImage image, int start, int end)
    {
        float[] r = image.R, g = image.G, b = image.B;
        if (tables.Red is { } redTable)
        {
            float[] greenTable = tables.Green!, blueTable = tables.Blue!;
            var scale = (float)tables.MaxIndex;
            var maxIndex = tables.MaxIndex;
            float[] sourceR = planes[0], sourceG = planes[1], sourceB = planes[2];
            for (var i = start; i < end; i++)
            {
                r[i] = redTable[Math.Clamp((int)((sourceR[i] * scale) + 0.5f), 0, maxIndex)];
                g[i] = greenTable[Math.Clamp((int)((sourceG[i] * scale) + 0.5f), 0, maxIndex)];
                b[i] = blueTable[Math.Clamp((int)((sourceB[i] * scale) + 0.5f), 0, maxIndex)];
            }
        }
        else
        {
            planes[0].AsSpan(start, end - start).CopyTo(r.AsSpan(start));
            planes[1].AsSpan(start, end - start).CopyTo(g.AsSpan(start));
            planes[2].AsSpan(start, end - start).CopyTo(b.AsSpan(start));
        }

        var length = end - start;
        MultiplyMatrix(tables.Coefficients!, r.AsSpan(start, length), g.AsSpan(start, length), b.AsSpan(start, length));
        SrgbEncoder.EncodeInPlace(r.AsSpan(start, length));
        SrgbEncoder.EncodeInPlace(g.AsSpan(start, length));
        SrgbEncoder.EncodeInPlace(b.AsSpan(start, length));
    }

    /// <summary>Applies the 3x3 matrix plus offset to three planes in place, clamping to [0,1].</summary>
    private static void MultiplyMatrix(float[] m, Span<float> r, Span<float> g, Span<float> b)
    {
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var zero = Vector<float>.Zero;
            var one = Vector<float>.One;
            for (; i <= r.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                var vr = new Vector<float>(r[i..]);
                var vg = new Vector<float>(g[i..]);
                var vb = new Vector<float>(b[i..]);
                var outR = (vr * m[0]) + (vg * m[1]) + (vb * m[2]) + new Vector<float>(m[9]);
                var outG = (vr * m[3]) + (vg * m[4]) + (vb * m[5]) + new Vector<float>(m[10]);
                var outB = (vr * m[6]) + (vg * m[7]) + (vb * m[8]) + new Vector<float>(m[11]);
                Vector.Min(Vector.Max(outR, zero), one).CopyTo(r[i..]);
                Vector.Min(Vector.Max(outG, zero), one).CopyTo(g[i..]);
                Vector.Min(Vector.Max(outB, zero), one).CopyTo(b[i..]);
            }
        }

        for (; i < r.Length; i++)
        {
            float vr = r[i], vg = g[i], vb = b[i];
            r[i] = Math.Clamp((vr * m[0]) + (vg * m[1]) + (vb * m[2]) + m[9], 0f, 1f);
            g[i] = Math.Clamp((vr * m[3]) + (vg * m[4]) + (vb * m[5]) + m[10], 0f, 1f);
            b[i] = Math.Clamp((vr * m[6]) + (vg * m[7]) + (vb * m[8]) + m[11], 0f, 1f);
        }
    }

    /// <summary>A gray LUT profile sampled per input level (or 4096 steps), linear between entries.</summary>
    private static void ApplyGrayLut(Tables tables, float[] gray, PlanarImage image, int start, int end)
    {
        var grid = tables.Grid!;
        var max = tables.MaxIndex;
        float[] r = image.R, g = image.G, b = image.B;
        for (var i = start; i < end; i++)
        {
            Locate(gray[i], max, out var index, out var t);
            var at = index * 3;
            r[i] = grid[at] + ((grid[at + 3] - grid[at]) * t);
            g[i] = grid[at + 1] + ((grid[at + 4] - grid[at + 1]) * t);
            b[i] = grid[at + 2] + ((grid[at + 5] - grid[at + 2]) * t);
        }
    }

    private static void ApplyGrid3(Tables tables, float[][] planes, PlanarImage image, int start, int end)
    {
        var grid = tables.Grid!;
        var max = tables.MaxIndex;
        float[] r = image.R, g = image.G, b = image.B;
        var points = tables.GridPoints;
        int sz = 3, sy = points * 3, sx = points * points * 3;
        float[] p0 = planes[0], p1 = planes[1], p2 = planes[2];
        for (var i = start; i < end; i++)
        {
            Locate(p0[i], max, out var ix, out var fx);
            Locate(p1[i], max, out var iy, out var fy);
            Locate(p2[i], max, out var iz, out var fz);
            Tetrahedral(grid, (ix * sx) + (iy * sy) + (iz * sz), sx, sy, sz, fx, fy, fz, out r[i], out g[i], out b[i]);
        }
    }

    private static void ApplyGrid4(Tables tables, float[][] planes, PlanarImage image, int start, int end)
    {
        var grid = tables.Grid!;
        var max = tables.MaxIndex;
        var points = tables.GridPoints;
        int sk = 3, sy = points * 3, sm = points * points * 3, sc = points * points * points * 3;
        float[] c = planes[0], m = planes[1], y = planes[2], k = planes[3];
        float[] r = image.R, g = image.G, b = image.B;
        for (var i = start; i < end; i++)
        {
            Locate(c[i], max, out var ic, out var fc);
            Locate(m[i], max, out var im, out var fm);
            Locate(y[i], max, out var iy, out var fy);
            Locate(k[i], max, out var ik, out var fk);
            var origin = (ic * sc) + (im * sm) + (iy * sy) + (ik * sk);
            Tetrahedral(grid, origin, sm, sy, sk, fm, fy, fk, out var r0, out var g0, out var b0);
            if (fc > 0)
            {
                Tetrahedral(grid, origin + sc, sm, sy, sk, fm, fy, fk, out var r1, out var g1, out var b1);
                r0 += (r1 - r0) * fc;
                g0 += (g1 - g0) * fc;
                b0 += (b1 - b0) * fc;
            }

            r[i] = r0;
            g[i] = g0;
            b[i] = b0;
        }
    }

    private static void Locate(float value, int max, out int index, out float fraction)
    {
        var position = (value > 0f ? (value < 1f ? value : 1f) : 0f) * max;
        index = Math.Min((int)position, max - 1);
        fraction = position - index;
    }

    private static void Tetrahedral(float[] v, int o, int sx, int sy, int sz, float rx, float ry, float rz, out float r, out float g, out float b)
    {
        int a1, a2, a3;

        // The three corners walked from the origin along the fractional order (Little CMS's six tetrahedra).
        if (rx >= ry && ry >= rz)
        {
            (a1, a2, a3) = (sx, sx + sy, sx + sy + sz);
            Combine(v, o, a1, a2, a3, rx, ry, rz, out r, out g, out b);
        }
        else if (rx >= rz && rz >= ry)
        {
            (a1, a2, a3) = (sx, sx + sz, sx + sy + sz);
            Combine(v, o, a1, a2, a3, rx, rz, ry, out r, out g, out b);
        }
        else if (rz >= rx && rx >= ry)
        {
            (a1, a2, a3) = (sz, sx + sz, sx + sy + sz);
            Combine(v, o, a1, a2, a3, rz, rx, ry, out r, out g, out b);
        }
        else if (ry >= rx && rx >= rz)
        {
            (a1, a2, a3) = (sy, sx + sy, sx + sy + sz);
            Combine(v, o, a1, a2, a3, ry, rx, rz, out r, out g, out b);
        }
        else if (ry >= rz && rz >= rx)
        {
            (a1, a2, a3) = (sy, sy + sz, sx + sy + sz);
            Combine(v, o, a1, a2, a3, ry, rz, rx, out r, out g, out b);
        }
        else
        {
            (a1, a2, a3) = (sz, sy + sz, sx + sy + sz);
            Combine(v, o, a1, a2, a3, rz, ry, rx, out r, out g, out b);
        }
    }

    /// <summary>Barycentric blend along the path origin, origin+a1, origin+a2, origin+a3 with sorted fractions t1 &gt;= t2 &gt;= t3.</summary>
    private static void Combine(float[] v, int o, int a1, int a2, int a3, float t1, float t2, float t3, out float r, out float g, out float b)
    {
        float w0 = 1f - t1, w1 = t1 - t2, w2 = t2 - t3, w3 = t3;
        r = (w0 * v[o]) + (w1 * v[o + a1]) + (w2 * v[o + a2]) + (w3 * v[o + a3]);
        g = (w0 * v[o + 1]) + (w1 * v[o + a1 + 1]) + (w2 * v[o + a2 + 1]) + (w3 * v[o + a3 + 1]);
        b = (w0 * v[o + 2]) + (w1 * v[o + a1 + 2]) + (w2 * v[o + a2 + 2]) + (w3 * v[o + a3 + 2]);
    }

    private enum TableKind
    {
        None,
        Gray,
        GrayLut,
        Matrix,
        Grid3,
        Grid4,
    }

    /// <summary>Lookup data for one input depth.</summary>
    private sealed class Tables
    {
        public static Tables None { get; } = new();

        public TableKind Kind { get; init; }

        /// <summary>Largest table index (levels - 1) or grid index (points - 1).</summary>
        public int MaxIndex { get; init; }

        public int GridPoints { get; init; }

        public float[]? Red { get; init; }

        public float[]? Green { get; init; }

        public float[]? Blue { get; init; }

        /// <summary>Matrix (row major) followed by the three offsets.</summary>
        public float[]? Coefficients { get; init; }

        /// <summary>Interleaved RGB outputs per node.</summary>
        public float[]? Grid { get; init; }
    }
}

/// <summary>Linear-light to sRGB-encoded floats through a 4096-segment table (error under 2e-5).</summary>
internal static class SrgbEncoder
{
    private const int Segments = 4096;
    private static readonly float[] Table = BuildTable();

    public static void EncodeInPlace(Span<float> values)
    {
        var table = Table;
        for (var i = 0; i < values.Length; i++)
        {
            var position = (values[i] > 0f ? (values[i] < 1f ? values[i] : 1f) : 0f) * Segments;
            var index = Math.Min((int)position, Segments - 1);
            var t = position - index;
            values[i] = table[index] + ((table[index + 1] - table[index]) * t);
        }
    }

    private static float[] BuildTable()
    {
        var table = new float[Segments + 1];
        for (var i = 0; i <= Segments; i++)
        {
            table[i] = (float)IccPcs.EncodeSrgb(i / (double)Segments);
        }

        return table;
    }
}
