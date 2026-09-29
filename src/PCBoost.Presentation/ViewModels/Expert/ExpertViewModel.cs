using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;

namespace PCBoost.Presentation.ViewModels;

/// <summary>Modification brute (mode Expert) : type, cible, états avant / après JSON, erreur.</summary>
public sealed class ExpertChangeItemViewModel
{
    public ExpertChangeItemViewModel(ChangeRecord change, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Id = change.Id.ToString();
        Sequence = change.Sequence;
        Kind = change.Kind;
        OptimizationId = change.OptimizationId;
        Target = change.Target;
        Description = localizer.Format(change.Description);
        Status = change.Status.ToString();
        StatusText = localizer.Get($"History_ChangeStatus_{change.Status}");
        BeforeState = change.BeforeState ?? string.Empty;
        AfterState = change.AfterState ?? string.Empty;
        ErrorDetail = change.ErrorDetail ?? string.Empty;
        RecordedAtText = formatter.DateTimeFull(change.RecordedAt);
        RolledBackAtText = change.RolledBackAt is { } at ? formatter.DateTimeFull(at) : string.Empty;
        Reversible = change.Reversible;
    }

    public string Id { get; }

    public int Sequence { get; }

    public string Kind { get; }

    public string OptimizationId { get; }

    public string Target { get; }

    public string Description { get; }

    /// <summary>Valeur brute de l'état (« Applied », « RolledBack »…).</summary>
    public string Status { get; }

    public string StatusText { get; }

    public string BeforeState { get; }

    public string AfterState { get; }

    public bool HasBeforeState => BeforeState.Length > 0;

    public bool HasAfterState => AfterState.Length > 0;

    public string ErrorDetail { get; }

    public bool HasError => ErrorDetail.Length > 0;

    public string RecordedAtText { get; }

    public string RolledBackAtText { get; }

    public bool Reversible { get; }
}

/// <summary>Session brute (mode Expert).</summary>
public sealed partial class ExpertSessionItemViewModel : ObservableObject
{
    public ExpertSessionItemViewModel(OptimizationSession session, ILocalizer localizer, IValueFormatter formatter)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(formatter);
        Id = session.Id.ToString();
        Header = localizer.Format("Expert_Session_Header",
            formatter.DateTimeFull(session.StartedAt),
            localizer.Get($"History_Type_{session.Type}"),
            localizer.Get($"History_Status_{session.Status}"),
            session.Changes.Count);
        Title = session.Title is { } t ? localizer.Format(t) : localizer.Get($"History_Type_{session.Type}");
        ProfileId = session.ProfileId ?? string.Empty;
        CompletedAtText = session.CompletedAt is { } c ? formatter.DateTimeFull(c) : string.Empty;
        Changes = new ObservableCollection<ExpertChangeItemViewModel>(session.Changes.OrderBy(c => c.Sequence).Select(c => new ExpertChangeItemViewModel(c, localizer, formatter)));
    }

    public string Id { get; }

    /// <summary>« 28/09/2026 14:05 · OneClick · Completed · 6 modifications ».</summary>
    public string Header { get; }

    public string Title { get; }

    public string ProfileId { get; }

    public string CompletedAtText { get; }

    public ObservableCollection<ExpertChangeItemViewModel> Changes { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }
}

/// <summary>
/// Mode Expert (§64) : sessions avec cibles brutes et états avant / après JSON, dernières lignes du journal
/// de l'application (<see cref="IAppInfo.LogDirectory"/>), erreurs techniques consignées.
/// </summary>
public sealed partial class ExpertViewModel : ViewModelBase
{
    public const int LogLineCount = 200;
    private const int MaxLogBytes = 256 * 1024;
    private readonly IRollbackManager _rollback;
    private readonly IActivityJournal _journal;
    private readonly IAppInfo _appInfo;
    private readonly IShellService _shell;

    public ExpertViewModel(ViewModelContext context, IRollbackManager rollback, IActivityJournal journal, IAppInfo appInfo, IShellService shell)
        : base(context)
    {
        _rollback = rollback;
        _journal = journal;
        _appInfo = appInfo;
        _shell = shell;
        LogDirectory = appInfo.LogDirectory;
    }

    public ObservableCollection<ExpertSessionItemViewModel> Sessions { get; } = [];

    /// <summary>Erreurs techniques (modifications en échec, entrées de journal Error/Warning avec détail).</summary>
    public ObservableCollection<InfoRowViewModel> TechnicalErrors { get; } = [];

    [ObservableProperty]
    public partial bool HasTechnicalErrors { get; private set; }

    /// <summary>Dernières lignes du fichier journal le plus récent (texte brut, police à chasse fixe conseillée).</summary>
    [ObservableProperty]
    public partial string LogTail { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string LogFileName { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasLog { get; private set; }

    public string LogDirectory { get; }

    /// <summary>0 = Sessions, 1 = Journal de l'application, 2 = Erreurs techniques.</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => RefreshAsync(cancellationToken);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await RunSafeAsync(async ct =>
        {
            var sessions = await _rollback.GetHistoryAsync(100, ct).ConfigureAwait(true);
            CollectionSync.Replace(Sessions, sessions.OrderByDescending(s => s.StartedAt).Select(s => new ExpertSessionItemViewModel(s, Localizer, Formatter)));

            var entries = await _journal.GetRecentAsync(200, ct).ConfigureAwait(true);
            var errors = sessions
                .SelectMany(s => s.Changes.Where(c => !string.IsNullOrEmpty(c.ErrorDetail)).Select(c => (c.RecordedAt, Row: new InfoRowViewModel(
                    $"{Formatter.DateTimeFull(c.RecordedAt)} · {c.Kind} · {T($"History_ChangeStatus_{c.Status}")}", c.ErrorDetail!, c.Target))))
                .Concat(entries
                    .Where(e => e.Kind is ActivityKind.Error or ActivityKind.Warning && !string.IsNullOrEmpty(e.Detail))
                    .Select(e => (RecordedAt: e.Timestamp, Row: new InfoRowViewModel(
                        $"{Formatter.DateTimeFull(e.Timestamp)} · {e.Kind}", e.Detail!, Localizer.Format(e.Message)))))
                .OrderByDescending(x => x.RecordedAt)
                .Select(x => x.Row);
            CollectionSync.Replace(TechnicalErrors, errors);
            HasTechnicalErrors = TechnicalErrors.Count > 0;

            var (fileName, tail) = await Task.Run(() => ReadLogTail(_appInfo.LogDirectory, LogLineCount), ct).ConfigureAwait(true);
            LogFileName = fileName ?? string.Empty;
            HasLog = tail is not null;
            LogTail = tail ?? T("Expert_Log_None");
        }, cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLogFolder() => CheckResult(_shell.OpenFolder(_appInfo.LogDirectory));

    /// <summary>Lit les dernières lignes du fichier journal le plus récent (partage en lecture/écriture : le journal reste ouvert par l'application).</summary>
    internal static (string? FileName, string? Tail) ReadLogTail(string? directory, int lineCount)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return (null, null);
        FileInfo? latest;
        try
        {
            latest = new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => f.Extension is ".log" or ".txt" or ".json" or ".clef")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return (null, null);
        }

        if (latest is null) return (null, null);
        try
        {
            using var stream = new FileStream(latest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - MaxLogBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n');
            // Première ligne potentiellement tronquée si la lecture commence au milieu du fichier.
            var usable = start > 0 ? lines.Skip(1) : lines;
            var tail = usable.Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(lineCount);
            return (latest.Name, string.Join(Environment.NewLine, tail));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (latest.Name, null);
        }
    }
}
