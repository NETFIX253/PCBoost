using CommunityToolkit.Mvvm.ComponentModel;
using PCBoost.Core.Models.Monitoring;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Point de mesure mémorisé pour les graphiques (valeurs absentes = null).</summary>
public readonly record struct MetricPoint(
    DateTimeOffset Timestamp,
    double? Cpu,
    double? Memory,
    double? Disk,
    double? Gpu,
    double? NetworkReceive,
    double? NetworkSend,
    double? CpuTemperature,
    double? GpuTemperature)
{
    public double? Get(MetricKind metric) => metric switch
    {
        MetricKind.Cpu => Cpu,
        MetricKind.Memory => Memory,
        MetricKind.Disk => Disk,
        MetricKind.Gpu => Gpu,
        MetricKind.NetworkReceive => NetworkReceive,
        MetricKind.NetworkSend => NetworkSend,
        MetricKind.CpuTemperature => CpuTemperature,
        MetricKind.GpuTemperature => GpuTemperature,
        _ => null,
    };
}

/// <summary>Tampon circulaire de capacité fixe (aucune allocation par ajout).</summary>
public sealed class MetricRingBuffer
{
    private readonly MetricPoint[] _items;
    private int _start;

    public MetricRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new MetricPoint[capacity];
    }

    public int Count { get; private set; }

    public int Capacity => _items.Length;

    public MetricPoint this[int index] => _items[(_start + index) % _items.Length];

    public void Add(MetricPoint point)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = point;
            Count++;
        }
        else
        {
            _items[_start] = point;
            _start = (_start + 1) % _items.Length;
        }
    }

    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    /// <summary>Index du premier point dont l'horodatage est ≥ <paramref name="from"/> (Count si aucun).</summary>
    public int FirstIndexAtOrAfter(DateTimeOffset from)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (this[mid].Timestamp < from) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}

/// <summary>Résultat du calcul d'une série : points normalisés 0–100 (NaN = pas de mesure) et statistiques.</summary>
public readonly record struct SeriesComputation(double[] Points, double? Current, double? Average, double? Maximum);

/// <summary>Calcul des séries (réduction à un nombre maximal de points par moyenne de seaux).</summary>
public static class SeriesMath
{
    public const int DefaultMaxPoints = 120;

    public static SeriesComputation Compute(IReadOnlyList<MetricPoint> points, MetricKind metric, Func<double, double> normalize, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        return Compute(points.Count, i => points[i].Get(metric), normalize, maxPoints);
    }

    public static SeriesComputation Compute(MetricRingBuffer buffer, int startIndex, MetricKind metric, Func<double, double> normalize, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var count = Math.Max(0, buffer.Count - startIndex);
        return Compute(count, i => buffer[startIndex + i].Get(metric), normalize, maxPoints);
    }

    public static SeriesComputation Compute(int count, Func<int, double?> valueAt, Func<double, double> normalize, int maxPoints = DefaultMaxPoints)
    {
        ArgumentNullException.ThrowIfNull(valueAt);
        ArgumentNullException.ThrowIfNull(normalize);
        if (count <= 0) return new SeriesComputation([], null, null, null);

        double sum = 0;
        var n = 0;
        double? max = null;
        double? current = null;
        for (var i = 0; i < count; i++)
        {
            if (valueAt(i) is not { } v || !double.IsFinite(v)) continue;
            sum += v;
            n++;
            if (max is null || v > max) max = v;
            current = v;
        }

        var bucketCount = Math.Min(count, Math.Max(1, maxPoints));
        var result = new double[bucketCount];
        for (var b = 0; b < bucketCount; b++)
        {
            var from = (int)((long)b * count / bucketCount);
            var to = (int)((long)(b + 1) * count / bucketCount);
            double bucketSum = 0;
            var bucketN = 0;
            for (var i = from; i < Math.Max(to, from + 1) && i < count; i++)
            {
                if (valueAt(i) is not { } v || !double.IsFinite(v)) continue;
                bucketSum += v;
                bucketN++;
            }

            result[b] = bucketN == 0 ? double.NaN : Math.Clamp(normalize(bucketSum / bucketN), 0, 100);
        }

        return new SeriesComputation(result, current, n == 0 ? null : sum / n, max);
    }
}

/// <summary>
/// Série d'une métrique prête pour un contrôle graphique léger : <see cref="Points"/> contient des valeurs
/// normalisées 0–100 (<see cref="double.NaN"/> = pas de mesure, à ne pas tracer), régulièrement espacées
/// sur la période ; un nouveau tableau est publié à chaque mise à jour.
/// </summary>
public sealed partial class MetricSeriesViewModel : ObservableObject
{
    private readonly Func<double?, string> _format;

    public MetricSeriesViewModel(MetricKind metric, string title, Func<double?, string> format, string notAvailable)
    {
        Metric = metric;
        Title = title;
        _format = format ?? throw new ArgumentNullException(nameof(format));
        CurrentText = notAvailable;
        AverageText = notAvailable;
        MaximumText = notAvailable;
    }

    public MetricKind Metric { get; }

    public string Title { get; }

    [ObservableProperty]
    public partial IReadOnlyList<double> Points { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string CurrentText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string AverageText { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string MaximumText { get; private set; }

    /// <summary>Valeur actuelle brute (null si non mesurée).</summary>
    [ObservableProperty]
    public partial double? Current { get; private set; }

    /// <summary>Au moins une mesure sur la période.</summary>
    [ObservableProperty]
    public partial bool IsAvailable { get; private set; }

    /// <summary>Raison de l'absence de données (« Non disponible sur ce PC », « Non enregistré dans l'historique »).</summary>
    [ObservableProperty]
    public partial string UnavailableReason { get; private set; } = string.Empty;

    /// <summary>Échelle verticale (« 0 – 100 % », « Max : 1,2 Mo/s »).</summary>
    [ObservableProperty]
    public partial string ScaleText { get; private set; } = string.Empty;

    public string AccessibleName => $"{Title} : {CurrentText}, {AverageText}, {MaximumText}";

    internal void Apply(SeriesComputation computation, string scaleText, string unavailableReason)
    {
        Points = computation.Points;
        Current = computation.Current;
        CurrentText = _format(computation.Current);
        AverageText = _format(computation.Average);
        MaximumText = _format(computation.Maximum);
        IsAvailable = computation.Average.HasValue;
        ScaleText = scaleText;
        UnavailableReason = IsAvailable ? string.Empty : unavailableReason;
    }
}
