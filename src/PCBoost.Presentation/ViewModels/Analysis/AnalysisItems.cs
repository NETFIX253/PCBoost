using System.Collections.ObjectModel;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Catégories canoniques des cartes de constats (valeurs de <see cref="HealthFinding.Category"/>, insensibles à la casse).</summary>
public static class FindingCategories
{
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string Storage = "storage";
    public const string Startup = "startup";
    public const string Processes = "processes";
    public const string Temperature = "temperature";
    public const string Power = "power";
    public const string System = "system";
    public const string Security = "security";
    public const string Hardware = "hardware";
    public const string Other = "other";

    /// <summary>Ordre d'affichage des cartes (toujours affichées, même vides).</summary>
    public static IReadOnlyList<string> Canonical { get; } = [Cpu, Memory, Storage, Startup, Processes, Temperature, Power, System, Security, Hardware];

    /// <summary>Normalise une catégorie de constat (synonymes acceptés : ram, disk, thermal, windows…).</summary>
    public static string Normalize(string? category)
    {
        var c = (category ?? string.Empty).Trim().ToLowerInvariant();
        return c switch
        {
            "cpu" or "processor" or "load" => Cpu,
            "memory" or "ram" => Memory,
            "storage" or "disk" or "drive" or "cleanup" => Storage,
            "startup" or "boot" => Startup,
            "processes" or "process" or "background" => Processes,
            "temperature" or "thermal" or "temperatures" => Temperature,
            "power" or "battery" => Power,
            "system" or "windows" or "os" or "uptime" => System,
            "security" => Security,
            "hardware" or "health" => Hardware,
            _ => Other,
        };
    }

    public static string ResourceSuffix(string normalized) => normalized switch
    {
        Cpu => "Cpu",
        Memory => "Memory",
        Storage => "Storage",
        Startup => "Startup",
        Processes => "Processes",
        Temperature => "Temperature",
        Power => "Power",
        System => "System",
        Security => "Security",
        Hardware => "Hardware",
        _ => "Other",
    };
}

/// <summary>Carte de constats d'une catégorie ; vide → « Aucun problème de performance correspondant à cette catégorie n'a été identifié. »</summary>
public sealed class FindingCardViewModel
{
    public FindingCardViewModel(string category, IEnumerable<FindingItemViewModel> findings, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        Category = category;
        var suffix = FindingCategories.ResourceSuffix(category);
        Title = localizer.Get($"Analysis_Category_{suffix}");
        Findings = new ObservableCollection<FindingItemViewModel>(findings.OrderByDescending(f => f.Severity));
        EmptyText = localizer.Get("Analysis_Category_NoIssue");
        if (Findings.Count > 0)
        {
            WorstSeverity = Findings[0].Severity;
            IconGlyph = Labels.SeverityGlyph(WorstSeverity);
            StatusText = Findings.Count == 1
                ? localizer.Get("Analysis_Card_OneFinding")
                : localizer.Format("Analysis_Card_Findings", Findings.Count);
        }
        else
        {
            WorstSeverity = Severity.Info;
            IconGlyph = Glyphs.Success;
            StatusText = localizer.Get("Analysis_Card_NoFinding");
        }

        AccessibleName = $"{Title} : {StatusText}";
    }

    public string Category { get; }

    public string Title { get; }

    public ObservableCollection<FindingItemViewModel> Findings { get; }

    public bool HasFindings => Findings.Count > 0;

    public string EmptyText { get; }

    public Severity WorstSeverity { get; }

    public string IconGlyph { get; }

    /// <summary>« 2 constats » / « Aucun constat ».</summary>
    public string StatusText { get; }

    public string AccessibleName { get; }
}

/// <summary>Lecteur de stockage (type, espace, occupation).</summary>
public sealed class DriveItemViewModel
{
    public DriveItemViewModel(Core.Models.SystemInfo.StorageDrive drive, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Name = string.IsNullOrWhiteSpace(drive.Label) ? drive.RootPath : $"{drive.Label} ({drive.RootPath.TrimEnd('\\')})";
        TypeText = localizer.Get($"Analysis_Media_{drive.MediaType}");
        BusText = drive.BusType == Core.Models.SystemInfo.StorageBusType.Unknown ? string.Empty : localizer.Get($"Analysis_Bus_{drive.BusType}");
        Model = drive.Model ?? string.Empty;
        FreeText = localizer.Format("Analysis_Drive_Free", formatter.Bytes(drive.FreeBytes), formatter.Bytes(drive.TotalBytes));
        UsedPercent = drive.TotalBytes <= 0 ? 0 : Math.Clamp(drive.UsedBytes * 100d / drive.TotalBytes, 0, 100);
        UsedPercentText = formatter.Percent(UsedPercent);
        IsSystemDrive = drive.IsSystemDrive;
        IsRemovable = drive.IsRemovable;
        SystemBadge = drive.IsSystemDrive ? localizer.Get("Analysis_Drive_System") : string.Empty;
        AccessibleName = $"{Name}, {TypeText}, {FreeText}";
    }

    public string Name { get; }

    /// <summary>« SSD », « Disque dur (HDD) », « Type inconnu ».</summary>
    public string TypeText { get; }

    public string BusText { get; }

    public bool HasBus => !string.IsNullOrEmpty(BusText);

    public string Model { get; }

    /// <summary>« 54,2 Go libres sur 237,9 Go ».</summary>
    public string FreeText { get; }

    public double UsedPercent { get; }

    public string UsedPercentText { get; }

    public bool IsSystemDrive { get; }

    public bool IsRemovable { get; }

    public string SystemBadge { get; }

    public string AccessibleName { get; }
}

/// <summary>Processus parmi les plus consommateurs au moment de l'analyse.</summary>
public sealed record ProcessUsageItemViewModel(string Name, string Value, string? Path)
{
    public string AccessibleName => $"{Name} : {Value}";
}

/// <summary>Conseil matériel factuel (§24).</summary>
public sealed class HardwareAdviceItemViewModel
{
    public HardwareAdviceItemViewModel(HardwareAdvice advice, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentNullException.ThrowIfNull(localizer);
        Id = advice.Id;
        var component = string.IsNullOrWhiteSpace(advice.Component)
            ? "Other"
            : char.ToUpperInvariant(advice.Component.Trim()[0]) + advice.Component.Trim()[1..].ToLowerInvariant();
        Component = localizer.GetOr($"Analysis_Component_{component}", localizer.Get("Analysis_Component_Other"));
        Observation = localizer.Format(advice.Observation);
        Suggestion = localizer.Format(advice.Suggestion);
        ConfidenceText = localizer.Confidence(advice.Confidence);
    }

    public string Id { get; }

    public string Component { get; }

    public string Observation { get; }

    public string Suggestion { get; }

    public string ConfidenceText { get; }
}
