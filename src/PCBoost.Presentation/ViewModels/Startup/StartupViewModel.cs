using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Startup;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

public enum StartupFilter { All = 0, Enabled = 1, Recommended = 2 }

/// <summary>
/// Programme lancé au démarrage. La bascule <see cref="IsEnabled"/> (TwoWay) déclenche la modification ;
/// en cas d'échec (UAC refusé…), elle revient à l'état réel.
/// </summary>
public sealed partial class StartupItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly IValueFormatter _formatter;
    private readonly Func<StartupItemViewModel, bool, Task>? _toggle;
    private bool _suppressToggle;

    public StartupItemViewModel(StartupEntry entry, ILocalizer localizer, IValueFormatter formatter, Func<StartupItemViewModel, bool, Task>? toggle)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        Update(entry);
        _toggle = toggle;
    }

    public StartupEntry Model { get; private set; } = null!;

    public string Id => Model.Id;

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>Éditeur ou « Éditeur inconnu ».</summary>
    [ObservableProperty]
    public partial string PublisherText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; private set; } = string.Empty;

    /// <summary>« Impact élevé », « Non mesuré »…</summary>
    [ObservableProperty]
    public partial string ImpactText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsImpactMeasured { get; private set; }

    /// <summary>Preuve mesurée (« Mesuré : 180 Mo de mémoire, 12 s de processeur ») ou « Non mesuré : programme non lancé actuellement ».</summary>
    [ObservableProperty]
    public partial string EvidenceText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(AccessibleName))]
    public partial bool IsEnabled { get; set; }

    /// <summary>« Activé » / « Désactivé ».</summary>
    public string StateText => _localizer.Get(IsEnabled ? "Startup_State_Enabled" : "Startup_State_Disabled");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggle))]
    public partial bool IsToggling { get; private set; }

    /// <summary>La bascule est utilisable (pas pendant une modification ; jamais pour désactiver un logiciel de sécurité).</summary>
    public bool CanToggle => !IsToggling && !IsLocked;

    /// <summary>Logiciel de sécurité actif : jamais désactivé depuis PCBoost (le motif est donné à l'utilisateur).</summary>
    public bool IsLocked => Model.IsSecuritySoftware && IsEnabled;

    public string ToggleHelpText => IsLocked ? SecurityNotice : string.Empty;

    [ObservableProperty]
    public partial string RecommendationText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecommendationReason { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecommendationGlyph { get; private set; } = string.Empty;

    public bool IsRecommendedToDisable => Model.Recommendation == StartupRecommendation.CanDisable && Model.IsEnabled;

    /// <summary>Emplacement lisible (« Registre (utilisateur) », « Dossier Démarrage »…).</summary>
    [ObservableProperty]
    public partial string LocationText { get; private set; } = string.Empty;

    /// <summary>Chemin technique (clé de registre, dossier, tâche) — détail / mode Expert.</summary>
    [ObservableProperty]
    public partial string SourcePath { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CommandText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string SignatureText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool RequiresElevation { get; private set; }

    public string AdminBadgeText => _localizer.Get("Startup_AdminBadge");

    [ObservableProperty]
    public partial bool IsSecuritySoftware { get; private set; }

    /// <summary>« Logiciel de sécurité : PCBoost ne le désactive pas. »</summary>
    public string SecurityNotice => _localizer.Get("Startup_SecurityNotice");

    [ObservableProperty]
    public partial bool IsMissingExecutable { get; private set; }

    public string MissingExecutableText => _localizer.Get("Startup_MissingExecutable");

    public string AccessibleName => $"{Name}, {StateText}, {ImpactText}";

    internal void Update(StartupEntry entry)
    {
        Model = entry ?? throw new ArgumentNullException(nameof(entry));
        Name = entry.Name;
        PublisherText = string.IsNullOrWhiteSpace(entry.Publisher) ? _localizer.Get("Startup_UnknownPublisher") : entry.Publisher!;
        Description = entry.Description ?? string.Empty;
        IsImpactMeasured = entry.Impact != StartupImpact.NotMeasured;
        ImpactText = _localizer.Get($"Startup_Impact_{entry.Impact}");
        EvidenceText = BuildEvidence(entry);
        RecommendationText = _localizer.Get($"Startup_Recommendation_{entry.Recommendation}");
        RecommendationReason = entry.RecommendationReason is { } r ? _localizer.Format(r) : string.Empty;
        RecommendationGlyph = entry.Recommendation switch
        {
            StartupRecommendation.CanDisable => Glyphs.Info,
            StartupRecommendation.Review => Glyphs.Warning,
            StartupRecommendation.Keep => Glyphs.Shield,
            _ => Glyphs.Info,
        };
        LocationText = _localizer.Get($"Startup_Location_{entry.Location}");
        SourcePath = entry.SourcePath;
        CommandText = entry.Command ?? entry.ExecutablePath ?? string.Empty;
        SignatureText = _localizer.Signature(entry.Signature);
        RequiresElevation = entry.RequiresElevation;
        IsSecuritySoftware = entry.IsSecuritySoftware;
        IsMissingExecutable = !entry.ExecutableExists && !string.IsNullOrEmpty(entry.ExecutablePath);
        SetEnabledSilently(entry.IsEnabled);
        OnPropertyChanged(nameof(IsRecommendedToDisable));
        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(ToggleHelpText));
    }

    internal void SetEnabledSilently(bool value)
    {
        _suppressToggle = true;
        try
        {
            IsEnabled = value;
        }
        finally
        {
            _suppressToggle = false;
        }

        OnPropertyChanged(nameof(CanToggle));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(ToggleHelpText));
    }

    internal void SetToggling(bool value) => IsToggling = value;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppressToggle || _toggle is null) return;
        _ = _toggle(this, value);
    }

    private string BuildEvidence(StartupEntry entry)
    {
        if (entry.Evidence is not { } e) return _localizer.Get("Startup_Evidence_NotMeasured");
        if (!e.ProcessRunning) return _localizer.Get("Startup_Evidence_NotRunning");
        var parts = new List<string>();
        if (e.WorkingSetBytes is { } ws) parts.Add(_localizer.Format("Startup_Evidence_Memory", _formatter.Bytes(ws)));
        if (e.CpuTime is { } cpu) parts.Add(_localizer.Format("Startup_Evidence_Cpu", _formatter.Duration(cpu)));
        if (e.IoReadBytes is { } io) parts.Add(_localizer.Format("Startup_Evidence_Disk", _formatter.Bytes(io)));
        return parts.Count == 0
            ? _localizer.Get("Startup_Evidence_NotMeasured")
            : _localizer.Format("Startup_Evidence_Measured", string.Join(", ", parts));
    }
}

/// <summary>
/// Programmes au démarrage (§12) : liste, filtres (tous / activés / recommandés), recherche, bascule
/// via <see cref="IStartupService.SetEnabledAsync"/> avec retour visuel à l'état réel en cas d'échec.
/// </summary>
public sealed partial class StartupViewModel : ViewModelBase
{
    private readonly IStartupService _startup;
    private readonly List<StartupItemViewModel> _all = [];

    public StartupViewModel(ViewModelContext context, IStartupService startup)
        : base(context)
    {
        _startup = startup;
        Filters =
        [
            new OptionItem(nameof(StartupFilter.All), T("Startup_Filter_All")),
            new OptionItem(nameof(StartupFilter.Enabled), T("Startup_Filter_Enabled")),
            new OptionItem(nameof(StartupFilter.Recommended), T("Startup_Filter_Recommended")),
        ];
    }

    /// <summary>Éléments filtrés affichés.</summary>
    public ObservableCollection<StartupItemViewModel> Items { get; } = [];

    /// <summary>Options du filtre (liaison SelectedIndex ↔ <see cref="SelectedFilterIndex"/>).</summary>
    public IReadOnlyList<OptionItem> Filters { get; }

    [ObservableProperty]
    public partial int SelectedFilterIndex { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    /// <summary>« Aucun programme ne correspond à ce filtre. » / « Aucun programme ne se lance au démarrage. »</summary>
    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>« 9 programmes se lancent au démarrage sur 14. »</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int RecommendedCount { get; private set; }

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    partial void OnSelectedFilterIndexChanged(int value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RunSafeAsync(async ct =>
        {
            var entries = await _startup.GetEntriesAsync(ct).ConfigureAwait(true);
            var existing = _all.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            _all.Clear();
            foreach (var entry in entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (existing.TryGetValue(entry.Id, out var item)) item.Update(entry);
                else item = new StartupItemViewModel(entry, Localizer, Formatter, ToggleAsync);
                _all.Add(item);
            }

            IsLoaded = true;
            UpdateSummary();
            ApplyFilter();
        }, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);

    internal async Task ToggleAsync(StartupItemViewModel item, bool enabled)
    {
        if (item.IsToggling) return;
        var previous = item.Model.IsEnabled;
        if (item.Model.IsSecuritySoftware && !enabled)
        {
            item.SetEnabledSilently(previous);
            ErrorText = T("Startup_SecurityNotice");
            return;
        }

        ErrorText = null;
        item.SetToggling(true);
        try
        {
            var ok = await RunSafeAsync(async ct =>
            {
                var result = await _startup.SetEnabledAsync(item.Model, enabled, ct).ConfigureAwait(true);
                if (!result.Success)
                {
                    // UAC refusé, accès refusé… : retour visuel à l'état réel.
                    item.SetEnabledSilently(previous);
                    ErrorText = result.Error == OperationErrorKind.ElevationCancelled
                        ? T("Startup_Error_ElevationCancelled", item.Name)
                        : T("Startup_Error_Toggle", item.Name, DescribeError(result));
                    return;
                }

                item.Update(item.Model with { IsEnabled = enabled });
                StatusMessage = enabled ? T("Startup_Enabled", item.Name) : T("Startup_Disabled", item.Name);
                UpdateSummary();
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
            if (!ok) item.SetEnabledSilently(previous);
        }
        finally
        {
            item.SetToggling(false);
        }
    }

    private void ApplyFilter()
    {
        var filter = (StartupFilter)Math.Clamp(SelectedFilterIndex, 0, 2);
        var search = SearchText?.Trim() ?? string.Empty;
        var filtered = _all.Where(i => filter switch
            {
                StartupFilter.Enabled => i.IsEnabled,
                StartupFilter.Recommended => i.IsRecommendedToDisable,
                _ => true,
            })
            .Where(i => search.Length == 0
                || i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || i.PublisherText.Contains(search, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
        CollectionSync.SyncTo(Items, filtered);
        EmptyText = _all.Count == 0 ? T("Startup_Empty_None") : T("Startup_Empty_Filter");
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void UpdateSummary()
    {
        var enabled = _all.Count(i => i.IsEnabled);
        SummaryText = T("Startup_Summary", enabled, _all.Count);
        RecommendedCount = _all.Count(i => i.IsRecommendedToDisable);
    }
}
