using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Drivers;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Libellés des classes de périphériques (« Display » → « Carte graphique »), sinon le nom de classe Windows.</summary>
public static class DriverClassLabels
{
    /// <summary>Classes connues (nom Windows → suffixe de la clé « Drivers_Class_* »).</summary>
    public static IReadOnlyDictionary<string, string> Known { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Display"] = "Display",
        ["Net"] = "Net",
        ["MEDIA"] = "Media",
        ["AudioEndpoint"] = "Media",
        ["Bluetooth"] = "Bluetooth",
        ["System"] = "System",
        ["HDC"] = "Storage",
        ["SCSIAdapter"] = "Storage",
        ["DiskDrive"] = "Disk",
        ["USB"] = "Usb",
        ["HIDClass"] = "Input",
        ["Mouse"] = "Input",
        ["Keyboard"] = "Input",
        ["Camera"] = "Camera",
        ["Image"] = "Camera",
        ["Biometric"] = "Biometric",
        ["Extension"] = "Extension",
        ["SoftwareComponent"] = "Software",
        ["Printer"] = "Printer",
        ["Monitor"] = "Monitor",
        ["Firmware"] = "Firmware",
        ["SecurityDevices"] = "Security",
        ["Processor"] = "Processor",
        ["Battery"] = "Battery",
        ["Ports"] = "Ports",
        ["SmartCardReader"] = "SmartCard",
        ["Sensor"] = "Sensor",
    };

    public static string Describe(string? deviceClass, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        if (string.IsNullOrWhiteSpace(deviceClass)) return localizer.Get("Drivers_Class_Unknown");
        return Known.TryGetValue(deviceClass, out var suffix) ? localizer.Get("Drivers_Class_" + suffix) : deviceClass;
    }
}

/// <summary>
/// Mise à jour de pilote affichée : périphérique, pilote installé et proposé, source (Windows Update), raisons de son
/// classement. Les mises à jour exclues ne sont jamais sélectionnables.
/// </summary>
public sealed partial class DriverUpdateItemViewModel : ObservableObject
{
    private readonly Action _selectionChanged;
    private readonly Action? _openWindowsUpdate;

    public DriverUpdateItemViewModel(DriverUpdateCandidate candidate, ILocalizer localizer, IValueFormatter formatter, Action selectionChanged, Action? openWindowsUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Candidate = candidate;
        _selectionChanged = selectionChanged;
        _openWindowsUpdate = openWindowsUpdate;
        var offer = candidate.Offer;
        var device = candidate.Device;
        DeviceName = device?.DeviceName ?? offer.Model ?? offer.Title;
        ClassText = DriverClassLabels.Describe(offer.DriverClass ?? device?.DeviceClass, localizer);
        if (candidate.Devices.Count > 1) ClassText += " · " + localizer.Format("Drivers_DeviceCount", candidate.Devices.Count);

        CurrentText = device is null
            ? localizer.Get("Drivers_Current_NoDevice")
            : device.HasNoDriver && device.Version is null
                ? localizer.Get("Drivers_Current_None")
                : localizer.Format("Drivers_Current", Version(device.Version, localizer), Date(device.Date, localizer), Provider(device.Provider, localizer));
        ProposedText = localizer.Format("Drivers_Proposed", Version(offer.Version, localizer), Date(offer.DriverDate, localizer), Provider(offer.Provider ?? offer.Manufacturer, localizer));

        var details = new List<string> { localizer.Get(offer.IsOptional ? "Drivers_Source_Optional" : "Drivers_Source_Automatic") };
        if (offer.PublishedAt is { } published) details.Add(localizer.Format("Drivers_Published", published.ToLocalTime().ToString("d", localizer.Culture)));
        details.Add(offer.DownloadBytes is { } size ? formatter.Bytes(size) : localizer.Get("Drivers_SizeUnknown"));
        if (offer.RebootBehavior == DriverRebootBehavior.Always) details.Add(localizer.Get("Drivers_RestartRequired"));
        DetailsText = string.Join(" · ", details);

        Reasons = candidate.Reasons
            .Select(r => r == DriverUpdateReason.DeviceHasProblem
                ? localizer.Format("Drivers_Reason_DeviceHasProblem", candidate.Devices.First(d => d.ProblemCode != 0).ProblemCode)
                : localizer.Get("Drivers_Reason_" + r))
            .ToList();
        TierBadge = localizer.Get("Drivers_Tier_" + candidate.Tier);
        // Jamais pour un microprogramme : PCBoost n'oriente pas vers l'installation d'un BIOS ou d'un UEFI.
        CanOpenWindowsUpdate = (candidate.Has(DriverUpdateReason.RequiresUserInput) || candidate.Has(DriverUpdateReason.LicenseNotAccepted))
            && !candidate.Has(DriverUpdateReason.Firmware);
        _isSelected = candidate.SelectedByDefault;
        AccessibleName = $"{DeviceName}, {ClassText}, {CurrentText}, {ProposedText}";
    }

    public DriverUpdateCandidate Candidate { get; }

    public string DeviceName { get; }

    public string ClassText { get; }

    public string CurrentText { get; }

    public string ProposedText { get; }

    public string DetailsText { get; }

    public IReadOnlyList<string> Reasons { get; }

    public bool HasReasons => Reasons.Count > 0;

    public string TierBadge { get; }

    public DriverUpdateTier Tier => Candidate.Tier;

    public bool IsSelectable => Candidate.CanInstall;

    /// <summary>Installation demandant une interaction ou l'acceptation d'une licence : à faire depuis Windows Update.</summary>
    public bool CanOpenWindowsUpdate { get; }

    public string AccessibleName { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var effective = value && IsSelectable;
            if (SetProperty(ref _isSelected, effective)) _selectionChanged();
        }
    }

    private bool _isSelected;

    /// <summary>Ouvre les mises à jour facultatives de Windows Update (installation avec interaction ou licence).</summary>
    [RelayCommand]
    private void OpenWindowsUpdate() => _openWindowsUpdate?.Invoke();

    internal static string Version(string? version, ILocalizer localizer) => version ?? localizer.Get("Drivers_VersionUnknown");

    internal static string Date(DateOnly? date, ILocalizer localizer) => date is { } d ? d.ToString("d", localizer.Culture) : localizer.Get("Drivers_DateUnknown");

    private static string Provider(string? provider, ILocalizer localizer) => provider ?? localizer.Get("Drivers_ProviderUnknown");
}

public enum DriverOutcomeLevel { Good = 0, Warning = 1, Critical = 2 }

/// <summary>Résultat d'installation d'un pilote, avec le retour au pilote précédent s'il est possible.</summary>
public sealed partial class DriverResultItemViewModel : ObservableObject
{
    private readonly Func<DriverResultItemViewModel, Task> _rollback;

    public DriverResultItemViewModel(string deviceName, string versionsText, DriverOutcomeLevel level, string outcomeText, Guid? changeId,
        string rollbackLabel, Func<DriverResultItemViewModel, Task> rollback)
    {
        DeviceName = deviceName;
        VersionsText = versionsText;
        Level = level;
        OutcomeText = outcomeText;
        ChangeId = changeId;
        RollbackLabel = rollbackLabel;
        _rollback = rollback;
    }

    public string DeviceName { get; }

    public string VersionsText { get; }

    public string RollbackLabel { get; }

    public Guid? ChangeId { get; }

    [ObservableProperty]
    public partial DriverOutcomeLevel Level { get; internal set; }

    [ObservableProperty]
    public partial string OutcomeText { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRollback))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    public partial bool IsRollingBack { get; internal set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRollback))]
    [NotifyCanExecuteChangedFor(nameof(RollbackCommand))]
    public partial bool IsRolledBack { get; internal set; }

    public bool CanRollback => ChangeId is not null && !IsRollingBack && !IsRolledBack;

    public string AccessibleName => $"{DeviceName}, {OutcomeText}";

    [RelayCommand(CanExecute = nameof(CanRollback))]
    private Task RollbackAsync() => _rollback(this);
}

/// <summary>Source officielle complémentaire (fabricant du PC ou de la carte graphique) : ouverture du site officiel.</summary>
public sealed partial class OfficialSourceItemViewModel : ObservableObject
{
    private readonly Action<Uri> _open;

    public OfficialSourceItemViewModel(Core.Drivers.OfficialDriverSource source, ILocalizer localizer, Action<Uri> open)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(localizer);
        Source = source;
        _open = open;
        Title = localizer.Format("Drivers_Source_Title", source.Vendor, source.ToolName);
        KindText = localizer.Get(source.Kind == Core.Drivers.DriverSourceKind.PcManufacturer ? "Drivers_Source_Kind_Pc" : "Drivers_Source_Kind_Graphics");
        Description = source.DetectedFor is { } detected
            ? localizer.Format(source.Kind == Core.Drivers.DriverSourceKind.PcManufacturer ? "Drivers_Source_ForPc" : "Drivers_Source_ForGraphics", detected)
            : localizer.Get(source.Kind == Core.Drivers.DriverSourceKind.PcManufacturer ? "Drivers_Source_ForPcUnknown" : "Drivers_Source_ForGraphicsUnknown");
        HostText = source.Uri.Host;
        AccessibleName = $"{Title}, {Description}";
    }

    public Core.Drivers.OfficialDriverSource Source { get; }

    public string Title { get; }

    public string KindText { get; }

    public string Description { get; }

    /// <summary>Domaine officiel affiché avant l'ouverture (« support.hp.com »).</summary>
    public string HostText { get; }

    public string AccessibleName { get; }

    [RelayCommand]
    private void Open() => _open(Source.Uri);
}

