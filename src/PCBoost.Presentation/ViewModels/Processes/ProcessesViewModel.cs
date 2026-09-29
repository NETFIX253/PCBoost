using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

public enum ProcessSortColumn { Name = 0, ProcessId, Cpu, Memory, Disk, Publisher, Trust }

/// <summary>Processus affiché (mis à jour en place à chaque rafraîchissement pour préserver la sélection).</summary>
public sealed partial class ProcessItemViewModel : ObservableObject
{
    private readonly ILocalizer _localizer;
    private readonly IValueFormatter _formatter;

    public ProcessItemViewModel(ProcessInfo info, ILocalizer localizer, IValueFormatter formatter)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        ProcessId = info.ProcessId;
        Update(info);
    }

    public int ProcessId { get; }

    public ProcessInfo Model { get; private set; } = null!;

    [ObservableProperty]
    public partial string Name { get; private set; } = string.Empty;

    /// <summary>Titre de la fenêtre ou description du fichier, si disponible.</summary>
    [ObservableProperty]
    public partial string DisplayDescription { get; private set; } = string.Empty;

    public string ProcessIdText => ProcessId.ToString(_localizer.Culture);

    [ObservableProperty]
    public partial double CpuPercent { get; private set; }

    [ObservableProperty]
    public partial string CpuText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial long MemoryBytes { get; private set; }

    [ObservableProperty]
    public partial string MemoryText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double DiskBytesPerSec { get; private set; }

    [ObservableProperty]
    public partial string DiskText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PublisherText { get; private set; } = string.Empty;

    /// <summary>« Signé par Microsoft / Signé par X / Non signé / Signature invalide — à examiner / Inconnu ».</summary>
    [ObservableProperty]
    public partial string TrustText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string TrustGlyph { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string PathText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasPath { get; private set; }

    [ObservableProperty]
    public partial string ProtectionText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsCritical { get; private set; }

    [ObservableProperty]
    public partial bool IsSensitive { get; private set; }

    [ObservableProperty]
    public partial bool HasWindow { get; private set; }

    [ObservableProperty]
    public partial string StartTimeText { get; private set; } = string.Empty;

    public string ProtectionGlyph => IsCritical ? Glyphs.Blocked : IsSensitive ? Glyphs.Warning : string.Empty;

    /// <summary>Libellé court de la liste (« Protégé », « Système ») ; vide pour un processus ordinaire.</summary>
    public string ProtectionBadgeText => IsCritical ? _localizer.Get("Processes_Badge_Critical") : IsSensitive ? _localizer.Get("Processes_Badge_Sensitive") : string.Empty;

    public bool HasProtectionBadge => IsCritical || IsSensitive;

    public string AccessibleName => HasProtectionBadge
        ? $"{Name}, {ProtectionBadgeText}, {CpuText}, {MemoryText}, {TrustText}"
        : $"{Name}, {CpuText}, {MemoryText}, {TrustText}";

    internal int TrustSortKey => Model.Trust.Level switch
    {
        TrustLevel.WindowsComponent => 0,
        TrustLevel.SignedPublisher => 1,
        TrustLevel.Unknown => 2,
        TrustLevel.Unsigned => 3,
        _ => 4,
    };

    internal void Update(ProcessInfo info)
    {
        Model = info;
        if (Name != info.Name) Name = info.Name;
        var description = !string.IsNullOrWhiteSpace(info.WindowTitle) ? info.WindowTitle! : info.Trust.Description ?? string.Empty;
        if (DisplayDescription != description) DisplayDescription = description;
        if (Math.Abs(CpuPercent - info.CpuPercent) > 0.01)
        {
            CpuPercent = info.CpuPercent;
        }

        var cpu = _formatter.Percent(info.CpuPercent, 1);
        if (CpuText != cpu) CpuText = cpu;
        if (MemoryBytes != info.MemoryBytes)
        {
            MemoryBytes = info.MemoryBytes;
            MemoryText = _formatter.Bytes(info.MemoryBytes);
        }
        else if (MemoryText.Length == 0)
        {
            MemoryText = _formatter.Bytes(info.MemoryBytes);
        }

        DiskBytesPerSec = info.DiskBytesPerSec ?? -1;
        var disk = _formatter.Rate(info.DiskBytesPerSec);
        if (DiskText != disk) DiskText = disk;

        var publisher = info.Trust.Publisher ?? info.Trust.Signature.Signer ?? _localizer.Get("Processes_UnknownPublisher");
        if (PublisherText != publisher) PublisherText = publisher;
        var trust = _localizer.Trust(info.Trust);
        if (TrustText != trust)
        {
            TrustText = trust;
            TrustGlyph = Labels.TrustGlyph(info.Trust.Level);
        }

        PathText = info.ExecutablePath ?? _localizer.Get("Processes_PathUnavailable");
        HasPath = !string.IsNullOrEmpty(info.ExecutablePath);
        IsCritical = info.Protection.Level == ProtectionLevel.Critical;
        IsSensitive = info.Protection.Level == ProtectionLevel.Sensitive;
        ProtectionText = info.Protection.Reason is { } reason
            ? _localizer.Format(reason)
            : _localizer.Get($"Processes_Protection_{info.Protection.Level}");
        HasWindow = info.HasWindow;
        StartTimeText = info.StartTime is { } st ? _formatter.DateTimeFull(st) : _formatter.NotAvailable;
        OnPropertyChanged(nameof(ProtectionGlyph));
        OnPropertyChanged(nameof(ProtectionBadgeText));
        OnPropertyChanged(nameof(HasProtectionBadge));
    }
}

/// <summary>
/// Gestionnaire de processus (§13) : liste triable, recherche, rafraîchissement toutes les 2 s tant que la page
/// est visible (suspendu pendant une action), détail, fermeture propre, terminaison (confirmation, bloquée pour les
/// processus critiques), emplacement, propriétés, recherche en ligne.
/// </summary>
public sealed partial class ProcessesViewModel : ViewModelBase
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly IProcessService _processes;
    private readonly IShellService _shell;
    private readonly IAppLifecycle? _lifecycle;
    private readonly Dictionary<int, ProcessItemViewModel> _byId = [];
    private CancellationTokenSource? _refreshCts;
    private int _actionDepth;

    public ProcessesViewModel(ViewModelContext context, IProcessService processes, IShellService shell, IAppLifecycle? lifecycle = null)
        : base(context)
    {
        _lifecycle = lifecycle;
        _processes = processes;
        _shell = shell;
    }

    /// <summary>Processus filtrés et triés.</summary>
    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProcessSortColumn SortColumn { get; private set; } = ProcessSortColumn.Cpu;

    [ObservableProperty]
    public partial bool SortDescending { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CloseApplicationCommand), nameof(TerminateCommand), nameof(OpenLocationCommand), nameof(ShowPropertiesCommand), nameof(SearchOnlineCommand))]
    public partial ProcessItemViewModel? SelectedProcess { get; set; }

    public bool HasSelection => SelectedProcess is not null;

    [ObservableProperty]
    public partial bool IsLoaded { get; private set; }

    /// <summary>« 182 processus ».</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Le rafraîchissement automatique est suspendu (action en cours).</summary>
    [ObservableProperty]
    public partial bool IsActionInProgress { get; private set; }

    /// <summary>Glyphe de tri de la colonne active (flèche haut / bas).</summary>
    public string SortGlyph => SortDescending ? Glyphs.ArrowDown : Glyphs.ArrowUp;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        await RefreshCoreAsync(cancellationToken).ConfigureAwait(true);
        StartAutoRefresh();
    }

    protected override void OnDeactivated() => StopAutoRefresh();

    partial void OnSearchTextChanged(string value) => ApplyView();

    partial void OnSortColumnChanged(ProcessSortColumn value) => OnPropertyChanged(nameof(SortGlyph));

    partial void OnSortDescendingChanged(bool value) => OnPropertyChanged(nameof(SortGlyph));

    /// <summary>Trie par colonne (paramètre : nom de <see cref="ProcessSortColumn"/>) ; un second clic inverse l'ordre.</summary>
    [RelayCommand]
    private void Sort(string? column)
    {
        if (!Enum.TryParse<ProcessSortColumn>(column, true, out var c)) return;
        if (c == SortColumn) SortDescending = !SortDescending;
        else
        {
            SortColumn = c;
            SortDescending = c is ProcessSortColumn.Cpu or ProcessSortColumn.Memory or ProcessSortColumn.Disk;
        }

        ApplyView();
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => RefreshCoreAsync(cancellationToken);

    /// <summary>Fermeture propre (fenêtre principale) : l'application peut demander d'enregistrer.</summary>
    [RelayCommand(CanExecute = nameof(CanClose))]
    private async Task CloseApplicationAsync()
    {
        if (SelectedProcess is not { } p) return;
        await RunActionAsync(() =>
        {
            var result = _processes.CloseApplication(p.ProcessId);
            if (CheckResult(result)) StatusMessage = T("Processes_Closed", p.Name);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    private bool CanClose() => SelectedProcess is { IsCritical: false, HasWindow: true };

    /// <summary>Terminaison explicite : confirmation ; refusée et expliquée pour les processus critiques.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task TerminateAsync()
    {
        if (SelectedProcess is not { } p) return;
        if (p.IsCritical)
        {
            await Dialogs.ShowMessageAsync(TextRef.Of("Processes_Critical_Title"), TextRef.Of("Processes_Critical_Message", p.Name)).ConfigureAwait(true);
            return;
        }

        await RunActionAsync(async () =>
        {
            var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
                TextRef.Of("Processes_Terminate_ConfirmTitle"),
                TextRef.Of(p.IsSensitive ? "Processes_Terminate_ConfirmSensitive" : "Processes_Terminate_ConfirmMessage", p.Name),
                TextRef.Of("Processes_Action_Terminate"),
                CloseButton: TextRef.Of("Common_Action_Cancel"),
                IsDestructive: true)).ConfigureAwait(true);
            if (answer != DialogResultKind.Primary) return;
            var result = _processes.TerminateProcess(p.ProcessId);
            if (CheckResult(result)) StatusMessage = T("Processes_Terminated", p.Name);
        }).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPath))]
    private void OpenLocation()
    {
        if (SelectedProcess?.Model.ExecutablePath is { } path) CheckResult(_shell.RevealInExplorer(path));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedPath))]
    private void ShowProperties()
    {
        if (SelectedProcess?.Model.ExecutablePath is { } path) CheckResult(_shell.ShowFileProperties(path));
    }

    /// <summary>Recherche web explicite sur le nom du processus.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SearchOnline()
    {
        if (SelectedProcess is { } p) CheckResult(_shell.SearchOnline(p.Name));
    }

    private bool HasSelectedPath() => SelectedProcess?.HasPath == true;

    private async Task RunActionAsync(Func<Task> action)
    {
        _actionDepth++;
        IsActionInProgress = true;
        ErrorText = null;
        try
        {
            await RunSafeAsync(_ => action()).ConfigureAwait(true);
        }
        finally
        {
            _actionDepth--;
            IsActionInProgress = _actionDepth > 0;
        }

        await RefreshCoreAsync(PageToken).ConfigureAwait(true);
    }

    private void StartAutoRefresh()
    {
        StopAutoRefresh();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(PageToken);
        _refreshCts = cts;
        _ = AutoRefreshLoopAsync(cts.Token);
    }

    private void StopAutoRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
    }

    private async Task AutoRefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Pas de rafraîchissement pendant une action ni quand la fenêtre est masquée.
                if (_actionDepth > 0 || _lifecycle?.IsWindowVisible == false) continue;
                await Context.Dispatcher.InvokeAsync(() => RefreshCoreAsync(cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Page quittée.
        }
    }

    private Task RefreshCoreAsync(CancellationToken cancellationToken) => RunSafeAsync(async ct =>
    {
        var list = await _processes.GetProcessesAsync(ct).ConfigureAwait(true);
        if (ct.IsCancellationRequested) return;
        var seen = new HashSet<int>();
        foreach (var info in list)
        {
            seen.Add(info.ProcessId);
            if (_byId.TryGetValue(info.ProcessId, out var item)) item.Update(info);
            else _byId[info.ProcessId] = new ProcessItemViewModel(info, Localizer, Formatter);
        }

        foreach (var gone in _byId.Keys.Where(id => !seen.Contains(id)).ToList()) _byId.Remove(gone);
        if (SelectedProcess is { } selected && !_byId.ContainsKey(selected.ProcessId)) SelectedProcess = null;
        CountText = T("Processes_Count", _byId.Count);
        IsLoaded = true;
        ApplyView();
        SelectedProcessRefreshed();
    }, cancellationToken, trackBusy: false);

    private void SelectedProcessRefreshed()
    {
        CloseApplicationCommand.NotifyCanExecuteChanged();
        OpenLocationCommand.NotifyCanExecuteChanged();
        ShowPropertiesCommand.NotifyCanExecuteChanged();
    }

    private void ApplyView()
    {
        var search = SearchText?.Trim() ?? string.Empty;
        IEnumerable<ProcessItemViewModel> query = _byId.Values;
        if (search.Length > 0)
        {
            query = query.Where(p => p.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || p.PublisherText.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                || p.ProcessIdText.Contains(search, StringComparison.Ordinal));
        }

        var comparer = StringComparer.CurrentCultureIgnoreCase;
        IOrderedEnumerable<ProcessItemViewModel> ordered = (SortColumn, SortDescending) switch
        {
            (ProcessSortColumn.Name, false) => query.OrderBy(p => p.Name, comparer),
            (ProcessSortColumn.Name, true) => query.OrderByDescending(p => p.Name, comparer),
            (ProcessSortColumn.ProcessId, false) => query.OrderBy(p => p.ProcessId),
            (ProcessSortColumn.ProcessId, true) => query.OrderByDescending(p => p.ProcessId),
            (ProcessSortColumn.Cpu, false) => query.OrderBy(p => p.CpuPercent),
            (ProcessSortColumn.Cpu, true) => query.OrderByDescending(p => p.CpuPercent),
            (ProcessSortColumn.Memory, false) => query.OrderBy(p => p.MemoryBytes),
            (ProcessSortColumn.Memory, true) => query.OrderByDescending(p => p.MemoryBytes),
            (ProcessSortColumn.Disk, false) => query.OrderBy(p => p.DiskBytesPerSec),
            (ProcessSortColumn.Disk, true) => query.OrderByDescending(p => p.DiskBytesPerSec),
            (ProcessSortColumn.Publisher, false) => query.OrderBy(p => p.PublisherText, comparer),
            (ProcessSortColumn.Publisher, true) => query.OrderByDescending(p => p.PublisherText, comparer),
            (ProcessSortColumn.Trust, false) => query.OrderBy(p => p.TrustSortKey),
            _ => query.OrderByDescending(p => p.TrustSortKey),
        };

        CollectionSync.SyncTo(Items, ordered.ThenBy(p => p.ProcessId).ToList());
    }
}
