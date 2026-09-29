using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Performances en temps réel (§22, §67) : séries CPU, RAM, disque, GPU, réseau, températures.
/// Périodes 30 s / 5 min / 30 min (historique du moniteur, mise à jour à chaque échantillon via un tampon circulaire)
/// et 24 h / 7 j (historique persistant). Valeur actuelle, moyenne et maximum par métrique.
/// </summary>
public sealed partial class PerformanceViewModel : ViewModelBase
{
    private const int LiveCapacity = 1800;
    private static readonly TimeSpan[] PeriodWindows =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(24),
        TimeSpan.FromDays(7),
    ];

    private readonly IPerformanceMonitor _monitor;
    private readonly IPerformanceHistoryService _history;
    private readonly ISettingsService _settings;
    private readonly IAppLifecycle? _lifecycle;
    private readonly MetricRingBuffer _live = new(LiveCapacity);
    private MonitorLease? _lease;
    private IReadOnlyList<MetricPoint> _persisted = [];

    public PerformanceViewModel(ViewModelContext context, IPerformanceMonitor monitor, IPerformanceHistoryService history, ISettingsService settings, IAppLifecycle? lifecycle = null)
        : base(context)
    {
        _lifecycle = lifecycle;
        _monitor = monitor;
        _history = history;
        _settings = settings;
        var na = Formatter.NotAvailable;
        CpuSeries = new MetricSeriesViewModel(MetricKind.Cpu, T("Performance_Metric_Cpu"), v => Formatter.Percent(v), na);
        MemorySeries = new MetricSeriesViewModel(MetricKind.Memory, T("Performance_Metric_Memory"), v => Formatter.Percent(v), na);
        DiskSeries = new MetricSeriesViewModel(MetricKind.Disk, T("Performance_Metric_Disk"), v => Formatter.Percent(v), na);
        GpuSeries = new MetricSeriesViewModel(MetricKind.Gpu, T("Performance_Metric_Gpu"), v => Formatter.Percent(v), na);
        NetworkReceiveSeries = new MetricSeriesViewModel(MetricKind.NetworkReceive, T("Performance_Metric_NetworkReceive"), Formatter.Rate, na);
        NetworkSendSeries = new MetricSeriesViewModel(MetricKind.NetworkSend, T("Performance_Metric_NetworkSend"), Formatter.Rate, na);
        CpuTemperatureSeries = new MetricSeriesViewModel(MetricKind.CpuTemperature, T("Performance_Metric_CpuTemperature"), Formatter.Temperature, na);
        GpuTemperatureSeries = new MetricSeriesViewModel(MetricKind.GpuTemperature, T("Performance_Metric_GpuTemperature"), Formatter.Temperature, na);
        Series = [CpuSeries, MemorySeries, DiskSeries, GpuSeries, NetworkReceiveSeries, NetworkSendSeries, CpuTemperatureSeries, GpuTemperatureSeries];
        Periods =
        [
            new OptionItem("30s", T("Performance_Period_30s")),
            new OptionItem("5m", T("Performance_Period_5m")),
            new OptionItem("30m", T("Performance_Period_30m")),
            new OptionItem("24h", T("Performance_Period_24h")),
            new OptionItem("7d", T("Performance_Period_7d")),
        ];
        SelectedPeriodIndex = 1;
    }

    public MetricSeriesViewModel CpuSeries { get; }

    public MetricSeriesViewModel MemorySeries { get; }

    public MetricSeriesViewModel DiskSeries { get; }

    public MetricSeriesViewModel GpuSeries { get; }

    public MetricSeriesViewModel NetworkReceiveSeries { get; }

    public MetricSeriesViewModel NetworkSendSeries { get; }

    public MetricSeriesViewModel CpuTemperatureSeries { get; }

    public MetricSeriesViewModel GpuTemperatureSeries { get; }

    /// <summary>Toutes les séries dans l'ordre d'affichage.</summary>
    public ObservableCollection<MetricSeriesViewModel> Series { get; }

    /// <summary>30 s, 5 min, 30 min (direct), 24 h, 7 j (historique).</summary>
    public IReadOnlyList<OptionItem> Periods { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLivePeriod))]
    public partial int SelectedPeriodIndex { get; set; }

    /// <summary>La période sélectionnée est mise à jour en direct (30 s / 5 min / 30 min).</summary>
    public bool IsLivePeriod => SelectedPeriodIndex <= 2;

    /// <summary>« Il y a 5 min » (début de l'axe horizontal).</summary>
    [ObservableProperty]
    public partial string AxisStartText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsMonitoringPaused { get; private set; }

    /// <summary>Période historique sans données enregistrées.</summary>
    [ObservableProperty]
    public partial bool HasNoHistory { get; private set; }

    /// <summary>« Intervalle de mesure : 1 s ».</summary>
    [ObservableProperty]
    public partial string IntervalText { get; private set; } = string.Empty;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _lease = MonitorLease.Acquire(_monitor, _settings.Current.MonitoringEnabled, _lifecycle);
        _live.Clear();
        foreach (var sample in _monitor.GetHistory(PeriodWindows[2])) _live.Add(ToPoint(sample, null));
        _monitor.SampleAvailable += OnSample;
        _monitor.ModeChanged += OnModeChanged;
        UpdateMonitorState();
        await LoadPeriodAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _monitor.SampleAvailable -= OnSample;
        _monitor.ModeChanged -= OnModeChanged;
        _lease?.Dispose();
        _lease = null;
    }

    partial void OnSelectedPeriodIndexChanged(int value)
    {
        if (IsActive) _ = LoadPeriodAsync(PageToken);
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadPeriodAsync(cancellationToken);

    private async Task LoadPeriodAsync(CancellationToken cancellationToken)
    {
        var index = Math.Clamp(SelectedPeriodIndex, 0, PeriodWindows.Length - 1);
        AxisStartText = T($"Performance_AxisStart_{Periods[index].Key}");
        if (IsLivePeriod)
        {
            HasNoHistory = false;
            RebuildLive();
            return;
        }

        await RunSafeAsync(async ct =>
        {
            var snapshots = await _history.GetHistoryAsync(PeriodWindows[index], ct).ConfigureAwait(true);
            _persisted = snapshots.OrderBy(s => s.Timestamp).Select(s => new MetricPoint(
                s.Timestamp, s.CpuPercent, s.MemoryPercent, s.DiskActivePercent, s.GpuPercent, null, null, s.CpuTemperatureC, s.GpuTemperatureC)).ToList();
            HasNoHistory = _persisted.Count == 0;
            RebuildPersisted();
        }, cancellationToken).ConfigureAwait(true);
    }

    private void OnSample(object? sender, SystemMetricsSample sample)
    {
        var temps = _monitor.Temperatures;
        OnUi(() =>
        {
            if (!IsActive) return;
            _live.Add(ToPoint(sample, temps));
            if (IsLivePeriod) RebuildLive();
        });
    }

    private void OnModeChanged(object? sender, EventArgs e) => OnUi(UpdateMonitorState);

    private void UpdateMonitorState()
    {
        IsMonitoringPaused = _monitor.Mode == MonitoringMode.Paused;
        IntervalText = T("Performance_Interval", Formatter.Duration(_monitor.CurrentInterval));
    }

    private void RebuildLive()
    {
        var window = PeriodWindows[Math.Clamp(SelectedPeriodIndex, 0, 2)];
        var now = _live.Count > 0 ? _live[_live.Count - 1].Timestamp : Context.Clock.UtcNow;
        var start = _live.FirstIndexAtOrAfter(now - window);
        foreach (var series in Series)
        {
            var normalize = NormalizerFor(series.Metric, Enumerable.Range(start, _live.Count - start).Select(i => _live[i].Get(series.Metric)), out var scale);
            series.Apply(SeriesMath.Compute(_live, start, series.Metric, normalize), scale, T("Performance_Unavailable_Live"));
        }
    }

    private void RebuildPersisted()
    {
        foreach (var series in Series)
        {
            var normalize = NormalizerFor(series.Metric, _persisted.Select(p => p.Get(series.Metric)), out var scale);
            var reason = series.Metric is MetricKind.NetworkReceive or MetricKind.NetworkSend
                ? T("Performance_Unavailable_NotRecorded")
                : T("Performance_Unavailable_History");
            series.Apply(SeriesMath.Compute(_persisted, series.Metric, normalize), scale, reason);
        }
    }

    private Func<double, double> NormalizerFor(MetricKind metric, IEnumerable<double?> values, out string scale)
    {
        switch (metric)
        {
            case MetricKind.NetworkReceive or MetricKind.NetworkSend:
                var max = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).DefaultIfEmpty(0).Max();
                var top = Math.Max(max, 1024d);
                scale = T("Performance_Scale_Max", Formatter.Rate(top));
                return v => v * 100d / top;
            case MetricKind.CpuTemperature or MetricKind.GpuTemperature:
                scale = T("Performance_Scale_Temperature");
                return v => v;
            default:
                scale = T("Performance_Scale_Percent");
                return v => v;
        }
    }

    private static MetricPoint ToPoint(SystemMetricsSample s, TemperatureReadings? temps) => new(
        s.Timestamp,
        s.CpuPercent,
        s.MemoryUsedPercent,
        s.DiskActivePercent,
        s.GpuPercent,
        s.NetworkReceiveBytesPerSec,
        s.NetworkSendBytesPerSec,
        temps is { Cpu.HasValue: true } ? temps.Cpu.Value : null,
        temps is { Gpu.HasValue: true } ? temps.Gpu.Value : null);
}
