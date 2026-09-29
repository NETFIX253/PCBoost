using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>État affiché d'un élément matériel (toujours accompagné d'un libellé textuel).</summary>
public enum HealthLevel { Unknown = 0, Good, Warning, Critical }

/// <summary>Disque physique : état signalé par Windows, usure, température, heures de fonctionnement, erreurs.</summary>
public sealed class DiskHealthItemViewModel
{
    public DiskHealthItemViewModel(DiskHealthInfo disk, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);

        Name = string.IsNullOrWhiteSpace(disk.FriendlyName) ? localizer.Get("Health_Disk_Unnamed") : disk.FriendlyName;
        IsSystemDisk = disk.IsSystemDisk;
        Level = LevelOf(disk);
        StatusText = localizer.Get($"Health_Level_{Level}");

        var parts = new List<string> { localizer.Get($"Analysis_Media_{disk.MediaType}") };
        if (disk.BusType is not (StorageBusType.Unknown or StorageBusType.Other)) parts.Add(localizer.Get($"Analysis_Bus_{disk.BusType}"));
        if (disk.SizeBytes > 0) parts.Add(formatter.Bytes(disk.SizeBytes));
        DetailsText = string.Join(" · ", parts);

        var r = disk.Reliability;
        WearPercent = r?.WearPercent;
        HasWear = WearPercent.HasValue;
        // L'usure n'a de sens que pour un SSD ; un disque dur ne la communique pas.
        var wearValue = r?.WearPercent is { } wear
            ? localizer.Format("Health_Disk_WearValue", wear)
            : localizer.Get(r is null ? "Health_NotRead" : "Health_NotProvided");
        var rows = new List<InfoRowViewModel>();
        if (disk.MediaType != StorageMediaType.Hdd || r?.WearPercent is not null)
            rows.Add(new(localizer.Get("Health_Disk_Wear"), wearValue, HasWear ? localizer.Get("Health_Disk_WearDetail") : null));
        rows.Add(new(localizer.Get("Health_Disk_Temperature"),
            r?.TemperatureCelsius is { } t ? formatter.Temperature(t) : localizer.Get(r is null ? "Health_NotRead" : "Health_NotProvided"),
            r?.TemperatureMaxCelsius is { } max ? localizer.Format("Health_Disk_TemperatureMax", formatter.Temperature(max)) : null));
        rows.Add(new(localizer.Get("Health_Disk_PowerOn"),
            r?.PowerOnHours is { } hours ? localizer.Format("Health_Disk_PowerOnValue", formatter.Number(hours)) : localizer.Get(r is null ? "Health_NotRead" : "Health_NotProvided")));
        rows.Add(new(localizer.Get("Health_Disk_Errors"),
            r?.ReadErrorsUncorrected is { } errors ? formatter.Number(errors) : localizer.Get(r is null ? "Health_NotRead" : "Health_NotProvided"),
            r?.ReadErrorsUncorrected is > 0 ? localizer.Get("Health_Disk_ErrorsDetail") : null));
        Rows = rows;

        AdviceText = Level switch
        {
            HealthLevel.Critical => localizer.Get("Health_Disk_AdviceCritical"),
            HealthLevel.Warning => localizer.Get("Health_Disk_AdviceWarning"),
            _ => string.Empty,
        };
        SystemBadge = localizer.Get("Health_Disk_SystemBadge");
        AccessibleName = $"{Name}, {DetailsText} : {StatusText}";
    }

    public string Name { get; }

    public string DetailsText { get; }

    public bool IsSystemDisk { get; }

    public string SystemBadge { get; }

    public HealthLevel Level { get; }

    public string StatusText { get; }

    public int? WearPercent { get; }

    public bool HasWear { get; }

    /// <summary>Usure 0–100 pour la barre (0 si inconnue).</summary>
    public double WearBarValue => WearPercent ?? 0;

    public IReadOnlyList<InfoRowViewModel> Rows { get; }

    public string AdviceText { get; }

    public bool HasAdvice => !string.IsNullOrEmpty(AdviceText);

    public string AccessibleName { get; }

    internal static HealthLevel LevelOf(DiskHealthInfo disk)
    {
        if (disk.IsCritical) return HealthLevel.Critical;
        if (disk.IsWarning) return HealthLevel.Warning;
        return disk.Status == DiskHealthStatus.Healthy ? HealthLevel.Good : HealthLevel.Unknown;
    }
}

/// <summary>Batterie : capacité actuelle comparée à la capacité d'origine, cycles de charge.</summary>
public sealed class BatteryHealthItemViewModel
{
    public BatteryHealthItemViewModel(BatteryInfo battery, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(battery);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);

        Name = string.Join(" ", new[] { battery.Manufacturer, battery.Name }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));
        if (Name.Length == 0) Name = localizer.Get("Health_Battery_Unnamed");
        HealthPercent = battery.HealthPercent;
        Level = HealthPercent switch
        {
            null => HealthLevel.Unknown,
            < BatteryInfo.WornPercent => HealthLevel.Critical,
            < BatteryInfo.AgingPercent => HealthLevel.Warning,
            _ => HealthLevel.Good,
        };
        StatusText = localizer.Get($"Health_Battery_Level_{Level}");
        HealthText = HealthPercent is { } p ? formatter.Percent(p) : localizer.Get("Health_NotProvided");

        string Capacity(long? value) => value is not { } v
            ? localizer.Get("Health_NotProvided")
            : battery.CapacityIsRelative ? formatter.Number(v) : localizer.Format("Health_Battery_WattHours", formatter.Number(v / 1000d, v % 1000 == 0 ? 0 : 1));

        Rows =
        [
            new(localizer.Get("Health_Battery_Design"), Capacity(battery.DesignCapacityMWh)),
            new(localizer.Get("Health_Battery_FullCharge"), Capacity(battery.FullChargeCapacityMWh)),
            new(localizer.Get("Health_Battery_Cycles"), battery.CycleCount is { } c ? formatter.Number(c) : localizer.Get("Health_NotProvided")),
            new(localizer.Get("Health_Battery_Chemistry"), ChemistryText(battery.Chemistry, localizer)),
        ];
        AdviceText = Level switch
        {
            HealthLevel.Critical => localizer.Get("Health_Battery_AdviceWorn"),
            HealthLevel.Warning => localizer.Get("Health_Battery_AdviceAging"),
            _ => string.Empty,
        };
        AccessibleName = $"{Name} : {StatusText}, {HealthText}";
    }

    public string Name { get; }

    /// <summary>« LION » → « Lithium-ion » : codes de technologie fournis par le microprogramme de la batterie.</summary>
    internal static string ChemistryText(string? chemistry, ILocalizer localizer)
    {
        if (string.IsNullOrWhiteSpace(chemistry)) return localizer.Get("Health_NotProvided");
        var code = new string(chemistry.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var key = code switch
        {
            "LION" or "LI" or "LIION" or "LII" => "Health_Battery_Chemistry_LiIon",
            "LIP" or "LIPO" or "LIPOLY" => "Health_Battery_Chemistry_LiPoly",
            "NIMH" => "Health_Battery_Chemistry_NiMH",
            "NICD" => "Health_Battery_Chemistry_NiCd",
            "PBAC" => "Health_Battery_Chemistry_LeadAcid",
            _ => null,
        };
        return key is null ? chemistry.Trim() : localizer.Get(key);
    }

    public double? HealthPercent { get; }

    public double HealthBarValue => HealthPercent ?? 0;

    public HealthLevel Level { get; }

    public string StatusText { get; }

    /// <summary>« 78 % » de la capacité d'origine.</summary>
    public string HealthText { get; }

    public IReadOnlyList<InfoRowViewModel> Rows { get; }

    public string AdviceText { get; }

    public bool HasAdvice => !string.IsNullOrEmpty(AdviceText);

    public string AccessibleName { get; }
}

/// <summary>Périphérique signalé en erreur par Windows (information uniquement).</summary>
public sealed class DeviceProblemItemViewModel
{
    private static readonly HashSet<int> KnownCodes = [1, 3, 10, 12, 14, 18, 19, 21, 24, 28, 29, 31, 32, 37, 38, 39, 41, 43, 48, 52];

    public DeviceProblemItemViewModel(DeviceProblem device, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(localizer);
        Name = device.Name;
        ClassText = string.IsNullOrWhiteSpace(device.DeviceClass)
            ? string.Empty
            : localizer.GetOr($"Health_DeviceClass_{device.DeviceClass}", device.DeviceClass!);
        ProblemText = KnownCodes.Contains(device.ProblemCode)
            ? localizer.Format("Health_DeviceProblem_WithCode", localizer.Get($"Health_DeviceProblem_{device.ProblemCode}"), device.ProblemCode)
            : localizer.Format("Health_DeviceProblem_Other", device.ProblemCode);
        AccessibleName = string.IsNullOrEmpty(ClassText) ? $"{Name} : {ProblemText}" : $"{Name}, {ClassText} : {ProblemText}";
    }

    public string Name { get; }

    public string ClassText { get; }

    public bool HasClass => !string.IsNullOrEmpty(ClassText);

    public string ProblemText { get; }

    public string AccessibleName { get; }
}

/// <summary>
/// Santé du matériel : disques (état, usure, erreurs), batterie, limitation thermique du processeur, périphériques en
/// erreur. Lecture seule ; seule la lecture des compteurs détaillés des disques demande une autorisation administrateur.
/// </summary>
public sealed partial class HealthViewModel : ViewModelBase
{
    private readonly IHardwareHealthService _health;
    private readonly IThermalThrottlingDetector _thermal;

    public HealthViewModel(ViewModelContext context, IHardwareHealthService health, IThermalThrottlingDetector thermal)
        : base(context)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _thermal = thermal ?? throw new ArgumentNullException(nameof(thermal));
        ThermalAdvice =
        [
            new(T("Health_Thermal_Advice_Dust")),
            new(T("Health_Thermal_Advice_Airflow")),
            new(T("Health_Thermal_Advice_Surface")),
            new(T("Health_Thermal_Advice_Technician")),
        ];
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ReadCountersCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ReadCountersCommand))]
    public partial bool IsReadingCounters { get; private set; }

    [ObservableProperty]
    public partial bool HasReport { get; private set; }

    /// <summary>« Relevé du 28/09/2026 à 14:05 ».</summary>
    [ObservableProperty]
    public partial string UpdatedText { get; private set; } = string.Empty;

    // ---- Résumé ----

    [ObservableProperty]
    public partial HealthLevel OverallLevel { get; private set; }

    [ObservableProperty]
    public partial string OverallTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string OverallDetail { get; private set; } = string.Empty;

    // ---- Disques ----

    public ObservableCollection<DiskHealthItemViewModel> Disks { get; } = [];

    [ObservableProperty]
    public partial bool HasDisks { get; private set; }

    [ObservableProperty]
    public partial string DisksUnavailableText { get; private set; } = string.Empty;

    /// <summary>Date des compteurs détaillés, ou explication de l'autorisation nécessaire.</summary>
    [ObservableProperty]
    public partial string CountersText { get; private set; } = string.Empty;

    // ---- Batterie ----

    public ObservableCollection<BatteryHealthItemViewModel> Batteries { get; } = [];

    [ObservableProperty]
    public partial bool HasBatteries { get; private set; }

    [ObservableProperty]
    public partial string NoBatteryText { get; private set; } = string.Empty;

    // ---- Température et limitation ----

    [ObservableProperty]
    public partial HealthLevel ThermalLevel { get; private set; }

    [ObservableProperty]
    public partial string ThermalStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ThermalBadgeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ThermalDetailText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowThermalAdvice { get; private set; }

    public IReadOnlyList<TextItemViewModel> ThermalAdvice { get; }

    // ---- Périphériques ----

    public ObservableCollection<DeviceProblemItemViewModel> Devices { get; } = [];

    [ObservableProperty]
    public partial bool HasDeviceProblems { get; private set; }

    [ObservableProperty]
    public partial string DevicesStatusText { get; private set; } = string.Empty;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        _health.ReportUpdated += OnReportUpdated;
        _thermal.EpisodeDetected += OnEpisodeDetected;
        if (_health.Latest is { } latest && Context.Clock.UtcNow - latest.Timestamp < TimeSpan.FromMinutes(5)) Apply(latest);
        else await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _health.ReportUpdated -= OnReportUpdated;
        _thermal.EpisodeDetected -= OnEpisodeDetected;
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var report = await _health.RefreshAsync(ct).ConfigureAwait(true);
                Apply(report);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRefresh() => !IsLoading && !IsReadingCounters;

    /// <summary>Rapport de diagnostic (aperçu, puis enregistrement en PDF ou HTML).</summary>
    [RelayCommand]
    private void ExportReport() => Navigation.Navigate(PCBoost.Presentation.Navigation.PageKeys.Report);

    /// <summary>Lecture des compteurs de fiabilité des disques : une invite UAC, lecture seule.</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ReadCountersAsync(CancellationToken cancellationToken)
    {
        IsReadingCounters = true;
        StatusMessage = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var result = await _health.ReadDiskReliabilityAsync(ct).ConfigureAwait(true);
                if (!CheckResult(result)) return;
                StatusMessage = result.Message is { } message ? T(message) : T("Health_Counters_Read");
                if (_health.Latest is { } latest) Apply(latest);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsReadingCounters = false;
        }
    }

    private void OnReportUpdated(object? sender, HardwareHealthReport report) => OnUi(() =>
    {
        if (IsActive) Apply(report);
    });

    private void OnEpisodeDetected(object? sender, ThrottlingEpisode episode) => OnUi(() =>
    {
        if (IsActive && _health.Latest is { } latest)
            Apply(latest with { Thermal = latest.Thermal with { ObservedEpisodes = _thermal.EpisodeCount, LastEpisode = episode } });
    });

    internal void Apply(HardwareHealthReport report)
    {
        UpdatedText = T("Health_Updated", Formatter.DateTime(report.Timestamp));

        CollectionSync.Replace(Disks, report.Disks.Select(d => new DiskHealthItemViewModel(d, Localizer, Formatter)));
        HasDisks = Disks.Count > 0;
        DisksUnavailableText = HasDisks ? string.Empty : T(report.DiskAvailability == Availability.NotSupported ? "Health_Disks_NotSupported" : "Health_Disks_Unavailable");
        CountersText = report.ReliabilityMeasuredAt is { } measured
            ? T("Health_Counters_MeasuredAt", Formatter.DateTimeFull(measured))
            : T("Health_Counters_NotRead");

        CollectionSync.Replace(Batteries, report.Batteries.Select(b => new BatteryHealthItemViewModel(b, Localizer, Formatter)));
        HasBatteries = Batteries.Count > 0;
        NoBatteryText = HasBatteries ? string.Empty : T(report.BatteryAvailability == Availability.Available ? "Health_Battery_None" : "Health_Battery_Unavailable");

        ApplyThermal(report.Thermal);

        CollectionSync.Replace(Devices, report.DeviceProblems.Select(d => new DeviceProblemItemViewModel(d, Localizer)));
        HasDeviceProblems = Devices.Count > 0;
        DevicesStatusText = report.DeviceAvailability != Availability.Available
            ? T("Health_Devices_Unavailable")
            : HasDeviceProblems ? T("Health_Devices_Count", Devices.Count) : T("Health_Devices_None");

        ApplyOverall(report);
        HasReport = true;
    }

    private void ApplyThermal(ThermalLimitInfo thermal)
    {
        if (thermal.LastEpisode is { } episode)
        {
            ThermalLevel = HealthLevel.Warning;
            ThermalStatusText = T("Health_Thermal_Episode");
            var detail = T("Health_Thermal_EpisodeDetail", Formatter.DateTimeFull(episode.StartedAt), Formatter.Duration(episode.Duration),
                Formatter.Percent(episode.AverageProcessorPerformancePercent), Formatter.Percent(episode.AverageCpuPercent));
            if (episode.MaxCpuTemperatureC is { } temp) detail += " " + T("Health_Thermal_EpisodeTemperature", Formatter.Temperature(temp));
            ThermalDetailText = detail;
        }
        else if (thermal.FirmwareLimitEvents > 0)
        {
            ThermalLevel = HealthLevel.Warning;
            ThermalStatusText = T("Health_Thermal_Firmware");
            ThermalDetailText = T("Health_Thermal_FirmwareDetail", thermal.FirmwareLimitEvents, Formatter.DateTimeFull(thermal.LastFirmwareLimitEvent));
        }
        else
        {
            ThermalLevel = HealthLevel.Good;
            ThermalStatusText = T("Health_Thermal_None");
            ThermalDetailText = T("Health_Thermal_NoneDetail");
        }
        ShowThermalAdvice = ThermalLevel != HealthLevel.Good;
        ThermalBadgeText = T($"Health_Level_{ThermalLevel}");
    }

    private void ApplyOverall(HardwareHealthReport report)
    {
        var levels = Disks.Select(d => d.Level).Concat(Batteries.Select(b => b.Level)).Append(ThermalLevel)
            .Append(HasDeviceProblems ? HealthLevel.Warning : HealthLevel.Good).ToList();
        OverallLevel = levels.Contains(HealthLevel.Critical) ? HealthLevel.Critical
            : levels.Contains(HealthLevel.Warning) ? HealthLevel.Warning
            : HealthLevel.Good;

        var points = Disks.Count(d => d.Level >= HealthLevel.Warning) + Batteries.Count(b => b.Level >= HealthLevel.Warning)
            + (ThermalLevel >= HealthLevel.Warning ? 1 : 0) + (HasDeviceProblems ? 1 : 0);
        OverallTitle = T($"Health_Overall_{OverallLevel}");
        OverallDetail = OverallLevel switch
        {
            HealthLevel.Critical when Disks.Any(d => d.Level == HealthLevel.Critical) => T("Health_Overall_CriticalDisk"),
            HealthLevel.Good => report.ReliabilityMeasuredAt is null ? T("Health_Overall_GoodDetailNoCounters") : T("Health_Overall_GoodDetail"),
            _ => T("Health_Overall_Points", points),
        };
    }
}
