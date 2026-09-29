using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum BenchmarkPhaseState { Idle = 0, MeasuringBefore, BeforeDone, MeasuringAfter, Compared }

/// <summary>Mesure enregistrée (historique ou mesure en cours).</summary>
public sealed partial class BenchmarkRunItemViewModel
{
    private readonly Action<BenchmarkRunItemViewModel>? _compare;

    public BenchmarkRunItemViewModel(BenchmarkRun run, ILocalizer localizer, IValueFormatter formatter, Action<BenchmarkRunItemViewModel>? compare)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = run;
        _compare = compare;
        DateText = formatter.DateTimeFull(run.Timestamp);
        PhaseText = localizer.Get($"Benchmark_Phase_{run.Phase}");
        DurationText = formatter.Duration(run.Duration);
        Title = !string.IsNullOrWhiteSpace(run.Label) ? run.Label! : run.GameName ?? localizer.Get("Benchmark_NoLabel");
        GameText = run.GameName ?? localizer.Get("Benchmark_NoGame");
        CpuText = localizer.Format("Benchmark_Run_Cpu", formatter.Percent(run.CpuAveragePercent), formatter.Percent(run.CpuMaxPercent));
        GpuText = formatter.Percent(run.GpuAveragePercent);
        RamText = formatter.Percent(run.RamAveragePercent);
        DiskText = formatter.Percent(run.DiskActiveAveragePercent);
        FpsText = run.Frames.HasData ? formatter.Fps(run.Frames.AverageFps) : formatter.NotAvailable;
        OnePercentLowText = run.Frames.HasData ? formatter.Fps(run.Frames.OnePercentLowFps) : formatter.NotAvailable;
        SampleCountText = localizer.Format("Benchmark_Run_Samples", run.SampleCount);
        CompareLabel = localizer.Get("Benchmark_Action_ShowComparison");
    }

    public BenchmarkRun Model { get; }

    public string DateText { get; }

    /// <summary>« Avant », « Après », « Mesure seule ».</summary>
    public string PhaseText { get; }

    public string DurationText { get; }

    public string Title { get; }

    public string GameText { get; }

    /// <summary>« Moyenne 34 % — max 78 % ».</summary>
    public string CpuText { get; }

    public string GpuText { get; }

    public string RamText { get; }

    public string DiskText { get; }

    public string FpsText { get; }

    public string OnePercentLowText { get; }

    public string SampleCountText { get; }

    public bool CanCompare => Model.Phase == BenchmarkPhase.After && Model.PairedRunId.HasValue && _compare is not null;

    public string CompareLabel { get; }

    public string AccessibleName => $"{PhaseText}, {Title}, {DateText}";

    [RelayCommand(CanExecute = nameof(CanCompare))]
    private void Compare() => _compare?.Invoke(this);
}

/// <summary>Écart mesuré pour une métrique ; « Non comparable » si une mesure manque.</summary>
public sealed class BenchmarkDeltaItemViewModel
{
    public BenchmarkDeltaItemViewModel(BenchmarkDelta delta, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(delta);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Metric = delta.Metric;
        MetricName = localizer.GetOr($"Benchmark_Metric_{delta.Metric}", delta.Metric);
        var kind = KindOf(delta.Metric);
        BeforeText = Format(kind, delta.Before, formatter);
        AfterText = Format(kind, delta.After, formatter);
        IsComparable = delta.Difference.HasValue && delta.Before.HasValue && delta.After.HasValue;
        if (!IsComparable)
        {
            DifferenceText = localizer.Get("Benchmark_NotComparable");
            VerdictText = localizer.Get("Benchmark_Verdict_NotComparable");
            IconGlyph = Glyphs.Unknown;
            return;
        }

        var diff = delta.Difference!.Value;
        DifferenceText = FormatDifference(kind, diff, localizer, formatter);
        if (Math.Abs(diff) < 0.05)
        {
            VerdictText = localizer.Get("Benchmark_Verdict_Same");
            IconGlyph = Glyphs.Equal;
        }
        else
        {
            IsImprovement = delta.HigherIsBetter ? diff > 0 : diff < 0;
            IsRegression = !IsImprovement;
            VerdictText = localizer.Get(IsImprovement ? "Benchmark_Verdict_Better" : "Benchmark_Verdict_Worse");
            IconGlyph = diff > 0 ? Glyphs.ArrowUp : Glyphs.ArrowDown;
        }
    }

    public string Metric { get; }

    public string MetricName { get; }

    public string BeforeText { get; }

    public string AfterText { get; }

    /// <summary>« +6 FPS », « -3,2 pts », « Non comparable ».</summary>
    public string DifferenceText { get; }

    /// <summary>« Mieux », « Moins bien », « Identique », « Non comparable ».</summary>
    public string VerdictText { get; }

    public string IconGlyph { get; }

    public bool IsComparable { get; }

    public bool IsImprovement { get; }

    public bool IsRegression { get; }

    public string AccessibleName => $"{MetricName} : {BeforeText} → {AfterText}, {DifferenceText}, {VerdictText}";

    private enum MetricValueKind { Fps, Milliseconds, Percent, Number }

    private static MetricValueKind KindOf(string metric)
    {
        if (metric.Contains("Fps", StringComparison.OrdinalIgnoreCase)) return MetricValueKind.Fps;
        if (metric.Contains("FrameTime", StringComparison.OrdinalIgnoreCase) || metric.EndsWith("Ms", StringComparison.Ordinal)) return MetricValueKind.Milliseconds;
        if (metric.Contains("Percent", StringComparison.OrdinalIgnoreCase) || metric.Contains("Cpu", StringComparison.OrdinalIgnoreCase)
            || metric.Contains("Gpu", StringComparison.OrdinalIgnoreCase) || metric.Contains("Ram", StringComparison.OrdinalIgnoreCase)
            || metric.Contains("Disk", StringComparison.OrdinalIgnoreCase)) return MetricValueKind.Percent;
        return MetricValueKind.Number;
    }

    private static string Format(MetricValueKind kind, double? value, IValueFormatter f) => kind switch
    {
        MetricValueKind.Fps => f.Fps(value),
        MetricValueKind.Milliseconds => f.Milliseconds(value),
        MetricValueKind.Percent => f.Percent(value, 1),
        _ => f.Number(value, 1),
    };

    private static string FormatDifference(MetricValueKind kind, double diff, ILocalizer l, IValueFormatter f)
    {
        var sign = diff > 0 ? "+" : diff < 0 ? "\u2212" : string.Empty;
        var abs = Math.Abs(diff);
        var text = kind switch
        {
            MetricValueKind.Fps => f.Fps(abs),
            MetricValueKind.Milliseconds => f.Milliseconds(abs),
            MetricValueKind.Percent => l.Format("Benchmark_Delta_Points", f.Number(abs, 1)),
            _ => f.Number(abs, 1),
        };
        return sign + text;
    }
}

/// <summary>
/// Mesure AVANT / APRÈS (§68) : durée 30 / 60 / 120 s, jeu suivi facultatif, progression, historique,
/// comparaison des écarts mesurés uniquement (« Non comparable » sinon).
/// </summary>
public sealed partial class BenchmarkViewModel : ViewModelBase
{
    private static readonly int[] DurationsSeconds = [30, 60, 120];
    private readonly IBenchmarkService _benchmark;
    private readonly IGamingService _gaming;
    private readonly IGameDetectionService _detection;
    private DetectedGameProcess? _game;
    private CancellationTokenSource? _runCts;

    public BenchmarkViewModel(ViewModelContext context, IBenchmarkService benchmark, IGamingService gaming, IGameDetectionService detection)
        : base(context)
    {
        _benchmark = benchmark;
        _gaming = gaming;
        _detection = detection;
        DurationOptions = DurationsSeconds
            .Select(s => new OptionItem(s.ToString(CultureInfo.InvariantCulture), Formatter.Duration(TimeSpan.FromSeconds(s))))
            .ToList();
        SelectedDurationIndex = 1;
    }

    public IReadOnlyList<OptionItem> DurationOptions { get; }

    /// <summary>0 = 30 s, 1 = 60 s, 2 = 120 s.</summary>
    [ObservableProperty]
    public partial int SelectedDurationIndex { get; set; }

    /// <summary>Libellé facultatif de la mesure (ex. « Scène du marché, 1080p »).</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = string.Empty;

    /// <summary>Suivre le jeu détecté (mesure des images si disponible).</summary>
    [ObservableProperty]
    public partial bool TrackGame { get; set; } = true;

    [ObservableProperty]
    public partial bool HasDetectedGame { get; private set; }

    /// <summary>« Jeu suivi : {nom} » / « Aucun jeu détecté : seules les mesures système seront enregistrées. »</summary>
    [ObservableProperty]
    public partial string DetectedGameText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsIdle), nameof(IsBeforeDone), nameof(IsCompared))]
    [NotifyCanExecuteChangedFor(nameof(MeasureBeforeCommand), nameof(MeasureAfterCommand), nameof(CancelMeasureCommand))]
    public partial BenchmarkPhaseState Phase { get; private set; }

    public bool IsIdle => Phase == BenchmarkPhaseState.Idle;

    public bool IsRunning => Phase is BenchmarkPhaseState.MeasuringBefore or BenchmarkPhaseState.MeasuringAfter;

    /// <summary>Mesure AVANT terminée : l'utilisateur applique ses changements puis lance la mesure APRÈS.</summary>
    public bool IsBeforeDone => Phase == BenchmarkPhaseState.BeforeDone;

    public bool IsCompared => Phase == BenchmarkPhaseState.Compared;

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    /// <summary>« Mesure AVANT en cours… ».</summary>
    [ObservableProperty]
    public partial string ProgressText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBeforeRun))]
    public partial BenchmarkRunItemViewModel? BeforeRun { get; private set; }

    public bool HasBeforeRun => BeforeRun is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAfterRun))]
    public partial BenchmarkRunItemViewModel? AfterRun { get; private set; }

    public bool HasAfterRun => AfterRun is not null;

    /// <summary>Écarts mesurés (comparaison AVANT / APRÈS).</summary>
    public ObservableCollection<BenchmarkDeltaItemViewModel> Comparison { get; } = [];

    [ObservableProperty]
    public partial bool HasComparison { get; private set; }

    /// <summary>Avertissement si les deux mesures ne sont pas comparables (durées ou jeux différents).</summary>
    [ObservableProperty]
    public partial string ComparisonNotice { get; private set; } = string.Empty;

    public ObservableCollection<BenchmarkRunItemViewModel> History { get; } = [];

    [ObservableProperty]
    public partial bool HasHistory { get; private set; }

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        await DetectGameAsync(cancellationToken).ConfigureAwait(true);
        await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _runCts?.Cancel();
    }

    [RelayCommand]
    private Task DetectGameAsync(CancellationToken cancellationToken) => RunSafeAsync(async ct =>
    {
        _game = _gaming.CurrentGame ?? await _detection.DetectRunningGameAsync(ct).ConfigureAwait(true);
        HasDetectedGame = _game is not null;
        DetectedGameText = _game is { } g ? T("Benchmark_TrackedGame", g.Game.Name) : T("Benchmark_NoGameDetected");
    }, cancellationToken, trackBusy: false);

    [RelayCommand(CanExecute = nameof(CanMeasureBefore))]
    private async Task MeasureBeforeAsync()
    {
        AfterRun = null;
        Comparison.Clear();
        HasComparison = false;
        ComparisonNotice = string.Empty;
        var run = await RunAsync(BenchmarkPhase.Before, null, BenchmarkPhaseState.MeasuringBefore).ConfigureAwait(true);
        if (run is null)
        {
            Phase = BeforeRun is null ? BenchmarkPhaseState.Idle : BenchmarkPhaseState.BeforeDone;
            return;
        }

        BeforeRun = new BenchmarkRunItemViewModel(run, Localizer, Formatter, null);
        Phase = BenchmarkPhaseState.BeforeDone;
        StatusMessage = T("Benchmark_BeforeDone");
    }

    private bool CanMeasureBefore() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanMeasureAfter))]
    private async Task MeasureAfterAsync()
    {
        if (BeforeRun is not { } before) return;
        var run = await RunAsync(BenchmarkPhase.After, before.Model.Id, BenchmarkPhaseState.MeasuringAfter).ConfigureAwait(true);
        if (run is null)
        {
            Phase = BenchmarkPhaseState.BeforeDone;
            return;
        }

        AfterRun = new BenchmarkRunItemViewModel(run, Localizer, Formatter, null);
        ShowComparison(before.Model, run);
        Phase = BenchmarkPhaseState.Compared;
    }

    private bool CanMeasureAfter() => !IsRunning && BeforeRun is not null;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void CancelMeasure() => _runCts?.Cancel();

    /// <summary>Nouvelle comparaison.</summary>
    [RelayCommand]
    private void Reset()
    {
        BeforeRun = null;
        AfterRun = null;
        Comparison.Clear();
        HasComparison = false;
        ComparisonNotice = string.Empty;
        Phase = BenchmarkPhaseState.Idle;
    }

    [RelayCommand]
    private void OpenGaming() => Navigation.Navigate(PageKeys.Gaming);

    private async Task<BenchmarkRun?> RunAsync(BenchmarkPhase phase, Guid? pairedRunId, BenchmarkPhaseState runningState)
    {
        ErrorText = null;
        StatusMessage = null;
        ProgressPercent = 0;
        ProgressText = T(phase == BenchmarkPhase.Before ? "Benchmark_Progress_Before" : "Benchmark_Progress_After");
        Phase = runningState;
        var seconds = DurationsSeconds[Math.Clamp(SelectedDurationIndex, 0, DurationsSeconds.Length - 1)];
        var label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim();
        var pid = TrackGame ? _game?.ProcessId : null;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        _runCts = cts;
        BenchmarkRun? result = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var progress = UiProgress<double>(p => ProgressPercent = Math.Clamp(p <= 1 ? p * 100 : p, 0, 100));
                result = await _benchmark.RunAsync(phase, TimeSpan.FromSeconds(seconds), label, pid, pairedRunId, progress, ct).ConfigureAwait(true);
                ProgressPercent = 100;
                await LoadHistoryAsync(ct).ConfigureAwait(true);
            }, cts.Token, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            _runCts = null;
        }

        return result;
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        await RunSafeAsync(async ct =>
        {
            var runs = await _benchmark.GetHistoryAsync(50, ct).ConfigureAwait(true);
            CollectionSync.Replace(History, runs.OrderByDescending(r => r.Timestamp).Select(r => new BenchmarkRunItemViewModel(r, Localizer, Formatter, CompareFromHistory)));
            HasHistory = History.Count > 0;
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);
    }

    private void CompareFromHistory(BenchmarkRunItemViewModel after)
    {
        var before = History.FirstOrDefault(h => h.Model.Id == after.Model.PairedRunId);
        if (before is null)
        {
            ErrorText = T("Benchmark_PairMissing");
            return;
        }

        BeforeRun = new BenchmarkRunItemViewModel(before.Model, Localizer, Formatter, null);
        AfterRun = new BenchmarkRunItemViewModel(after.Model, Localizer, Formatter, null);
        ShowComparison(before.Model, after.Model);
        Phase = BenchmarkPhaseState.Compared;
    }

    private void ShowComparison(BenchmarkRun before, BenchmarkRun after)
    {
        var comparison = _benchmark.Compare(before, after);
        CollectionSync.Replace(Comparison, comparison.Deltas.Select(d => new BenchmarkDeltaItemViewModel(d, Localizer, Formatter)));
        HasComparison = Comparison.Count > 0;
        var notices = new List<string>();
        if (before.Duration != after.Duration) notices.Add(T("Benchmark_Notice_Duration"));
        if (!string.Equals(before.GameName, after.GameName, StringComparison.OrdinalIgnoreCase)) notices.Add(T("Benchmark_Notice_Game"));
        ComparisonNotice = string.Join(" ", notices);
    }
}
