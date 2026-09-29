using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Élément ayant ralenti le démarrage d'après Windows (application, pilote, service).</summary>
public sealed record BootDegradationItemViewModel(string Name, string KindText, string DelayText, string DateText)
{
    public string AccessibleName => $"{Name}, {KindText} : {DelayText}, {DateText}";
}

/// <summary>
/// Durée du démarrage (page Programmes au démarrage) : dernier démarrage mesuré par Windows, comparaison avant / après
/// les changements de programmes au démarrage, éléments signalés comme ayant ralenti le démarrage. Seules des mesures
/// réelles de Windows sont affichées ; sans mesure, la section l'indique au lieu d'estimer.
/// </summary>
public sealed partial class StartupViewModel
{
    internal const int MaxDegradations = 5;
    internal static readonly TimeSpan DegradationWindow = TimeSpan.FromDays(30);

    private readonly IBootTimeService? _bootTime;
    private IReadOnlyList<BootDegradation> _degradations = [];

    public bool HasBootSection => _bootTime is not null;

    [ObservableProperty]
    public partial bool IsBootLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReadBootMeasurementsCommand))]
    public partial bool IsReadingBoot { get; private set; }

    [ObservableProperty]
    public partial bool HasBootMeasurement { get; private set; }

    /// <summary>« 42 s » : durée totale du dernier démarrage complet mesuré par Windows.</summary>
    [ObservableProperty]
    public partial string BootValueText { get; private set; } = string.Empty;

    /// <summary>« Dernier démarrage complet mesuré par Windows, le 28/09/2026 08:02 ».</summary>
    [ObservableProperty]
    public partial string BootCaptionText { get; private set; } = string.Empty;

    /// <summary>« Jusqu'au bureau : 30 s · Finalisation : 12 s · 14 programmes au démarrage ».</summary>
    [ObservableProperty]
    public partial string BootBreakdownText { get; private set; } = string.Empty;

    /// <summary>Explication lorsque aucune mesure n'est disponible (ou dernier démarrage connu sans mesure).</summary>
    [ObservableProperty]
    public partial string BootInfoText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowFastStartupNote { get; private set; }

    [ObservableProperty]
    public partial bool HasBootComparison { get; private set; }

    [ObservableProperty]
    public partial string BootComparisonText { get; private set; } = string.Empty;

    /// <summary>« 17 s de moins » / « 3 s de plus » / « Aucun changement notable ».</summary>
    [ObservableProperty]
    public partial string BootDifferenceText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool BootImproved { get; private set; }

    public ObservableCollection<BootDegradationItemViewModel> BootDegradations { get; } = [];

    [ObservableProperty]
    public partial bool HasBootDegradations { get; private set; }

    private async Task LoadBootAsync(CancellationToken cancellationToken)
    {
        if (_bootTime is null) return;
        var report = await _bootTime.GetReportAsync(cancellationToken).ConfigureAwait(true);
        ApplyBoot(report);
    }

    /// <summary>Lecture des mesures de démarrage de Windows : une invite UAC, lecture seule.</summary>
    [RelayCommand(CanExecute = nameof(CanReadBoot))]
    private async Task ReadBootMeasurementsAsync(CancellationToken cancellationToken)
    {
        if (_bootTime is null) return;
        IsReadingBoot = true;
        StatusMessage = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var result = await _bootTime.ReadMeasurementsAsync(ct).ConfigureAwait(true);
                if (!CheckResult(result)) return;
                StatusMessage = result.Message is { } message ? T(message) : T("Startup_Boot_Read");
                ApplyBoot(await _bootTime.GetReportAsync(ct).ConfigureAwait(true));
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsReadingBoot = false;
        }
    }

    private bool CanReadBoot() => !IsReadingBoot;

    internal void ApplyBoot(BootTimeReport report)
    {
        var latest = report.LatestBoot;
        HasBootMeasurement = latest is not null;
        if (latest is not null)
        {
            BootValueText = BootDuration(Formatter, Localizer, latest.BootTime);
            BootCaptionText = T("Startup_Boot_LatestCaption", Formatter.DateTimeFull(latest.Timestamp));
            var parts = new List<string>
            {
                T("Startup_Boot_MainPath", BootDuration(Formatter, Localizer, latest.MainPathBootTime)),
                T("Startup_Boot_PostBoot", BootDuration(Formatter, Localizer, latest.PostBootTime)),
            };
            if (latest.StartupAppCount is { } apps) parts.Add(T("Startup_Boot_Apps", apps));
            BootBreakdownText = string.Join(" · ", parts);
            BootInfoText = string.Empty;
        }
        else
        {
            BootValueText = string.Empty;
            BootCaptionText = string.Empty;
            BootBreakdownText = string.Empty;
            BootInfoText = report.Sessions.Count > 0
                ? T("Startup_Boot_NoMeasurementLast", Formatter.DateTimeFull(report.Sessions[0].StartedAt), T($"Startup_Boot_Kind_{report.Sessions[0].Kind}"))
                : T("Startup_Boot_NoMeasurement");
        }
        ShowFastStartupNote = report.MostlyFastStartup;

        if (report.Comparison is { } c)
        {
            HasBootComparison = true;
            BootComparisonText = T("Startup_Boot_Comparison", Formatter.DateTimeFull(c.ChangedAt),
                BootDuration(Formatter, Localizer, c.AverageBefore), c.BootsBefore,
                BootDuration(Formatter, Localizer, c.AverageAfter), c.BootsAfter);
            var diff = c.Difference;
            BootImproved = diff < -NoticeableDifference;
            BootDifferenceText = diff.Duration() <= NoticeableDifference
                ? T("Startup_Boot_NoChange")
                : T(diff < TimeSpan.Zero ? "Startup_Boot_Faster" : "Startup_Boot_Slower", BootDuration(Formatter, Localizer, diff.Duration()));
        }
        else
        {
            HasBootComparison = report.LastStartupChangeAt is not null;
            BootComparisonText = report.LastStartupChangeAt is { } changed
                ? T("Startup_Boot_ComparisonPending", Formatter.DateTimeFull(changed))
                : string.Empty;
            BootDifferenceText = string.Empty;
            BootImproved = false;
        }

        var since = Context.Clock.UtcNow - DegradationWindow;
        _degradations = report.Measurements?.Degradations.Where(d => d.Timestamp >= since).ToList() ?? [];
        var top = _degradations
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.Timestamp).First())
            .OrderByDescending(d => d.DegradationTime)
            .Take(MaxDegradations)
            .Select(d => new BootDegradationItemViewModel(
                d.Name,
                T($"Startup_Boot_DegradationKind_{d.Kind}"),
                T("Startup_Boot_DegradationDelay", BootDuration(Formatter, Localizer, d.DegradationTime)),
                Formatter.DateTimeFull(d.Timestamp)));
        Common.CollectionSync.Replace(BootDegradations, top);
        HasBootDegradations = BootDegradations.Count > 0;
        IsBootLoaded = true;
        MarkSlowedItems();
    }

    /// <summary>Badge « A ralenti le démarrage » sur les programmes dont l'exécutable figure dans les mesures de Windows.</summary>
    private void MarkSlowedItems()
    {
        var byFile = _degradations
            .Where(d => d.Kind == BootDegradationKind.Application && !string.IsNullOrWhiteSpace(d.FileName))
            .GroupBy(d => FileNameOf(d.FileName!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(d => d.DegradationTime), StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
        {
            var file = string.IsNullOrWhiteSpace(item.Model.ExecutablePath) ? null : FileNameOf(item.Model.ExecutablePath);
            item.SetBootDelay(file is not null && byFile.TryGetValue(file, out var delay)
                ? T("Startup_Badge_SlowedBoot", BootDuration(Formatter, Localizer, delay))
                : null);
        }
    }

    /// <summary>Nom de fichier d'un chemin Windows (séparateurs « \ » et « / »), indépendamment de la plateforme.</summary>
    internal static string FileNameOf(string path)
    {
        var trimmed = path.Trim().Trim('"');
        var index = trimmed.LastIndexOfAny(['\\', '/']);
        return index >= 0 ? trimmed[(index + 1)..] : trimmed;
    }

    private static readonly TimeSpan NoticeableDifference = TimeSpan.FromSeconds(1);

    /// <summary>« 42 s », « 1 min 35 s », « 0,8 s » : durées de démarrage lisibles, sans arrondi trompeur à la minute.</summary>
    internal static string BootDuration(IValueFormatter formatter, Core.Localization.ILocalizer localizer, TimeSpan duration)
    {
        var seconds = Math.Max(0, duration.TotalSeconds);
        if (seconds < 10) return localizer.Format("Startup_Boot_Seconds", formatter.Number(Math.Round(seconds, 1), 1));
        if (seconds < 60) return localizer.Format("Startup_Boot_Seconds", formatter.Number(Math.Round(seconds), 0));
        var total = (int)Math.Round(seconds);
        return localizer.Format("Startup_Boot_MinutesSeconds", total / 60, (total % 60).ToString("00", localizer.Culture));
    }
}
