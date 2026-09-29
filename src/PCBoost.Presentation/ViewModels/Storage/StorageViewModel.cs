using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Catégorie d'espace (Documents, Téléchargements…) ; « Non mesuré » si la mesure n'a pas été possible.</summary>
public sealed partial class StorageCategoryItemViewModel
{
    private readonly Action<string>? _open;

    public StorageCategoryItemViewModel(StorageCategoryUsage usage, long driveTotal, ILocalizer localizer, IValueFormatter formatter, Action<string>? open)
    {
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = usage;
        _open = open;
        Name = localizer.Get($"Storage_Category_{usage.Kind}");
        IsMeasured = usage.Measured;
        SizeText = usage.Measured ? formatter.Bytes(usage.Bytes) : localizer.Get("Storage_NotMeasured");
        Percent = usage.Measured && driveTotal > 0 ? Math.Clamp(usage.Bytes * 100d / driveTotal, 0, 100) : 0;
        PercentText = usage.Measured && driveTotal > 0 ? formatter.Percent(Percent, 1) : string.Empty;
        Path = usage.Path ?? string.Empty;
    }

    public StorageCategoryUsage Model { get; }

    public string Name { get; }

    public bool IsMeasured { get; }

    public string SizeText { get; }

    /// <summary>Part du disque 0–100 (0 si non mesuré).</summary>
    public double Percent { get; }

    public string PercentText { get; }

    public string Path { get; }

    public bool CanOpen => !string.IsNullOrEmpty(Path) && _open is not null;

    public string AccessibleName => $"{Name} : {SizeText}";

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _open?.Invoke(Path);
}

/// <summary>Dossier volumineux avec action « Ouvrir ».</summary>
public sealed partial class LargeFolderItemViewModel
{
    private readonly Action<string>? _open;

    public LargeFolderItemViewModel(LargeFolder folder, long driveTotal, IValueFormatter formatter, Action<string>? open)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(formatter);
        _open = open;
        Path = folder.Path;
        Name = System.IO.Path.GetFileName(folder.Path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : folder.Path;
        SizeText = formatter.Bytes(folder.Bytes);
        Percent = driveTotal > 0 ? Math.Clamp(folder.Bytes * 100d / driveTotal, 0, 100) : 0;
    }

    public string Path { get; }

    public string Name { get; }

    public string SizeText { get; }

    public double Percent { get; }

    public string AccessibleName => $"{Name}, {SizeText}";

    [RelayCommand]
    private void Open() => _open?.Invoke(Path);
}

/// <summary>
/// Stockage (§25) : disque système (utilisé / libre / total), catégories (non mesuré si indisponible),
/// plus gros dossiers avec « Ouvrir ». Lecture seule : PCBoost ne supprime jamais vos fichiers personnels.
/// </summary>
public sealed partial class StorageViewModel : ViewModelBase
{
    private readonly IStorageAnalyzer _storage;
    private readonly IShellService _shell;

    public StorageViewModel(ViewModelContext context, IStorageAnalyzer storage, IShellService shell)
        : base(context)
    {
        _storage = storage;
        _shell = shell;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    public partial bool HasData { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    public partial bool IsAnalyzing { get; private set; }

    /// <summary>Analyse terminée sans résultat : « L'analyse du stockage n'est pas disponible. »</summary>
    public bool ShowUnavailable => !HasData && !IsAnalyzing && IsAttempted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnavailable))]
    public partial bool IsAttempted { get; private set; }

    /// <summary>« Disque système (C:) ».</summary>
    [ObservableProperty]
    public partial string DriveName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string UsedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string FreeText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TotalText { get; private set; } = string.Empty;

    /// <summary>Occupation 0–100.</summary>
    [ObservableProperty]
    public partial double UsedPercent { get; private set; }

    [ObservableProperty]
    public partial string UsedPercentText { get; private set; } = string.Empty;

    public ObservableCollection<StorageCategoryItemViewModel> Categories { get; } = [];

    public ObservableCollection<LargeFolderItemViewModel> LargestFolders { get; } = [];

    [ObservableProperty]
    public partial string AnalysisDateText { get; private set; } = string.Empty;

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => AnalyzeAsync(cancellationToken);

    protected override void OnDeactivated() => AnalyzeCancelCommand.Execute(null);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        IsAnalyzing = true;
        ErrorText = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var breakdown = await _storage.AnalyzeSystemDriveAsync(ct).ConfigureAwait(true);
                IsAttempted = true;
                if (breakdown is null)
                {
                    HasData = false;
                    return;
                }

                Apply(breakdown);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand]
    private void OpenCleanup() => Navigation.Navigate(PageKeys.Cleanup);

    private void Apply(StorageBreakdown b)
    {
        var d = b.Drive;
        DriveName = T("Storage_SystemDrive", d.RootPath.TrimEnd('\\'));
        UsedText = Formatter.Bytes(d.UsedBytes);
        FreeText = Formatter.Bytes(d.FreeBytes);
        TotalText = Formatter.Bytes(d.TotalBytes);
        UsedPercent = d.TotalBytes <= 0 ? 0 : Math.Clamp(d.UsedBytes * 100d / d.TotalBytes, 0, 100);
        UsedPercentText = Formatter.Percent(UsedPercent);
        CollectionSync.Replace(Categories, b.Categories
            .OrderByDescending(c => c.Measured)
            .ThenByDescending(c => c.Bytes)
            .Select(c => new StorageCategoryItemViewModel(c, d.TotalBytes, Localizer, Formatter, OpenFolder)));
        CollectionSync.Replace(LargestFolders, b.LargestFolders
            .OrderByDescending(f => f.Bytes)
            .Select(f => new LargeFolderItemViewModel(f, d.TotalBytes, Formatter, OpenFolder)));
        AnalysisDateText = T("Storage_Date", Formatter.DateTime(b.Timestamp));
        HasData = true;
    }

    private void OpenFolder(string path) => CheckResult(_shell.OpenFolder(path));
}
