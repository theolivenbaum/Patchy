namespace XRay.Psd.Text;

/// <summary>
/// Photoshop's Warp Text surface: a Bezier patch over the warp box, built from the style
/// presets exactly as the reference bakes them (<c>generate_style_warp_mesh</c> and
/// <c>apply_warp_distortion</c> in <c>.reference/src/core/warp_mesh.cpp</c>, pinned there
/// against Photoshop COM captures; see <c>.reference/docs/warp.md</c>).
/// </summary>
internal sealed class TextWarpMesh
{
    private TextWarpMesh(int uOrder, int vOrder, double[] xs, double[] ys)
    {
        UOrder = uOrder;
        VOrder = vOrder;
        Xs = xs;
        Ys = ys;
    }

    public int UOrder { get; }

    public int VOrder { get; }

    public double[] Xs { get; }

    public double[] Ys { get; }

    private double Left { get; init; }

    private double Top { get; init; }

    private double Width { get; init; }

    private double Height { get; init; }

    /// <summary>The warp for a style over the box (text space), or null for identity, unknown styles and empty boxes.</summary>
    public static TextWarpMesh? Create(TextWarp warp, TextBounds box)
    {
        if (warp.IsIdentity || box.IsEmpty || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
        {
            return null;
        }

        var style = warp.StyleKey;
        var vertical = warp.Orientation == TextOrientation.Vertical;
        var mesh = GenerateStyle(style, Finite(warp.Value), vertical, box.Width, box.Height);
        if (mesh is null)
        {
            return null;
        }

        mesh.ApplyDistortion(Finite(warp.Perspective), Finite(warp.PerspectiveOther));
        for (var i = 0; i < mesh.Xs.Length; i++)
        {
            mesh.Xs[i] += box.Left;
            mesh.Ys[i] += box.Top;
        }

        return new TextWarpMesh(mesh.UOrder, mesh.VOrder, mesh.Xs, mesh.Ys)
        {
            Left = box.Left,
            Top = box.Top,
            Width = box.Width,
            Height = box.Height,
        };
    }

    /// <summary>Maps a text-space point through the warp (the patch is evaluated past the box edges too).</summary>
    public (double X, double Y) Map(double x, double y) => Evaluate((x - Left) / Width, (y - Top) / Height);

    public (double X, double Y) Evaluate(double u, double v)
    {
        Span<double> wu = stackalloc double[4];
        Span<double> wv = stackalloc double[4];
        Bernstein(UOrder, u, wu);
        Bernstein(VOrder, v, wv);
        double x = 0, y = 0;
        for (var row = 0; row < VOrder; row++)
        {
            for (var column = 0; column < UOrder; column++)
            {
                var index = (row * UOrder) + column;
                var weight = wu[column] * wv[row];
                x += weight * Xs[index];
                y += weight * Ys[index];
            }
        }

        return (x, y);
    }

    private static double Finite(double value) => double.IsFinite(value) ? value : 0;

    private static void Bernstein(int order, double t, Span<double> weights)
    {
        var s = 1 - t;
        switch (order)
        {
            case 2:
                weights[0] = s;
                weights[1] = t;
                break;
            case 3:
                weights[0] = s * s;
                weights[1] = 2 * s * t;
                weights[2] = t * t;
                break;
            default:
                weights[0] = s * s * s;
                weights[1] = 3 * s * s * t;
                weights[2] = 3 * s * t * t;
                weights[3] = t * t * t;
                break;
        }
    }

    private static MeshBuilder? GenerateStyle(string style, double value, bool vertical, double width, double height)
    {
        if (!IsKnownStyle(style) || width <= 0 || height <= 0)
        {
            return null;
        }

        // Twist is orientation-invariant: Photoshop bakes the same mesh for Hrzn and Vrtc.
        return !vertical || style == "warpTwist" ? Horizontal(style, value, width, height) : Horizontal(style, value, height, width).SwapAxes();
    }

    private static bool IsKnownStyle(string style) => style is "warpArc" or "warpArch" or "warpBulge" or "warpFlag" or "warpWave" or "warpRise"
        or "warpArcLower" or "warpArcUpper" or "warpShellLower" or "warpShellUpper" or "warpFish" or "warpFisheye" or "warpInflate"
        or "warpSqueeze" or "warpTwist";

    private static MeshBuilder Horizontal(string style, double value, double width, double height)
    {
        var bend = Math.Clamp(value, -100, 100);
        var theta = Math.Abs(bend) * Math.PI / 200;
        var displacement = 2 * height * bend / 100;
        const double zeroBend = 1e-9;
        var mesh = new MeshBuilder(4, style == "warpWave" ? 3 : 2);
        switch (style)
        {
            case "warpArcLower" or "warpArcUpper":
            {
                if (Math.Abs(bend) < zeroBend)
                {
                    return MeshBuilder.Identity(width, height, 4, 2);
                }

                mesh.IdentityRow(width, 0);
                mesh.ArcRow(0, width, height, theta, bend < 0);
                return style == "warpArcUpper" ? mesh.MirrorVertically(height) : mesh;
            }

            case "warpFish":
                mesh.IdentityRow(width, 0);
                mesh.IdentityRow(width, height);
                mesh.Ys[1] -= displacement;
                mesh.Ys[2] += displacement;
                mesh.Ys[5] += displacement;
                mesh.Ys[6] -= displacement;
                return mesh;

            case "warpFisheye":
            {
                mesh = MeshBuilder.Identity(width, height, 4, 4);
                var t = bend / 50;
                for (var row = 1; row <= 2; row++)
                {
                    for (var column = 1; column <= 2; column++)
                    {
                        var index = (row * 4) + column;
                        var cornerX = column == 1 ? 0 : width;
                        var cornerY = row == 1 ? 0 : height;
                        mesh.Xs[index] += t * (cornerX - mesh.Xs[index]);
                        mesh.Ys[index] += t * (cornerY - mesh.Ys[index]);
                    }
                }

                return mesh;
            }

            case "warpInflate" or "warpSqueeze":
            {
                mesh = MeshBuilder.Identity(width, height, 3, 3);
                var dx = width * bend / 200;
                var dy = height * bend / 200;
                mesh.Ys[1] -= dy;
                mesh.Ys[7] += dy;
                if (style == "warpInflate")
                {
                    mesh.Xs[3] -= dx;
                    mesh.Xs[5] += dx;
                }
                else
                {
                    mesh.Xs[3] += dx;
                    mesh.Xs[5] -= dx;
                }

                return mesh;
            }

            case "warpTwist":
            {
                mesh = MeshBuilder.Identity(width, height, 4, 4);
                var moveX = width * Math.Abs(bend) / 100;
                var moveY = height * Math.Abs(bend) / 100;
                if (bend > 0)
                {
                    mesh.Xs[5] += moveX;
                    mesh.Ys[6] += moveY;
                    mesh.Xs[10] -= moveX;
                    mesh.Ys[9] -= moveY;
                }
                else if (bend < 0)
                {
                    mesh.Ys[5] += moveY;
                    mesh.Xs[6] -= moveX;
                    mesh.Ys[10] -= moveY;
                    mesh.Xs[9] += moveX;
                }

                return mesh;
            }

            case "warpShellLower" or "warpShellUpper":
            {
                if (Math.Abs(bend) < zeroBend)
                {
                    return MeshBuilder.Identity(width, height, 4, 4);
                }

                var sin = Math.Sin(theta);
                var cos = Math.Cos(theta);
                mesh = new MeshBuilder(4, 4);
                mesh.IdentityRow(width, 0);
                mesh.IdentityRow(width, height / 3);
                if (bend > 0)
                {
                    var radius = 2 * height / 3;
                    mesh.Row([-radius * sin, width / 3, 2 * width / 3, width + (radius * sin)], [radius * cos, 2 * height / 3, 2 * height / 3, radius * cos]);
                    mesh.ArcRow(-height * sin, width + (height * sin), height * cos, theta, false);
                }
                else
                {
                    var radius = height / 3;
                    mesh.Row([-radius * sin, width / 3, 2 * width / 3, width + (radius * sin)], [height - (radius * cos), 2 * height / 3, 2 * height / 3, height - (radius * cos)]);
                    mesh.ArcRow(0, width, height, theta, true);
                }

                return style == "warpShellUpper" ? mesh.MirrorVertically(height) : mesh;
            }

            case "warpArc":
            {
                if (Math.Abs(bend) < zeroBend)
                {
                    return MeshBuilder.Identity(width, height, 4, 2);
                }

                var sin = Math.Sin(theta);
                mesh.ArcRow(-height * sin, width + (height * sin), height * (1 - Math.Cos(theta)), theta, true);
                mesh.ArcRow(0, width, height, theta, true);
                return bend < 0 ? mesh.MirrorVertically(height) : mesh;
            }

            case "warpArch" or "warpBulge":
            {
                if (Math.Abs(bend) < zeroBend)
                {
                    return MeshBuilder.Identity(width, height, 4, 2);
                }

                var topUp = bend > 0;
                mesh.ArcRow(0, width, 0, theta, topUp);
                mesh.ArcRow(0, width, height, theta, style == "warpBulge" ? !topUp : topUp);
                return mesh;
            }

            case "warpFlag":
                mesh.IdentityRow(width, 0);
                mesh.IdentityRow(width, height);
                mesh.Ys[1] -= displacement;
                mesh.Ys[2] += displacement;
                mesh.Ys[5] -= displacement;
                mesh.Ys[6] += displacement;
                return mesh;

            case "warpWave":
                mesh.IdentityRow(width, 0);
                mesh.IdentityRow(width, height / 2);
                mesh.IdentityRow(width, height);
                mesh.Ys[5] += displacement;
                mesh.Ys[6] -= displacement;
                return mesh;

            default: // warpRise: rigid column ramp.
                mesh.IdentityRow(width, 0);
                mesh.IdentityRow(width, height);
                mesh.Ys[0] += displacement;
                mesh.Ys[1] += displacement;
                mesh.Ys[4] += displacement;
                mesh.Ys[5] += displacement;
                return mesh;
        }
    }

    private sealed class MeshBuilder(int uOrder, int vOrder)
    {
        private readonly List<double> _xs = [];
        private readonly List<double> _ys = [];
        private double[]? _xArray;
        private double[]? _yArray;

        public int UOrder { get; } = uOrder;

        public int VOrder { get; } = vOrder;

        public double[] Xs => _xArray ??= [.. _xs];

        public double[] Ys => _yArray ??= [.. _ys];

        public static MeshBuilder Identity(double width, double height, int uOrder, int vOrder)
        {
            var mesh = new MeshBuilder(uOrder, vOrder);
            for (var row = 0; row < vOrder; row++)
            {
                var fv = (double)row / (vOrder - 1);
                for (var column = 0; column < uOrder; column++)
                {
                    var fu = (double)column / (uOrder - 1);
                    mesh._xs.Add(width * fu);
                    mesh._ys.Add(height * fv);
                }
            }

            return mesh;
        }

        public void Row(double[] xs, double[] ys)
        {
            if (_xArray is not null || _yArray is not null)
            {
                // Keep edits made through the arrays before growing the lists again.
                _xs.Clear();
                _xs.AddRange(Xs);
                _ys.Clear();
                _ys.AddRange(Ys);
                _xArray = null;
                _yArray = null;
            }

            _xs.AddRange(xs);
            _ys.AddRange(ys);
        }

        public void IdentityRow(double width, double y) => Row([0, width / 3, 2 * width / 3, width], [y, y, y, y]);

        /// <summary>The cubic circle-arc row over the chord (ax,y)-(bx,y) with central angle 2 theta (reference <c>append_arc_row</c>).</summary>
        public void ArcRow(double ax, double bx, double y, double theta, bool bulgeUp)
        {
            var sin = Math.Sin(theta);
            var cos = Math.Cos(theta);
            var radius = (bx - ax) / (2 * sin);
            var handle = 4.0 / 3.0 * Math.Tan(theta / 2) * radius;
            var dy = bulgeUp ? -handle * sin : handle * sin;
            Row([ax, ax + (handle * cos), bx - (handle * cos), bx], [y, y + dy, y + dy, y]);
        }

        public MeshBuilder MirrorVertically(double height)
        {
            var mirrored = new MeshBuilder(UOrder, VOrder);
            for (var row = 0; row < VOrder; row++)
            {
                var source = VOrder - 1 - row;
                for (var column = 0; column < UOrder; column++)
                {
                    mirrored._xs.Add(Xs[(source * UOrder) + column]);
                    mirrored._ys.Add(height - Ys[(source * UOrder) + column]);
                }
            }

            return mirrored;
        }

        public MeshBuilder SwapAxes()
        {
            var swapped = new MeshBuilder(VOrder, UOrder);
            var xs = new double[Xs.Length];
            var ys = new double[Ys.Length];
            for (var row = 0; row < VOrder; row++)
            {
                for (var column = 0; column < UOrder; column++)
                {
                    var from = (row * UOrder) + column;
                    var to = (column * VOrder) + row;
                    xs[to] = Ys[from];
                    ys[to] = Xs[from];
                }
            }

            swapped._xs.AddRange(xs);
            swapped._ys.AddRange(ys);
            return swapped;
        }

        /// <summary>Photoshop's Horizontal/Vertical Distortion: rows scale about their edge midpoint first, then columns.</summary>
        public void ApplyDistortion(double horizontalPercent, double verticalPercent)
        {
            var vertical = Math.Clamp(verticalPercent, -100, 100);
            var horizontal = Math.Clamp(horizontalPercent, -100, 100);
            var xs = Xs;
            var ys = Ys;
            if (vertical != 0 && VOrder > 1)
            {
                for (var row = 0; row < VOrder; row++)
                {
                    var first = row * UOrder;
                    var last = first + UOrder - 1;
                    var midX = (xs[first] + xs[last]) / 2;
                    var midY = (ys[first] + ys[last]) / 2;
                    var scale = 1 + (((2.0 * row / (VOrder - 1)) - 1) * vertical / 100);
                    for (var column = 0; column < UOrder; column++)
                    {
                        xs[first + column] = midX + ((xs[first + column] - midX) * scale);
                        ys[first + column] = midY + ((ys[first + column] - midY) * scale);
                    }
                }
            }

            if (horizontal != 0 && UOrder > 1)
            {
                for (var column = 0; column < UOrder; column++)
                {
                    var first = column;
                    var last = ((VOrder - 1) * UOrder) + column;
                    var midX = (xs[first] + xs[last]) / 2;
                    var midY = (ys[first] + ys[last]) / 2;
                    var scale = 1 + (((2.0 * column / (UOrder - 1)) - 1) * horizontal / 100);
                    for (var row = 0; row < VOrder; row++)
                    {
                        var index = (row * UOrder) + column;
                        xs[index] = midX + ((xs[index] - midX) * scale);
                        ys[index] = midY + ((ys[index] - midY) * scale);
                    }
                }
            }
        }
    }
}
