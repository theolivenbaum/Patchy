using System.Runtime.ExceptionServices;

namespace XRay.Psd;

/// <summary>
/// Row-strip parallelism for decode, render and encode loops. Every caller splits
/// independent items (rows, lines, channels) whose results do not depend on each
/// other, so the output is bit-identical to the sequential loop whatever the
/// degree of parallelism. Strip boundaries depend only on the item count and cost,
/// never on the thread count.
/// </summary>
/// <remarks>
/// The degree comes from an ambient scope (<see cref="Use(int)"/>), which
/// <see cref="Rendering.RenderOptions.MaxDegreeOfParallelism"/> sets for a render; it
/// flows into worker threads through the execution context. Outside a scope every
/// processor is used. A loop started from inside another parallel loop's body runs
/// inline, so nesting never oversubscribes. Exceptions surface unwrapped (the one
/// from the lowest strip), never as <see cref="AggregateException"/>.
/// </remarks>
internal static class Parallelism
{
    /// <summary>Approximate per-strip work (item count times cost) below which a strip is not worth a task.</summary>
    public const long MinimumStripWork = 1 << 16;

    private static readonly AsyncLocal<int> Degree = new();

    private static readonly AsyncLocal<long> StripWork = new();

    [ThreadStatic]
    private static bool insideStrip;

    /// <summary>The degree in effect: the innermost scope's, or the processor count.</summary>
    public static int MaxDegree => Degree.Value > 0 ? Degree.Value : Environment.ProcessorCount;

    /// <summary>
    /// Sets the degree of parallelism until the returned scope is disposed.
    /// Zero or negative means the processor count; 1 runs every loop sequentially.
    /// </summary>
    public static Scope Use(int maxDegree) => Use(maxDegree, 0);

    /// <summary>
    /// Also overrides the strip work threshold (0 keeps <see cref="MinimumStripWork"/>).
    /// Tests pass 1 so that even small fixtures split every loop into strips.
    /// </summary>
    internal static Scope Use(int maxDegree, long minimumStripWork)
    {
        var scope = new Scope(Degree.Value, StripWork.Value);
        Degree.Value = maxDegree > 0 ? maxDegree : Environment.ProcessorCount;
        StripWork.Value = minimumStripWork > 0 ? minimumStripWork : StripWork.Value;
        return scope;
    }

    /// <summary>
    /// Runs <paramref name="body"/>(start, end) over strips covering [0, <paramref name="count"/>).
    /// <paramref name="costPerItem"/> is a rough work estimate per item (pixels in a row,
    /// for instance) used only to size strips.
    /// </summary>
    public static void For(int count, long costPerItem, Action<int, int> body)
    {
        if (count <= 0)
        {
            return;
        }

        var strips = StripCount(count, costPerItem);
        var degree = MaxDegree;
        if (strips <= 1 || degree <= 1 || insideStrip)
        {
            body(0, count);
            return;
        }

        var errors = new Exception?[strips];
        var options = new ParallelOptions { MaxDegreeOfParallelism = degree };
        Parallel.For(0, strips, options, strip =>
        {
            var start = (int)((long)count * strip / strips);
            var end = (int)((long)count * (strip + 1) / strips);
            var outer = insideStrip;
            insideStrip = true;
            try
            {
                body(start, end);
            }
            catch (Exception ex)
            {
                errors[strip] = ex;
            }
            finally
            {
                insideStrip = outer;
            }
        });

        foreach (var error in errors)
        {
            if (error is not null)
            {
                ExceptionDispatchInfo.Capture(error).Throw();
            }
        }
    }

    /// <summary>
    /// Strip count from the work size alone (not the thread count), so the partition,
    /// and anything a caller derives from it, is the same on every machine.
    /// </summary>
    internal static int StripCount(int count, long costPerItem)
    {
        var work = (long)count * Math.Max(1, costPerItem);
        var strips = work / (StripWork.Value > 0 ? StripWork.Value : MinimumStripWork);
        return (int)Math.Clamp(strips, 1, Math.Min(count, 256));
    }

    /// <summary>Restores the previous degree on dispose.</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly int _degree;
        private readonly long _stripWork;

        internal Scope(int degree, long stripWork)
        {
            _degree = degree;
            _stripWork = stripWork;
        }

        public void Dispose()
        {
            Degree.Value = _degree;
            StripWork.Value = _stripWork;
        }
    }
}
