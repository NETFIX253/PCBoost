using PCBoost.Core.Models.Gaming;

namespace PCBoost.Gaming.Metrics;

/// <summary>
/// Calcul des statistiques d'images à partir des horodatages de présentation (millisecondes, horloge monotone).
/// Définitions (voir docs/GAMING.md) :
/// <list type="bullet">
/// <item>intervalle = horodatage(i) − horodatage(i−1) ; les intervalles ≤ 0 ms ou &gt; 5000 ms (pause, jeu réduit) sont ignorés ;</item>
/// <item>FPS moyens = (n − 1) × 1000 / durée, où n − 1 est le nombre d'intervalles retenus et la durée leur somme ;</item>
/// <item>1 % low = 1000 / moyenne des 1 % d'intervalles les plus longs (au moins un) ; 0,1 % low : idem avec 0,1 % ;</item>
/// <item>temps d'image moyen = moyenne des intervalles ; P99 = 99e centile des intervalles (rang le plus proche).</item>
/// </list>
/// Moins de deux images (aucun intervalle valide) → <see cref="FrameStats.Empty"/>.
/// </summary>
public static class FrameMetricsCalculator
{
    /// <summary>Intervalle maximal pris en compte (au-delà : pause, chargement, jeu réduit).</summary>
    public const double MaxIntervalMs = 5000;

    public const double OnePercent = 0.01;
    public const double PointOnePercent = 0.001;

    /// <summary>Statistiques à partir des horodatages de présentation (dans l'ordre de présentation).</summary>
    public static FrameStats Calculate(IReadOnlyList<double> presentTimestampsMs)
    {
        ArgumentNullException.ThrowIfNull(presentTimestampsMs);
        return FromIntervals(ToIntervals(presentTimestampsMs));
    }

    /// <summary>Intervalles valides (0 &lt; d ≤ 5000 ms) entre horodatages consécutifs.</summary>
    public static List<double> ToIntervals(IReadOnlyList<double> presentTimestampsMs)
    {
        ArgumentNullException.ThrowIfNull(presentTimestampsMs);
        var intervals = new List<double>(Math.Max(0, presentTimestampsMs.Count - 1));
        for (var i = 1; i < presentTimestampsMs.Count; i++)
        {
            var d = presentTimestampsMs[i] - presentTimestampsMs[i - 1];
            if (IsValidInterval(d)) intervals.Add(d);
        }
        return intervals;
    }

    public static bool IsValidInterval(double intervalMs) => intervalMs > 0 && intervalMs <= MaxIntervalMs && double.IsFinite(intervalMs);

    /// <summary>Statistiques à partir d'intervalles (les intervalles invalides sont ignorés).</summary>
    public static FrameStats FromIntervals(IEnumerable<double> intervalsMs)
    {
        ArgumentNullException.ThrowIfNull(intervalsMs);
        var sorted = intervalsMs.Where(IsValidInterval).ToArray();
        if (sorted.Length == 0) return FrameStats.Empty;
        Array.Sort(sorted);

        var count = sorted.Length;
        var total = 0d;
        foreach (var d in sorted) total += d;
        var mean = total / count;

        return new FrameStats(
            AverageFps: count * 1000d / total,
            OnePercentLowFps: 1000d / MeanOfLongest(sorted, LowCount(count, OnePercent)),
            PointOnePercentLowFps: 1000d / MeanOfLongest(sorted, LowCount(count, PointOnePercent)),
            AverageFrameTimeMs: mean,
            P99FrameTimeMs: sorted[PercentileIndex(count, 0.99)],
            FrameCount: count + 1,
            Duration: TimeSpan.FromMilliseconds(total));
    }

    /// <summary>Nombre d'intervalles retenus pour un « x % low » : ⌈n × x⌉, au moins 1.</summary>
    public static int LowCount(int intervalCount, double fraction)
        => Math.Max(1, (int)Math.Ceiling(intervalCount * fraction - 1e-9));

    /// <summary>Index (base 0, tri croissant) du centile par la méthode du rang le plus proche.</summary>
    public static int PercentileIndex(int count, double percentile)
        => Math.Clamp((int)Math.Ceiling(percentile * count - 1e-9) - 1, 0, count - 1);

    private static double MeanOfLongest(double[] sortedAscending, int k)
    {
        var sum = 0d;
        for (var i = sortedAscending.Length - k; i < sortedAscending.Length; i++) sum += sortedAscending[i];
        return sum / k;
    }
}

/// <summary>
/// Fenêtre glissante d'horodatages de présentation (statistiques en direct). Conserve les images des
/// <c>window</c> dernières millisecondes par rapport à la plus récente. Non thread-safe : l'appelant synchronise.
/// </summary>
public sealed class RollingFrameWindow
{
    private readonly double _windowMs;
    private readonly Queue<double> _timestamps = new();
    private double? _last;

    public RollingFrameWindow(TimeSpan window)
    {
        if (window <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(window));
        _windowMs = window.TotalMilliseconds;
    }

    public int Count => _timestamps.Count;

    public void Add(IReadOnlyList<double> presentTimestampsMs)
    {
        ArgumentNullException.ThrowIfNull(presentTimestampsMs);
        foreach (var t in presentTimestampsMs)
        {
            if (!double.IsFinite(t)) continue;
            // Horloge revenue en arrière (nouvelle session de capture) : on repart d'une fenêtre vide.
            if (_last is { } last && t < last - FrameMetricsCalculator.MaxIntervalMs) _timestamps.Clear();
            _timestamps.Enqueue(t);
            _last = t;
        }
        if (_last is { } newest)
        {
            var cutoff = newest - _windowMs;
            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff) _timestamps.Dequeue();
        }
    }

    public FrameStats Compute() => FrameMetricsCalculator.Calculate(_timestamps.ToArray());

    public void Clear()
    {
        _timestamps.Clear();
        _last = null;
    }
}

/// <summary>
/// Agrégation complète d'une longue session à mémoire bornée : histogramme creux des intervalles (pas de 0,05 ms,
/// somme exacte par case). FPS moyens et temps moyen exacts ; 1 %/0,1 % low et P99 à la résolution de l'histogramme près.
/// Non thread-safe : l'appelant synchronise.
/// </summary>
public sealed class FrameStatsAccumulator
{
    /// <summary>Résolution de l'histogramme (ms).</summary>
    public const double BucketWidthMs = 0.05;

    private readonly Dictionary<int, Bucket> _buckets = new();
    private double? _last;
    private long _count;
    private double _total;

    public long IntervalCount => _count;

    public void Add(IReadOnlyList<double> presentTimestampsMs)
    {
        ArgumentNullException.ThrowIfNull(presentTimestampsMs);
        foreach (var t in presentTimestampsMs)
        {
            if (!double.IsFinite(t)) continue;
            if (_last is { } last)
            {
                var d = t - last;
                if (FrameMetricsCalculator.IsValidInterval(d)) AddInterval(d);
            }
            _last = t;
        }
    }

    public void AddInterval(double intervalMs)
    {
        if (!FrameMetricsCalculator.IsValidInterval(intervalMs)) return;
        var key = (int)(intervalMs / BucketWidthMs);
        _buckets[key] = _buckets.TryGetValue(key, out var b) ? new Bucket(b.Count + 1, b.Sum + intervalMs) : new Bucket(1, intervalMs);
        _count++;
        _total += intervalMs;
    }

    public FrameStats Compute()
    {
        if (_count == 0 || _total <= 0) return FrameStats.Empty;
        var keys = _buckets.Keys.ToArray();
        Array.Sort(keys);
        var count = (int)Math.Min(_count, int.MaxValue);

        return new FrameStats(
            AverageFps: _count * 1000d / _total,
            OnePercentLowFps: 1000d / MeanOfLongest(keys, FrameMetricsCalculator.LowCount(count, FrameMetricsCalculator.OnePercent)),
            PointOnePercentLowFps: 1000d / MeanOfLongest(keys, FrameMetricsCalculator.LowCount(count, FrameMetricsCalculator.PointOnePercent)),
            AverageFrameTimeMs: _total / _count,
            P99FrameTimeMs: ValueAtRank(keys, FrameMetricsCalculator.PercentileIndex(count, 0.99) + 1L),
            FrameCount: count == int.MaxValue ? int.MaxValue : count + 1,
            Duration: TimeSpan.FromMilliseconds(_total));
    }

    private double MeanOfLongest(int[] sortedKeys, int k)
    {
        var remaining = (long)k;
        var sum = 0d;
        for (var i = sortedKeys.Length - 1; i >= 0 && remaining > 0; i--)
        {
            var b = _buckets[sortedKeys[i]];
            if (b.Count <= remaining)
            {
                sum += b.Sum;
                remaining -= b.Count;
            }
            else
            {
                sum += b.Sum / b.Count * remaining;
                remaining = 0;
            }
        }
        return sum / k;
    }

    private double ValueAtRank(int[] sortedKeys, long rank)
    {
        long cumulative = 0;
        foreach (var key in sortedKeys)
        {
            var b = _buckets[key];
            cumulative += b.Count;
            if (cumulative >= rank) return b.Sum / b.Count;
        }
        var lastBucket = _buckets[sortedKeys[^1]];
        return lastBucket.Sum / lastBucket.Count;
    }

    private readonly record struct Bucket(long Count, double Sum);
}
