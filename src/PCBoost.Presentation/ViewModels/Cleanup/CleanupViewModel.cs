using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Cleanup;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum CleanupPhase { Idle = 0, Scanning, Ready, Cleaning, Done }

/// <summary>Catégorie de nettoyage : sûreté (texte + glyphe), taille récupérable, badge administrateur, disponibilité.</summary>
public sealed partial class CleanupCategoryItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly IValueFormatter _formatter;
    private readonly Action _selectionChanged;

    public CleanupCategoryItemViewModel(CleanupCategory category, ILocalizer localizer, IValueFormatter formatter, Action selectionChanged)
    {
        ArgumentNullException.ThrowIfNull(category);
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        Model = category;
        Id = category.Id;
        Name = localizer.Format(category.Name);
        Description = localizer.Format(category.Description);
        Safety = category.Safety;
        SafetyText = localizer.Get($"Cleanup_Page_Safety_{category.Safety}");
        SafetyDescription = localizer.Get($"Cleanup_Page_SafetyDescription_{category.Safety}");
        SafetyGlyph = category.Safety switch
        {
            SafetyCategory.Safe => Glyphs.Shield,
            SafetyCategory.Caution => Glyphs.Warning,
            _ => Glyphs.Error,
        };
        RequiresElevation = category.RequiresElevation;
        AdminBadgeText = localizer.Get("Cleanup_Page_AdminBadge");
        SizeText = localizer.Get("Cleanup_Page_NotScanned");
        IsSelected = category.SelectedByDefault;
        _selectionChanged = selectionChanged;
    }

    public CleanupCategory Model { get; }

    public string Id { get; }

    public string Name { get; }

    public string Description { get; }

    public SafetyCategory Safety { get; }

    /// <summary>« Sûr », « Prudence », « Avancé ».</summary>
    public string SafetyText { get; }

    public string SafetyDescription { get; }

    public string SafetyGlyph { get; }

    public bool IsSafe => Safety == SafetyCategory.Safe;

    public bool IsAdvanced => Safety == SafetyCategory.Advanced;

    [ObservableProperty]
    public partial bool RequiresElevation { get; private set; }

    /// <summary>« Administrateur ».</summary>
    public string AdminBadgeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    public partial bool IsScanned { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect), nameof(IsUnavailable))]
    public partial bool IsAvailable { get; private set; } = true;

    public bool IsUnavailable => !IsAvailable;

    [ObservableProperty]
    public partial string UnavailableReason { get; private set; } = string.Empty;

    public bool CanSelect => IsAvailable;

    [ObservableProperty]
    public partial long Bytes { get; private set; }

    /// <summary>« 1,2 Go » ou « Non analysé ».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string SizeText { get; private set; }

    /// <summary>« 1 245 fichiers ».</summary>
    [ObservableProperty]
    public partial string FileCountText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsSelected { get; set; }

    /// <summary>Taille non mesurable sans autorisation administrateur (affichée comme telle, jamais « 0 o »).</summary>
    [ObservableProperty]
    public partial bool IsSizeUnknown { get; private set; }

    public string AccessibleName => $"{Name}, {SafetyText}, {SizeText}";

    partial void OnIsSelectedChanged(bool value) => _selectionChanged?.Invoke();

    internal void ApplyScan(CleanupScanResult result)
    {
        IsScanned = true;
        Bytes = result.Available ? result.Bytes : 0;
        IsAvailable = result.Available;
        RequiresElevation = result.RequiresElevation || Model.RequiresElevation;
        UnavailableReason = result.Available ? string.Empty
            : result.UnavailableReason is { } reason ? _localizer.Format(reason) : _localizer.Get("Cleanup_Page_Unavailable");
        // Catégorie système analysée sans autorisation administrateur : le dossier n'est souvent pas lisible,
        // « 0 o » serait trompeur ; la taille réelle sera connue au nettoyage (une seule invite UAC).
        IsSizeUnknown = result.Available && result.RequiresElevation && result.FileCount == 0;
        SizeText = !result.Available ? _formatter.NotAvailable
            : IsSizeUnknown ? _localizer.Get("Cleanup_Page_SizeNeedsAdmin")
            : _formatter.Bytes(result.Bytes);
        FileCountText = result.Available && !IsSizeUnknown
            ? result.FileCount == 1 ? _localizer.Get("Cleanup_Page_OneFile") : _localizer.Format("Cleanup_Page_Files", result.FileCount)
            : string.Empty;
        if (!result.Available && IsSelected) IsSelected = false;
    }
}

/// <summary>
/// Nettoyage (§11) : catégories SAFE / CAUTION / ADVANCED, sélection SAFE par défaut, total sélectionné, analyse,
/// nettoyage après confirmation (irréversible), progression, résultat (espace récupéré, fichiers ignorés car utilisés).
/// </summary>
public sealed partial class CleanupViewModel : ViewModelBase
{
    private readonly ICleanupService _cleanup;

    public CleanupViewModel(ViewModelContext context, ICleanupService cleanup)
        : base(context)
    {
        _cleanup = cleanup;
        SelectedTotalText = Formatter.Bytes(0);
    }

    public ObservableCollection<CleanupCategoryItemViewModel> Categories { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsScanning), nameof(IsReady), nameof(IsCleaning), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(CleanCommand))]
    public partial CleanupPhase Phase { get; private set; }

    public bool IsIdle => Phase == CleanupPhase.Idle;

    public bool IsScanning => Phase == CleanupPhase.Scanning;

    public bool IsReady => Phase == CleanupPhase.Ready;

    public bool IsCleaning => Phase == CleanupPhase.Cleaning;

    public bool IsDone => Phase == CleanupPhase.Done;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    public partial int SelectedCount { get; private set; }

    /// <summary>Total récupérable de la sélection (« 1,4 Go »).</summary>
    [ObservableProperty]
    public partial string SelectedTotalText { get; private set; }

    /// <summary>« 3 catégories sélectionnées — 1,4 Go ».</summary>
    [ObservableProperty]
    public partial string SelectionSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool SelectionRequiresElevation { get; private set; }

    [ObservableProperty]
    public partial bool SelectionHasAdvanced { get; private set; }

    /// <summary>Total récupérable toutes catégories disponibles.</summary>
    [ObservableProperty]
    public partial string ScannedTotalText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double CleanProgressPercent { get; private set; }

    // --- Résultat ---
    /// <summary>« 1,3 Go récupérés ».</summary>
    [ObservableProperty]
    public partial string ResultTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultFilesText { get; private set; } = string.Empty;

    /// <summary>« 12 fichiers ignorés car utilisés par une application. » ; vide si aucun.</summary>
    [ObservableProperty]
    public partial string ResultSkippedText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSkippedFiles { get; private set; }

    /// <summary>Résultat par catégorie (Text = nom, Detail = espace libéré / erreur).</summary>
    public ObservableCollection<TextItemViewModel> ResultItems { get; } = [];

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        CollectionSync.Replace(Categories, _cleanup.GetCategories()
            .OrderBy(c => c.Safety)
            .Select(c => new CleanupCategoryItemViewModel(c, Localizer, Formatter, RefreshSelection)));
        RefreshSelection();
        await ScanAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated() => ScanCancelCommand.Execute(null);

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanScan))]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        Phase = CleanupPhase.Scanning;
        var ok = await RunSafeAsync(async ct =>
        {
            var results = await _cleanup.ScanAsync(null, ct).ConfigureAwait(true);
            var byId = results.ToDictionary(r => r.CategoryId, StringComparer.OrdinalIgnoreCase);
            foreach (var category in Categories)
                if (byId.TryGetValue(category.Id, out var result)) category.ApplyScan(result);
            ScannedTotalText = T("Cleanup_Page_ScannedTotal", Formatter.Bytes(Categories.Where(c => c.IsAvailable).Sum(c => c.Bytes)));
            RefreshSelection();
            Phase = CleanupPhase.Ready;
        }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        if (!ok) Phase = Categories.Any(c => c.IsScanned) ? CleanupPhase.Ready : CleanupPhase.Idle;
    }

    private bool CanScan() => Phase is not (CleanupPhase.Scanning or CleanupPhase.Cleaning);

    /// <summary>Nettoie la sélection après confirmation (suppression définitive).</summary>
    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var selected = Categories.Where(c => c.IsSelected && c.IsAvailable).ToList();
        if (selected.Count == 0) return;

        var details = selected.Select(c => TextRef.Of("Cleanup_Page_Confirm_Item", c.Name, c.SizeText, c.SafetyText)).ToList();
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Cleanup_Page_Confirm_Title"),
            TextRef.Of(SelectionHasAdvanced ? "Cleanup_Page_Confirm_MessageAdvanced" : "Cleanup_Page_Confirm_Message", SelectedTotalText),
            TextRef.Of("Cleanup_Page_Action_Clean"),
            CloseButton: TextRef.Of("Common_Action_Cancel"),
            IsDestructive: true,
            Details: details)).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        CleanProgressPercent = 0;
        Phase = CleanupPhase.Cleaning;
        var ok = await RunSafeAsync(async ct =>
        {
            var progress = UiProgress<double>(p => CleanProgressPercent = Math.Clamp(p <= 1 ? p * 100 : p, 0, 100));
            var summary = await _cleanup.CleanAsync(selected.Select(c => c.Id).ToList(), progress, ct).ConfigureAwait(true);
            ApplySummary(summary, selected);
            Phase = CleanupPhase.Done;
        }, linkToPage: false).ConfigureAwait(true);
        if (!ok) Phase = CleanupPhase.Ready;
    }

    private bool CanClean() => Phase is CleanupPhase.Ready && SelectedCount > 0;

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var c in Categories) c.IsSelected = c.IsAvailable && c.IsSafe;
        RefreshSelection();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var c in Categories) c.IsSelected = false;
        RefreshSelection();
    }

    /// <summary>Après le résultat : nouvelle analyse.</summary>
    [RelayCommand]
    private Task ScanAgainAsync() => ScanAsync(CancellationToken.None);

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);

    [RelayCommand]
    private void OpenStorage() => Navigation.Navigate(PageKeys.Storage);

    [RelayCommand]
    private void OpenPrograms() => Navigation.Navigate(PageKeys.Programs);

    [RelayCommand]
    private void OpenFiles() => Navigation.Navigate(PageKeys.Files);

    private void ApplySummary(CleanupSummary summary, IReadOnlyList<CleanupCategoryItemViewModel> selected)
    {
        ResultTitle = T("Cleanup_Page_Result_Freed", Formatter.Bytes(summary.TotalBytesFreed));
        ResultFilesText = summary.TotalFilesDeleted == 1 ? T("Cleanup_Page_Result_OneFile") : T("Cleanup_Page_Result_Files", summary.TotalFilesDeleted);
        HasSkippedFiles = summary.TotalFilesSkipped > 0;
        ResultSkippedText = summary.TotalFilesSkipped switch
        {
            0 => string.Empty,
            1 => T("Cleanup_Page_Result_OneSkipped"),
            var n => T("Cleanup_Page_Result_Skipped", n),
        };

        var names = selected.ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);
        CollectionSync.Replace(ResultItems, summary.Results.Select(r =>
        {
            var name = names.TryGetValue(r.CategoryId, out var n) ? n : r.CategoryId;
            if (!r.Outcome.Success && r.BytesFreed == 0)
                return new TextItemViewModel(name, Glyphs.Error, DescribeError(r.Outcome));
            var detail = T("Cleanup_Page_Result_Category", Formatter.Bytes(r.BytesFreed), r.FilesDeleted);
            if (r.FilesSkipped > 0) detail += " " + T("Cleanup_Page_Result_CategorySkipped", r.FilesSkipped);
            return new TextItemViewModel(name, r.Outcome.Success ? Glyphs.Success : Glyphs.Warning, detail);
        }));
    }

    private void RefreshSelection()
    {
        var selected = Categories.Where(c => c.IsSelected && c.IsAvailable).ToList();
        SelectedCount = selected.Count;
        var total = selected.Sum(c => c.Bytes);
        SelectedTotalText = Formatter.Bytes(total);
        SelectionRequiresElevation = selected.Any(c => c.RequiresElevation);
        SelectionHasAdvanced = selected.Any(c => c.Safety != SafetyCategory.Safe);
        SelectionSummary = selected.Count switch
        {
            0 => T("Cleanup_Page_Selection_None"),
            1 => T("Cleanup_Page_Selection_One", SelectedTotalText),
            var n => T("Cleanup_Page_Selection_Many", n, SelectedTotalText),
        };
    }
}
