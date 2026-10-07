using System.Buffers.Binary;

namespace XRay.Psd.Layers;

/// <summary>How a subpath group combines with the coverage accumulated before it.</summary>
public enum PathCombineOperation
{
    Xor = 0,
    Add = 1,
    Subtract = 2,
    Intersect = 3,
}

/// <summary>A bezier knot in document pixel coordinates.</summary>
public readonly record struct PathKnot(double InX, double InY, double X, double Y, double OutX, double OutY, bool Smooth);

/// <summary>One contour of a vector path.</summary>
public sealed class PathSubpath
{
    public bool Closed { get; init; }

    public PathCombineOperation Operation { get; init; } = PathCombineOperation.Add;

    /// <summary>Subpaths sharing a group index fill together under the even-odd rule.</summary>
    public int ShapeGroup { get; init; }

    public List<PathKnot> Knots { get; } = [];
}

/// <summary>
/// A vector path from a <c>vmsk</c>/<c>vsms</c> layer block or a path image
/// resource. Coordinates are converted to document pixels on load.
/// </summary>
public sealed class VectorPath
{
    private const int RecordSize = 26;

    public IReadOnlyList<PathSubpath> Subpaths { get; init; } = [];

    /// <summary>Selector 8: when set, coverage starts from "all pixels" (Photoshop's initial fill record).</summary>
    public bool InitialFillAll { get; init; }

    /// <summary>Vector mask flag: the path is inverted.</summary>
    public bool Inverted { get; init; }

    /// <summary>Vector mask flag: the mask is not linked to the layer.</summary>
    public bool Unlinked { get; init; }

    /// <summary>Vector mask flag: the mask is disabled.</summary>
    public bool Disabled { get; init; }

    /// <summary>Parses a <c>vmsk</c>/<c>vsms</c> payload: version, flags, then path records.</summary>
    internal static VectorPath? ParseVectorMask(ReadOnlySpan<byte> payload, int documentWidth, int documentHeight)
    {
        if (payload.Length < 8)
        {
            return null;
        }

        var flags = BinaryPrimitives.ReadUInt32BigEndian(payload[4..]);
        return ParseRecords(payload[8..], documentWidth, documentHeight, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0);
    }

    /// <summary>Parses a raw path record stream (path image resources 2000-2997).</summary>
    internal static VectorPath? ParseRecords(ReadOnlySpan<byte> records, int documentWidth, int documentHeight, bool inverted = false, bool unlinked = false, bool disabled = false)
    {
        var subpaths = new List<PathSubpath>();
        PathSubpath? current = null;
        var expected = 0;
        var initialFill = false;
        for (var offset = 0; offset + RecordSize <= records.Length; offset += RecordSize)
        {
            var record = records.Slice(offset, RecordSize);
            var selector = BinaryPrimitives.ReadUInt16BigEndian(record);
            switch (selector)
            {
                case 0:
                case 3:
                    {
                        if (current is not null && current.Knots.Count != expected)
                        {
                            return null;
                        }

                        expected = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
                        int operation = BinaryPrimitives.ReadUInt16BigEndian(record[4..]);
                        var group = BinaryPrimitives.ReadInt32BigEndian(record[12..]);
                        if (operation == 0xFFFF)
                        {
                            // A continuation contour of a compound group inherits the group's
                            // operation; CS4-era files leave it unset and fill by parity (Xor).
                            operation = current is not null && current.ShapeGroup == group ? (int)current.Operation : 0;
                        }

                        if (operation > 3)
                        {
                            return null;
                        }

                        current = new PathSubpath { Closed = selector == 0, Operation = (PathCombineOperation)operation, ShapeGroup = group };
                        subpaths.Add(current);
                        break;
                    }

                case 1:
                case 2:
                case 4:
                case 5:
                    {
                        if (current is null || current.Knots.Count >= expected)
                        {
                            return null;
                        }

                        static double Fixed(ReadOnlySpan<byte> r, int at, int extent) => BinaryPrimitives.ReadInt32BigEndian(r[at..]) / 16777216.0 * extent;
                        current.Knots.Add(new PathKnot(
                            Fixed(record, 6, documentWidth),
                            Fixed(record, 2, documentHeight),
                            Fixed(record, 14, documentWidth),
                            Fixed(record, 10, documentHeight),
                            Fixed(record, 22, documentWidth),
                            Fixed(record, 18, documentHeight),
                            selector is 1 or 4));
                        break;
                    }

                case 6:
                case 7:
                    break;
                case 8:
                    initialFill = BinaryPrimitives.ReadUInt16BigEndian(record[2..]) == 1;
                    break;
                default:
                    return null;
            }
        }

        if (current is not null && current.Knots.Count != expected)
        {
            return null;
        }

        return new VectorPath { Subpaths = subpaths, InitialFillAll = initialFill, Inverted = inverted, Unlinked = unlinked, Disabled = disabled };
    }

    /// <summary>Bounding box of all anchors and control points, in pixels (rounded outward).</summary>
    public PsdRect Bounds
    {
        get
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var subpath in Subpaths)
            {
                foreach (var k in subpath.Knots)
                {
                    minX = Math.Min(minX, Math.Min(k.X, Math.Min(k.InX, k.OutX)));
                    minY = Math.Min(minY, Math.Min(k.Y, Math.Min(k.InY, k.OutY)));
                    maxX = Math.Max(maxX, Math.Max(k.X, Math.Max(k.InX, k.OutX)));
                    maxY = Math.Max(maxY, Math.Max(k.Y, Math.Max(k.InY, k.OutY)));
                }
            }

            return minX > maxX ? default : new PsdRect((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX), (int)Math.Ceiling(maxY));
        }
    }
}
