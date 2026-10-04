using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Programs;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

public enum ProgramSort { Size = 0, LastUsed = 1, Name = 2 }

/// <summary>Programme installé : taille, date d'installation, dernière utilisation connue (avec sa source), désinstallation.</summary>
public sealed partial class ProgramItemViewModel : ObservableObject
{
    private readonly Func<ProgramItemViewModel, Task> _uninstall;

    public ProgramItemViewModel(InstalledProgram program, DateTimeOffset now, ILocalizer localizer, IValueFormatter formatter, Func<ProgramItemViewModel, Task> uninstall)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = program;
        _uninstall = uninstall;
        Name = program.Name;
        var details = new List<string> { string.IsNullOrWhiteSpace(program.Publisher) ? localizer.Get("Programs_UnknownPublisher") : program.Publisher! };
        if (!string.IsNullOrWhiteSpace(program.Version)) details.Add(localizer.Format("Programs_Version", program.Version!));
        if (program.InstallDate is { } d) details.Add(localizer.Format("Programs_Installed", d.ToString("d", localizer.Culture)));
        if (program.Scope == ProgramScope.User) details.Add(localizer.Get("Programs_UserScope"));
        DetailsText = string.Join(" · ", details);
        SizeText = program.SizeBytes is { } bytes ? formatter.Bytes(bytes) : localizer.Get("Programs_SizeUnknown");
        SizeHint = program.SizeBytes is null ? string.Empty : localizer.Get(program.SizeFromRegistry ? "Programs_SizeFromRegistry" : "Programs_SizeMeasured");
        IsRarelyUsed = program.IsRarelyUsed(now);
        // Le dernier accès aux fichiers n'est pas une utilisation certaine (une lecture suffit) : il est libellé comme tel.
        LastUsedText = program.LastUsed is not { } used
            ? localizer.Get("Programs_LastUsedUnknown")
            : program.LastUseSource == LastUseSource.WindowsPrefetch
                ? localizer.Format("Programs_LastUsed", formatter.DateTime(used))
                : localizer.Format("Programs_LastAccess", formatter.DateTime(used));
        LastUsedHint = program.LastUseSource switch
        {
            LastUseSource.WindowsPrefetch => localizer.Get("Programs_Source_Prefetch"),
            LastUseSource.FileAccess => localizer.Get("Programs_Source_FileAccess"),
            _ => string.Empty,
        };
        RarelyUsedBadge = localizer.Get("Programs_Badge_RarelyUsed");
        UninstallUnavailableText = program.CanUninstall ? string.Empty : localizer.Get("Programs_UseWindowsSettings");
        AccessibleName = $"{Name}, {SizeText}, {LastUsedText}";
    }

    public InstalledProgram Model { get; }

    public string Name { get; }

    public string DetailsText { get; }

    public string SizeText { get; }

    public string SizeHint { get; }

    public string LastUsedText { get; }

    public string LastUsedHint { get; }

    public bool HasLastUsedHint => !string.IsNullOrEmpty(LastUsedHint);

    public bool IsRarelyUsed { get; }

    public string RarelyUsedBadge { get; }

    public bool CanUninstall => Model.CanUninstall;

    public string UninstallUnavailableText { get; }

    public string AccessibleName { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallCommand))]
    public partial bool IsBusy { get; internal set; }

    [RelayCommand(CanExecute = nameof(CanRunUninstall))]
    private Task UninstallAsync() => _uninstall(this);

    private bool CanRunUninstall() => CanUninstall && !IsBusy;
}

/// <summary>
/// Désinstallation assistée : programmes installés triés par taille ou par dernière utilisation, repérage des programmes
/// peu utilisés, désinstallation par le programme officiel de l'éditeur après confirmation (action définitive, inscrite
/// au journal). Les composants protégés ne sont jamais proposés.
/// </summary>
public sealed partial class ProgramsViewModel : ViewModelBase
{
    private readonly IProgramInventoryService _inventory;
    private readonly IShellService _shell;
    private readonly List<ProgramItemViewModel> _all = [];
    private CancellationTokenSource? _waitCts;

    public ProgramsViewModel(ViewModelContext context, IProgramInventoryService inventory, IShellService shell)
        : base(context)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        SortOptions =
        [
            new OptionItem(nameof(ProgramSort.Size), T("Programs_Sort_Size")),
            new OptionItem(nameof(ProgramSort.LastUsed), T("Programs_Sort_LastUsed")),
            new OptionItem(nameof(ProgramSort.Name), T("Programs_Sort_Name")),
        ];
    }

    public ObservableCollection<ProgramItemViewModel> Items { get; } = [];

    public IReadOnlyList<OptionItem> SortOptions { get; }

    [ObservableProperty]
    public partial int SelectedSortIndex { get; set; }

    [ObservableProperty]
    public partial bool RarelyUsedOnly { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ReadLastRunCommand))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Items.Count == 0;

    [ObservableProperty]
    public partial string EmptyText { get; private set; } = string.Empty;

    /// <summary>« 38 programmes · 24,6 Go déclarés ».</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string HiddenText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LastRunText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial int RarelyUsedCount { get; private set; }

    /// <summary>Un programme de désinstallation est ouvert : PCBoost attend qu'il se termine.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ReadLastRunCommand), nameof(StopWaitingCommand))]
    public partial bool IsUninstalling { get; private set; }

    [ObservableProperty]
    public partial string UninstallingText { get; private set; } = string.Empty;

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    protected override void OnDeactivated() => _waitCts?.Cancel();

    partial void OnSelectedSortIndexChanged(int value) => ApplyFilter();

    partial void OnRarelyUsedOnlyChanged(bool value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var inventory = await _inventory.GetInventoryAsync(ct).ConfigureAwait(true);
                Apply(inventory);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanRefresh() => !IsLoading && !IsUninstalling;

    /// <summary>Précise les dates avec les exécutions enregistrées par Windows (autorisation administrateur, lecture seule).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ReadLastRunAsync(CancellationToken cancellationToken)
    {
        StatusMessage = null;
        var ok = await RunSafeAsync(async ct =>
        {
            var result = await _inventory.ReadLastRunAsync(ct).ConfigureAwait(true);
            if (!CheckResult(result)) return;
            StatusMessage = result.Message is { } message ? T(message) : T("Programs_LastRun_Read");
        }, cancellationToken).ConfigureAwait(true);
        if (ok && ErrorText is null) await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenWindowsSettings() => CheckResult(_shell.OpenWindowsSettings(WindowsSettingsPages.InstalledApps));

    [RelayCommand(CanExecute = nameof(IsUninstalling))]
    private void StopWaiting() => _waitCts?.Cancel();

    internal async Task UninstallAsync(ProgramItemViewModel item)
    {
        if (IsUninstalling || !item.CanUninstall) return;
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Programs_Confirm_Title", item.Name),
            TextRef.Of("Programs_Confirm_Message"),
            TextRef.Of("Programs_Confirm_Uninstall"),
            CloseButton: TextRef.Of("Common_Action_Cancel"),
            IsDestructive: true,
            Details: [TextRef.Of("Programs_Confirm_Irreversible"), TextRef.Of("Programs_Confirm_Official")])).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        ErrorText = null;
        StatusMessage = null;
        item.IsBusy = true;
        IsUninstalling = true;
        UninstallingText = T("Programs_Uninstalling", item.Name);
        _waitCts = new CancellationTokenSource();
        try
        {
            await RunSafeAsync(async _ =>
            {
                var result = await _inventory.UninstallAsync(item.Model, _waitCts.Token).ConfigureAwait(true);
                switch (result.Outcome)
                {
                    case UninstallOutcome.Removed:
                        StatusMessage = T("Programs_Removed", item.Name);
                        break;
                    case UninstallOutcome.StillInstalled:
                        StatusMessage = T("Programs_StillInstalled", item.Name);
                        break;
                    default:
                        CheckResult(result.Error ?? OperationResult.Fail(OperationErrorKind.Failed));
                        break;
                }
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            item.IsBusy = false;
            IsUninstalling = false;
            _waitCts.Dispose();
            _waitCts = null;
        }

        if (IsActive) await RefreshAsync(PageToken).ConfigureAwait(true);
    }

    internal void Apply(ProgramInventory inventory)
    {
        var now = Context.Clock.UtcNow;
        _all.Clear();
        _all.AddRange(inventory.Programs.Select(p => new ProgramItemViewModel(p, now, Localizer, Formatter, UninstallAsync)));
        var total = inventory.Programs.Sum(p => p.SizeBytes ?? 0);
        SummaryText = T("Programs_Summary", _all.Count, Formatter.Bytes(total));
        HiddenText = inventory.HiddenCount > 0 ? T("Programs_Hidden", inventory.HiddenCount) : string.Empty;
        LastRunText = inventory.LastRunReadAt is { } readAt ? T("Programs_LastRun_ReadAt", Formatter.DateTimeFull(readAt)) : T("Programs_LastRun_Approximate");
        RarelyUsedCount = _all.Count(i => i.IsRarelyUsed);
        IsLoaded = true;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var search = SearchText?.Trim() ?? string.Empty;
        IEnumerable<ProgramItemViewModel> items = _all
            .Where(i => !RarelyUsedOnly || i.IsRarelyUsed)
            .Where(i => search.Length == 0
                || i.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || (i.Model.Publisher?.Contains(search, StringComparison.CurrentCultureIgnoreCase) ?? false));
        items = (ProgramSort)Math.Clamp(SelectedSortIndex, 0, 2) switch
        {
            ProgramSort.LastUsed => items.OrderBy(i => i.Model.LastUsed ?? DateTimeOffset.MaxValue).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            ProgramSort.Name => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => items.OrderByDescending(i => i.Model.SizeBytes ?? -1).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
        };
        CollectionSync.Replace(Items, items.ToList());
        EmptyText = _all.Count == 0 ? T("Programs_Empty_None") : T("Programs_Empty_Filter");
        OnPropertyChanged(nameof(ShowEmpty));
    }
}
