using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Modification consignée dans une session (description, cible, statut, réversibilité, annulation individuelle).</summary>
public sealed partial class ChangeItemViewModel : ObservableObject
{
    private readonly Func<ChangeItemViewModel, Task>? _undo;

    public ChangeItemViewModel(ChangeRecord change, ILocalizer localizer, IValueFormatter formatter, Func<ChangeItemViewModel, Task>? undo)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = change;
        _undo = undo;
        Description = localizer.Format(change.Description);
        Target = change.Target;
        StatusText = localizer.Get($"History_ChangeStatus_{change.Status}");
        StatusGlyph = change.Status switch
        {
            ChangeStatus.Applied => Glyphs.Success,
            ChangeStatus.RolledBack => Glyphs.Restore,
            ChangeStatus.Failed or ChangeStatus.RollbackFailed => Glyphs.Error,
            ChangeStatus.Irreversible => Glyphs.Info,
            _ => Glyphs.Pending,
        };
        ReversibleText = localizer.Reversibility(change.Reversible);
        TimeText = formatter.DateTimeFull(change.RecordedAt);
        UndoLabel = localizer.Get("History_Change_Undo");
    }

    public ChangeRecord Model { get; }

    public string Description { get; }

    /// <summary>Cible lisible (« HKCU\…\StartupApproved\Run : Discord »).</summary>
    public string Target { get; }

    public string StatusText { get; }

    public string StatusGlyph { get; }

    public string ReversibleText { get; }

    public bool IsReversible => Model.Reversible;

    public string TimeText { get; }

    public string UndoLabel { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    public partial bool IsUndoing { get; private set; }

    /// <summary>Modification appliquée et réversible : peut être annulée seule.</summary>
    public bool CanUndo => Model.Reversible && Model.Status == ChangeStatus.Applied && _undo is not null && !IsUndoing;

    public string AccessibleName => $"{Description}, {StatusText}, {ReversibleText}";

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (_undo is null) return;
        IsUndoing = true;
        try
        {
            await _undo(this).ConfigureAwait(true);
        }
        finally
        {
            IsUndoing = false;
        }
    }
}

/// <summary>Session de modifications (date, type, titre, nombre, statut, espace libéré, restauration, détail).</summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Func<SessionItemViewModel, Task>? _restore;

    public SessionItemViewModel(OptimizationSession session, bool canRollback, ILocalizer localizer, IValueFormatter formatter,
        Func<SessionItemViewModel, Task>? restore, Func<ChangeItemViewModel, Task>? undo)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Model = session;
        _restore = restore;
        Id = session.Id;
        DateText = formatter.DateTimeFull(session.StartedAt);
        RelativeDateText = formatter.DateTime(session.StartedAt);
        TypeText = localizer.Get($"History_Type_{session.Type}");
        Title = session.Title is { } t ? localizer.Format(t) : TypeText;
        ChangeCountText = session.Changes.Count == 1
            ? localizer.Get("History_OneChange")
            : localizer.Format("History_Changes", session.Changes.Count);
        StatusText = localizer.Get($"History_Status_{session.Status}");
        StatusGlyph = session.Status switch
        {
            SessionStatus.Completed => Glyphs.Success,
            SessionStatus.PartiallyCompleted or SessionStatus.PartiallyRolledBack => Glyphs.Warning,
            SessionStatus.Failed or SessionStatus.Interrupted => Glyphs.Error,
            SessionStatus.RolledBack => Glyphs.Restore,
            SessionStatus.InProgress => Glyphs.Running,
            _ => Glyphs.Info,
        };
        BytesFreedText = session.BytesFreed > 0 ? localizer.Format("History_BytesFreed", formatter.Bytes(session.BytesFreed)) : string.Empty;
        RestartText = session.RequiresRestart ? localizer.Get("History_RequiresRestart") : string.Empty;
        CanRestore = canRollback && restore is not null;
        RestoreLabel = localizer.Get("Common_Action_Restore");
        Changes = new ObservableCollection<ChangeItemViewModel>(session.Changes.OrderBy(c => c.Sequence).Select(c => new ChangeItemViewModel(c, localizer, formatter, undo)));
    }

    public OptimizationSession Model { get; }

    public Guid Id { get; }

    public string DateText { get; }

    public string RelativeDateText { get; }

    public string TypeText { get; }

    public string Title { get; }

    /// <summary>« 5 modifications ».</summary>
    public string ChangeCountText { get; }

    public string StatusText { get; }

    public string StatusGlyph { get; }

    /// <summary>« 1,2 Go libérés » ; vide si aucun.</summary>
    public string BytesFreedText { get; }

    public bool HasBytesFreed => !string.IsNullOrEmpty(BytesFreedText);

    public string RestartText { get; }

    public bool RequiresRestart => Model.RequiresRestart;

    public ObservableCollection<ChangeItemViewModel> Changes { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    public partial bool IsRestoring { get; private set; }

    public bool CanRestore { get; }

    public string RestoreLabel { get; }

    public string AccessibleName => $"{Title}, {RelativeDateText}, {StatusText}, {ChangeCountText}";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand(CanExecute = nameof(CanRunRestore))]
    private async Task RestoreAsync()
    {
        if (_restore is null) return;
        IsRestoring = true;
        try
        {
            await _restore(this).ConfigureAwait(true);
        }
        finally
        {
            IsRestoring = false;
        }
    }

    private bool CanRunRestore() => CanRestore && !IsRestoring;
}

/// <summary>
/// Historique (§10, §78) : sessions avec restauration si possible et détail des modifications ; onglet Journal des
/// modifications (<see cref="Journal"/>). Paramètre de navigation <see cref="PageKeys.Journal"/> : ouvre l'onglet Journal.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly IRollbackManager _rollback;

    public HistoryViewModel(ViewModelContext context, IRollbackManager rollback, JournalViewModel journal)
        : base(context)
    {
        _rollback = rollback;
        Journal = journal;
    }

    /// <summary>Onglet « Journal des modifications ».</summary>
    public JournalViewModel Journal { get; }

    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    /// <summary>0 = Sessions, 1 = Journal.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmpty))]
    public partial bool IsLoaded { get; private set; }

    public bool ShowEmpty => IsLoaded && Sessions.Count == 0;

    protected override async Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken)
    {
        if (string.Equals(parameter as string, PageKeys.Journal, StringComparison.OrdinalIgnoreCase)) SelectedTabIndex = 1;
        _rollback.SessionChanged += OnSessionChanged;
        await Journal.OnNavigatedToAsync(null).ConfigureAwait(true);
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    protected override void OnDeactivated()
    {
        _rollback.SessionChanged -= OnSessionChanged;
        Journal.OnNavigatedFrom();
    }

    [RelayCommand]
    private Task RefreshAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    private Task LoadAsync(CancellationToken cancellationToken) => RunSafeAsync(async ct =>
    {
        var sessions = await _rollback.GetHistoryAsync(100, ct).ConfigureAwait(true);
        var expanded = Sessions.Where(s => s.IsExpanded).Select(s => s.Id).ToHashSet();
        CollectionSync.Replace(Sessions, sessions.OrderByDescending(s => s.StartedAt).Select(s => CreateItem(s, expanded.Contains(s.Id))));
        IsLoaded = true;
        OnPropertyChanged(nameof(ShowEmpty));
    }, cancellationToken);

    private SessionItemViewModel CreateItem(OptimizationSession session, bool expanded)
        => new(session, _rollback.CanRollback(session), Localizer, Formatter, RestoreSessionAsync, UndoChangeAsync) { IsExpanded = expanded };

    private void OnSessionChanged(object? sender, Guid sessionId) => OnUi(() =>
    {
        if (IsActive) _ = ReloadSessionAsync(sessionId);
    });

    private Task ReloadSessionAsync(Guid sessionId) => RunSafeAsync(async ct =>
    {
        var session = await _rollback.GetSessionAsync(sessionId, ct).ConfigureAwait(true);
        var index = Sessions.ToList().FindIndex(s => s.Id == sessionId);
        if (session is null)
        {
            if (index >= 0) Sessions.RemoveAt(index);
            return;
        }

        var item = CreateItem(session, index >= 0 && Sessions[index].IsExpanded);
        if (index >= 0) Sessions[index] = item;
        else Sessions.Insert(0, item);
        OnPropertyChanged(nameof(ShowEmpty));
    }, trackBusy: false);

    private async Task RestoreSessionAsync(SessionItemViewModel item)
    {
        var answer = await Dialogs.ConfirmAsync(new ConfirmationRequest(
            TextRef.Of("History_Restore_ConfirmTitle"),
            TextRef.Of("History_Restore_ConfirmMessage", item.Title, item.DateText),
            TextRef.Of("Common_Action_Restore"),
            CloseButton: TextRef.Of("Common_Action_Cancel"))).ConfigureAwait(true);
        if (answer != DialogResultKind.Primary) return;

        ErrorText = null;
        await RunSafeAsync(async ct =>
        {
            var result = await _rollback.RestoreSessionAsync(item.Id, ct).ConfigureAwait(true);
            StatusMessage = RestoreText.Describe(Localizer, result);
            if (!result.Success && result.Errors.FirstOrDefault(e => !e.Success) is { } err) CheckResult(err);
            await ReloadSessionAsync(item.Id).ConfigureAwait(true);
        }, linkToPage: false).ConfigureAwait(true);
    }

    private async Task UndoChangeAsync(ChangeItemViewModel change)
    {
        ErrorText = null;
        await RunSafeAsync(async ct =>
        {
            var result = await _rollback.UndoChangeAsync(change.Model.Id, ct).ConfigureAwait(true);
            if (CheckResult(result)) StatusMessage = T("History_Change_Undone", change.Description);
            await ReloadSessionAsync(change.Model.SessionId).ConfigureAwait(true);
        }, linkToPage: false).ConfigureAwait(true);
    }
}
