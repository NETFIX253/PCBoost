using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Drivers;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Sévérité d'un bandeau d'information (InfoBar), indépendante de WinUI.</summary>
public enum NoticeSeverity { Informational = 0, Success = 1, Warning = 2, Error = 3 }

/// <summary>Bandeau d'état de la page Pilotes (sévérité + texte).</summary>
public sealed record DriverNotice(NoticeSeverity Severity, string Title, string Message);

/// <summary>
/// Mises à jour de pilotes sécurisées : recherche dans Windows Update, liste classée (recommandées présélectionnées,
/// à examiner, non proposées), aperçu de ce qui va être modifié, installation après un point de restauration Windows
/// neuf, résultat par pilote avec retour au pilote précédent.
/// </summary>
public sealed partial class DriversViewModel : ViewModelBase
{
    private readonly IDriverUpdateService _drivers;
    private readonly INetworkCostProvider _network;
    private readonly IRollbackManager _rollback;
    private readonly IShellService _shell;
    private DriverScanResult? _scan;
    private bool _protectionRequired;
    private bool _selectionSharesDevice;
    private IReadOnlyList<DriverUpdateItemViewModel> _installing = [];

    public DriversViewModel(ViewModelContext context, IDriverUpdateService drivers, INetworkCostProvider network, IRollbackManager rollback, IShellService shell)
        : base(context)
    {
        _drivers = drivers ?? throw new ArgumentNullException(nameof(drivers));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    public ObservableCollection<DriverUpdateItemViewModel> Recommended { get; } = [];

    public ObservableCollection<DriverUpdateItemViewModel> Review { get; } = [];

    public ObservableCollection<DriverUpdateItemViewModel> Excluded { get; } = [];

    public ObservableCollection<DriverNotice> Notices { get; } = [];

    public ObservableCollection<DriverResultItemViewModel> Results { get; } = [];

    /// <summary>Sources officielles complémentaires (fabricants), jamais utilisées par PCBoost pour installer.</summary>
    public ObservableCollection<OfficialSourceItemViewModel> Sources { get; } = [];

    [ObservableProperty]
    public partial bool ShowSources { get; private set; }

    [ObservableProperty]
    public partial string ComputerText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRestorePointCommand))]
    public partial bool IsCreatingRestorePoint { get; private set; }

    [ObservableProperty]
    public partial string RestorePointNowText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowUpToDate))]
    public partial bool IsScanning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowList), nameof(ShowUpToDate))]
    public partial bool HasScanned { get; private set; }

    [ObservableProperty]
    public partial string LastScanText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RecommendedHeader { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ReviewHeader { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ExcludedHeader { get; private set; } = string.Empty;

    public bool HasRecommended => Recommended.Count > 0;

    public bool HasReview => Review.Count > 0;

    public bool HasExcluded => Excluded.Count > 0;

    public bool ShowList => HasScanned && !IsScanning && (HasRecommended || HasReview || HasExcluded);

    /// <summary>Recherche réussie sans mise à jour installable.</summary>
    public bool ShowUpToDate => HasScanned && !IsScanning && _scan is { State: DriverScanState.Ready } && !HasRecommended && !HasReview;

    [ObservableProperty]
    public partial string SelectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    public partial int SelectedCount { get; private set; }

    /// <summary>La protection du système est désactivée : son activation doit être autorisée pour installer.</summary>
    [ObservableProperty]
    public partial bool ShowProtectionChoice { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    [NotifyPropertyChangedFor(nameof(InstallBlockedText))]
    public partial bool EnableProtection { get; set; }

    [ObservableProperty]
    public partial bool IsMetered { get; private set; }

    [ObservableProperty]
    public partial string MeteredText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand), nameof(InstallCommand), nameof(CreateRestorePointCommand))]
    public partial bool IsInstalling { get; private set; }

    [ObservableProperty]
    public partial string InstallStepText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double InstallProgress { get; private set; }

    [ObservableProperty]
    public partial bool InstallProgressKnown { get; private set; }

    [ObservableProperty]
    public partial bool HasResult { get; private set; }

    [ObservableProperty]
    public partial NoticeSeverity ResultSeverity { get; private set; }

    [ObservableProperty]
    public partial string ResultTitle { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultMessage { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string RestorePointText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowRestartNotice { get; private set; }

    /// <summary>Raison pour laquelle l'installation est impossible (vide si elle est possible).</summary>
    public string InstallBlockedText
    {
        get
        {
            if (_scan is null || _scan.State != DriverScanState.Ready) return string.Empty;
            if (_scan.Error is not null) return T("Drivers_Blocked_Devices");
            if (_scan.RebootPending) return T("Drivers_Blocked_Reboot");
            if (_scan.Protection == SystemProtectionState.DisabledByPolicy) return T("Drivers_Blocked_ProtectionPolicy");
            if (ShowProtectionChoice && !EnableProtection) return T("Drivers_Blocked_Protection");
            if (_selectionSharesDevice) return T("Drivers_Blocked_SameDevice");
            return string.Empty;
        }
    }

    public bool HasInstallBlockedText => !string.IsNullOrEmpty(InstallBlockedText);

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (_drivers.Latest is { } latest) Apply(latest);
        else await ScanCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanScan), IncludeCancelCommand = true)]
    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        ErrorText = null;
        IsScanning = true;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var scan = await _drivers.ScanAsync(ct).ConfigureAwait(true);
                Apply(scan);
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsScanning = false;
        }
    }

    private bool CanScan() => !IsScanning && !IsInstalling;

    internal void Apply(DriverScanResult scan)
    {
        _scan = scan;
        CollectionSync.Replace(Recommended, Items(scan, DriverUpdateTier.Recommended));
        CollectionSync.Replace(Review, Items(scan, DriverUpdateTier.Review));
        CollectionSync.Replace(Excluded, Items(scan, DriverUpdateTier.Excluded));
        RecommendedHeader = T("Drivers_Section_Recommended", Recommended.Count);
        ReviewHeader = T("Drivers_Section_Review", Review.Count);
        ExcludedHeader = T("Drivers_Section_Excluded", Excluded.Count);
        LastScanText = T("Drivers_LastScan", Formatter.DateTimeFull(scan.ScannedAt));
        // Protection désactivée (lue dans le registre, ou constatée par un refus faute de point de restauration) : le choix
        // est proposé pour installer ET pour le point de restauration à la demande (même sans mise à jour à installer).
        ShowProtectionChoice = scan.State != DriverScanState.ExcludedByPolicy && scan.Protection != SystemProtectionState.DisabledByPolicy
            && (scan.Protection == SystemProtectionState.Disabled || _protectionRequired);
        IsMetered = _network.IsMeteredConnection() == true;
        CollectionSync.Replace(Sources, scan.Sources.Select(s => new OfficialSourceItemViewModel(s, Localizer, OpenSource)).ToList());
        ShowSources = scan.State != DriverScanState.ExcludedByPolicy;
        ComputerText = scan.Computer is { Manufacturer: { } maker } computer
            ? T("Drivers_Sources_Computer", computer.Model is { } model ? $"{maker} · {model}" : maker)
            : string.Empty;
        BuildNotices(scan);
        HasScanned = true;
        OnPropertyChanged(nameof(HasRecommended));
        OnPropertyChanged(nameof(HasReview));
        OnPropertyChanged(nameof(HasExcluded));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowUpToDate));
        OnPropertyChanged(nameof(InstallBlockedText));
        OnPropertyChanged(nameof(HasInstallBlockedText));
        OnPropertyChanged(nameof(HasSources));
        CreateRestorePointCommand.NotifyCanExecuteChanged();
        UpdateSelection();
    }

    public bool HasSources => Sources.Count > 0;

    private List<DriverUpdateItemViewModel> Items(DriverScanResult scan, DriverUpdateTier tier)
        => scan.Candidates.Where(c => c.Tier == tier).Select(c => new DriverUpdateItemViewModel(c, Localizer, Formatter, UpdateSelection, OpenWindowsUpdate)).ToList();

    private void BuildNotices(DriverScanResult scan)
    {
        var notices = new List<DriverNotice>();
        switch (scan.State)
        {
            case DriverScanState.ExcludedByPolicy:
                notices.Add(new DriverNotice(NoticeSeverity.Informational, T("Drivers_Notice_Policy_Title"), T("Drivers_Notice_Policy")));
                break;
            case DriverScanState.UpdateServiceDisabled:
                notices.Add(new DriverNotice(NoticeSeverity.Warning, T("Drivers_Notice_Service_Title"), T("Drivers_Notice_Service")));
                break;
            case DriverScanState.SearchFailed:
                notices.Add(new DriverNotice(NoticeSeverity.Error, T("Drivers_Notice_SearchFailed_Title"),
                    scan.Error is { } error ? DescribeError(error) : T("Drv_Error_SearchFailed")));
                break;
        }
        if (scan.RebootPending) notices.Add(new DriverNotice(NoticeSeverity.Warning, T("Drivers_Notice_Reboot_Title"), T("Drivers_Notice_Reboot")));
        if (scan.InstallerBusy) notices.Add(new DriverNotice(NoticeSeverity.Informational, T("Drivers_Notice_Busy_Title"), T("Drivers_Notice_Busy")));
        if (scan.Protection == SystemProtectionState.DisabledByPolicy)
            notices.Add(new DriverNotice(NoticeSeverity.Error, T("Drivers_Notice_ProtectionPolicy_Title"), T("Drv_Stop_ProtectionPolicy")));
        if (scan.ManagedUpdateServer) notices.Add(new DriverNotice(NoticeSeverity.Informational, T("Drivers_Notice_Managed_Title"), T("Drivers_Notice_Managed")));
        if (scan.State == DriverScanState.Ready && scan.Error is not null)
            notices.Add(new DriverNotice(NoticeSeverity.Warning, T("Drivers_Notice_Devices_Title"), T("Drivers_Notice_Devices")));
        CollectionSync.Replace(Notices, notices);
        OnPropertyChanged(nameof(HasNotices));
    }

    public bool HasNotices => Notices.Count > 0;

    private IEnumerable<DriverUpdateItemViewModel> AllItems => Recommended.Concat(Review);

    private List<DriverUpdateItemViewModel> SelectedItems => AllItems.Where(i => i.IsSelected && i.IsSelectable).ToList();

    private void UpdateSelection()
    {
        var selected = SelectedItems;
        SelectedCount = selected.Count;
        _selectionSharesDevice = DriverUpdatePolicy.SharesDevice(selected.Select(i => i.Candidate));
        OnPropertyChanged(nameof(InstallBlockedText));
        OnPropertyChanged(nameof(HasInstallBlockedText));
        InstallCommand.NotifyCanExecuteChanged();
        var bytes = selected.Sum(i => i.Candidate.Offer.DownloadBytes ?? 0);
        var unknown = selected.Any(i => i.Candidate.Offer.DownloadBytes is null);
        var size = bytes > 0 ? Formatter.Bytes(bytes) : T("Drivers_SizeUnknown");
        SelectionText = selected.Count == 0
            ? T("Drivers_Selection_None")
            : T(unknown && bytes > 0 ? "Drivers_Selection_AtLeast" : "Drivers_Selection", selected.Count, size);
        MeteredText = T("Drivers_Notice_Metered", size);
    }

    [RelayCommand]
    private void SelectRecommended()
    {
        foreach (var item in Recommended) item.IsSelected = true;
        foreach (var item in Review) item.IsSelected = false;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var item in AllItems) item.IsSelected = false;
    }

    private bool CanInstall()
        => !IsScanning && !IsInstalling && SelectedCount > 0 && _scan is { } scan && scan.CanInstall
           && !(ShowProtectionChoice && !EnableProtection)
           && !_selectionSharesDevice
           && SelectedCount <= DriverElevatedData.MaxUpdates;

    /// <summary>Aperçu de ce qui va être modifié, confirmation, puis installation (une autorisation administrateur).</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var selected = SelectedItems;
        if (selected.Count == 0) return;

        var details = new List<TextRef>();
        // Chaque pilote : version actuelle → nouvelle ; retour limité au point de restauration signalé (extension, version inconnue…).
        details.AddRange(selected.Select(i => TextRef.Of(
            DriverUpdatePolicy.SupportsTargetedRollback(i.Candidate) ? "Drivers_Confirm_Item" : "Drivers_Confirm_ItemRestorePoint", i.DeviceName,
            DriverUpdateItemViewModel.Version(i.Candidate.Device?.Version, Localizer), DriverUpdateItemViewModel.Version(i.Candidate.Offer.Version, Localizer))));
        details.Add(TextRef.Of("Drivers_Confirm_RestorePoint"));
        if (ShowProtectionChoice && EnableProtection) details.Add(TextRef.Of("Drivers_Confirm_EnableProtection"));
        details.Add(TextRef.Of("Drivers_Confirm_Admin"));
        if (selected.Any(i => i.Candidate.Offer.RebootBehavior != DriverRebootBehavior.Never)) details.Add(TextRef.Of("Drivers_Confirm_Restart"));
        if (IsMetered) details.Add(TextRef.Of("Drivers_Confirm_Metered"));
        details.Add(TextRef.Of("Drivers_Confirm_Rollback"));
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Drivers_Confirm_Title", selected.Count),
            TextRef.Of("Drivers_Confirm_Message"),
            TextRef.Of("Drivers_Confirm_Install"),
            CloseButton: TextRef.Of("Common_Action_Cancel"),
            Details: details)).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        ErrorText = null;
        StatusMessage = null;
        HasResult = false;
        Results.Clear();
        _installing = selected;
        IsInstalling = true;
        InstallProgressKnown = false;
        InstallProgress = 0;
        InstallStepText = T("Drivers_Step_Waiting");
        var progress = UiProgress<DriverInstallProgress>(OnProgress);
        DriverInstallResult? result = null;
        try
        {
            // L'installation n'est pas interrompue si l'utilisateur quitte la page : l'état des pilotes doit rester connu.
            await RunSafeAsync(async _ =>
            {
                // Activation de la protection : uniquement si elle était proposée ET cochée (jamais une case restée cochée d'avant).
                result = await _drivers.InstallAsync(selected.Select(i => i.Candidate).ToList(), ShowProtectionChoice && EnableProtection, progress, CancellationToken.None)
                    .ConfigureAwait(true);
                await ShowResultAsync(result, selected).ConfigureAwait(true);
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsInstalling = false;
            _installing = [];
        }

        // Liste mise à jour (les pilotes installés n'y figurent plus), le résultat restant affiché.
        if (result is not null && IsActive) await ScanCommand.ExecuteAsync(null).ConfigureAwait(true);
    }

    internal void OnProgress(DriverInstallProgress progress)
    {
        var name = progress.Index >= 1 && progress.Index <= _installing.Count ? _installing[progress.Index - 1].DeviceName : string.Empty;
        InstallStepText = progress.Stage switch
        {
            DriverInstallStage.RestorePoint => T("Drivers_Step_RestorePoint"),
            DriverInstallStage.Searching => T("Drivers_Step_Searching"),
            _ => T("Drivers_Step_" + progress.Stage, progress.Index, progress.Total, name),
        };
        InstallProgress = ProgressPercent(progress);
        InstallProgressKnown = true;
    }

    /// <summary>Avancement : 5 % revérification, 10 % point de restauration, puis chaque pilote (téléchargement, installation, vérification).</summary>
    internal static double ProgressPercent(DriverInstallProgress progress)
    {
        if (progress.Total <= 0) return 0;
        var stage = progress.Stage switch
        {
            DriverInstallStage.Searching => (double?)null,
            DriverInstallStage.RestorePoint => null,
            DriverInstallStage.Downloading => 0.1,
            DriverInstallStage.Installing => 0.4,
            _ => 0.9,
        };
        if (stage is not { } fraction) return progress.Stage == DriverInstallStage.Searching ? 5 : 10;
        var index = Math.Clamp(progress.Index, 1, progress.Total);
        return Math.Clamp(10 + 90 * ((index - 1) + fraction) / progress.Total, 10, 100);
    }

    internal async Task ShowResultAsync(DriverInstallResult result, IReadOnlyList<DriverUpdateItemViewModel> selected)
    {
        var changes = await ChangesBySessionAsync(result.SessionId).ConfigureAwait(true);
        var byId = result.Drivers.ToDictionary(d => d.UpdateId, StringComparer.OrdinalIgnoreCase);
        var items = new List<DriverResultItemViewModel>();
        foreach (var item in selected)
        {
            var offer = item.Candidate.Offer;
            byId.TryGetValue(offer.UpdateId, out var outcome);
            changes.TryGetValue(offer.UpdateId, out var change);
            var status = outcome?.Status ?? DriverInstallStatus.NotRun;
            // Retour ciblé : pilote installé (consigné « appliqué »), ou résultat inconnu (consigné « en attente ») — le
            // retour ne touche que les périphériques encore sur la version installée.
            var canRollback = change is { Reversible: true }
                              && (status == DriverInstallStatus.Installed && change.Status == ChangeStatus.Applied
                                  || status == DriverInstallStatus.Unknown && change.Status == ChangeStatus.Pending);
            var (level, text) = Describe(status, outcome, restorePointOnly: status == DriverInstallStatus.Installed && !canRollback);
            items.Add(new DriverResultItemViewModel(item.DeviceName,
                T("Drivers_Result_Versions", DriverUpdateItemViewModel.Version(item.Candidate.Device?.Version, Localizer),
                    DriverUpdateItemViewModel.Version(outcome?.NewVersion ?? offer.Version, Localizer)),
                level, text, canRollback ? change!.Id : null, T("Drivers_Action_Rollback"), RollbackAsync));
        }
        CollectionSync.Replace(Results, items);

        var installed = result.InstalledCount;
        ResultSeverity = result.Outcome.Success ? NoticeSeverity.Success : installed > 0 ? NoticeSeverity.Warning : NoticeSeverity.Error;
        ResultTitle = installed > 0 ? T("Drivers_Result_Title", installed, result.Drivers.Count) : T("Drivers_Result_NoneTitle");
        ResultMessage = result.Stop == DriverInstallStop.DeviceProblem
            ? T("Drv_Stop_DeviceProblem")
            : result.Outcome.Success ? T("Drivers_Result_Success") : DescribeError(result.Outcome);
        RestorePointText = result.RestorePoint == RestorePointStatus.Created && result.RestorePointAt is { } at
            ? T(result.ProtectionEnabled ? "Drivers_Result_RestorePointProtection" : "Drivers_Result_RestorePoint", Formatter.DateTimeFull(at))
            : string.Empty;
        ShowRestartNotice = result.RebootRequired && installed > 0;
        if (result.Stop == DriverInstallStop.ProtectionDisabled) _protectionRequired = true;
        HasResult = true;
    }

    private (DriverOutcomeLevel Level, string Text) Describe(DriverInstallStatus status, DriverInstallOutcome? outcome, bool restorePointOnly) => status switch
    {
        DriverInstallStatus.Installed when outcome is { ProblemCodeAfter: > 0 } => (DriverOutcomeLevel.Critical, T("Drivers_Outcome_InstalledProblem", outcome.ProblemCodeAfter!.Value)),
        DriverInstallStatus.Installed when outcome is { RebootRequired: true } => (DriverOutcomeLevel.Good, T("Drivers_Outcome_InstalledRestart")),
        DriverInstallStatus.Installed when restorePointOnly => (DriverOutcomeLevel.Good, T("Drivers_Outcome_InstalledRestorePoint")),
        DriverInstallStatus.Installed => (DriverOutcomeLevel.Good, T("Drivers_Outcome_Installed")),
        DriverInstallStatus.Unknown => (DriverOutcomeLevel.Warning, T("Drv_Outcome_Unknown")),
        DriverInstallStatus.Failed => (DriverOutcomeLevel.Critical, T("Drv_Outcome_Failed")),
        DriverInstallStatus.NoLongerOffered => (DriverOutcomeLevel.Warning, T("Drv_Outcome_NoLongerOffered")),
        DriverInstallStatus.Refused => (DriverOutcomeLevel.Warning, T("Drv_Outcome_Refused")),
        _ => (DriverOutcomeLevel.Warning, T("Drv_Outcome_NotRun")),
    };

    /// <summary>Modifications de la session d'installation, par identifiant de mise à jour.</summary>
    private async Task<Dictionary<string, ChangeRecord>> ChangesBySessionAsync(Guid? sessionId)
    {
        var map = new Dictionary<string, ChangeRecord>(StringComparer.OrdinalIgnoreCase);
        if (sessionId is not { } id) return map;
        var session = await _rollback.GetSessionAsync(id, CancellationToken.None).ConfigureAwait(true);
        foreach (var change in session?.Changes ?? [])
        {
            if (change.Kind == ChangeKinds.DriverUpdate && ChangeStateSerializer.Deserialize<DriverUpdateState>(change.BeforeState) is { } state)
                map[state.UpdateId] = change;
        }
        return map;
    }

    internal async Task RollbackAsync(DriverResultItemViewModel item)
    {
        if (item.ChangeId is not { } changeId || item.IsRollingBack) return;
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("Drivers_Rollback_Title", item.DeviceName),
            TextRef.Of("Drivers_Rollback_Message"),
            TextRef.Of("Drivers_Action_Rollback"),
            CloseButton: TextRef.Of("Common_Action_Cancel"))).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        item.IsRollingBack = true;
        try
        {
            await RunSafeAsync(async _ =>
            {
                var result = await _drivers.RollbackAsync(changeId, CancellationToken.None).ConfigureAwait(true);
                if (!CheckResult(result)) return;
                item.IsRolledBack = true;
                item.Level = DriverOutcomeLevel.Warning;
                item.OutcomeText = result.Message is { } message ? T(message) : T("Drv_Rollback_Done");
                StatusMessage = item.OutcomeText;
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            item.IsRollingBack = false;
        }
    }

    /// <summary>Site officiel d'un fabricant (adresse HTTPS de la liste fermée), ouvert dans le navigateur.</summary>
    private void OpenSource(Uri uri) => CheckResult(_shell.OpenUri(uri));

    /// <summary>Point de restauration neuf à la demande, avant l'utilisation de l'outil d'un fabricant.</summary>
    [RelayCommand(CanExecute = nameof(CanCreateRestorePoint))]
    private async Task CreateRestorePointAsync()
    {
        IsCreatingRestorePoint = true;
        RestorePointNowText = string.Empty;
        try
        {
            await RunSafeAsync(async _ =>
            {
                var result = await _drivers.CreateRestorePointAsync(ShowProtectionChoice && EnableProtection, CancellationToken.None).ConfigureAwait(true);
                if (result.IsCreated && result.CreatedAt is { } at)
                {
                    RestorePointNowText = T(result.ProtectionEnabled ? "Drivers_Result_RestorePointProtection" : "Drivers_Result_RestorePoint", Formatter.DateTimeFull(at));
                    return;
                }
                if (result.Status == RestorePointStatus.Disabled) _protectionRequired = true;
                CheckResult(result.Outcome.Success ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Opt_RestorePoint_Failed")) : result.Outcome);
                if (_scan is not null) Apply(_scan);
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsCreatingRestorePoint = false;
        }
    }

    private bool CanCreateRestorePoint()
        => !IsCreatingRestorePoint && !IsInstalling && _scan?.Protection != SystemProtectionState.DisabledByPolicy;

    [RelayCommand]
    private void OpenHistory() => Navigation.Navigate(PageKeys.History);

    [RelayCommand]
    private void OpenSystemRestore() => CheckResult(_shell.OpenSystemRestore());

    [RelayCommand]
    private void OpenWindowsUpdate() => CheckResult(_shell.OpenWindowsSettings(WindowsSettingsPages.OptionalUpdates));

    partial void OnEnableProtectionChanged(bool value) => OnPropertyChanged(nameof(HasInstallBlockedText));
}
