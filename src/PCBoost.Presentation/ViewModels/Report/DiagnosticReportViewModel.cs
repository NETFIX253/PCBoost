using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Reports;
using PCBoost.Core.Privacy;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Reporting;

namespace PCBoost.Presentation.ViewModels;

/// <summary>
/// Rapport de diagnostic : l'utilisateur choisit le contenu (nom du PC exclu par défaut, journaux anonymisés inclus),
/// voit l'aperçu exact du document, puis l'enregistre en PDF ou en HTML pour le joindre à un ticket. Tout est produit
/// localement, sans connexion réseau ; aucune donnée n'est envoyée.
/// </summary>
public sealed partial class DiagnosticReportViewModel : ViewModelBase
{
    public const int MaxLogLines = 200;
    private const int HistorySessions = DiagnosticReportHtml.MaxSessions;
    private const int JournalEntries = DiagnosticReportHtml.MaxJournalEntries;

    private readonly ISystemAnalyzer _analyzer;
    private readonly IPerformanceScoreCalculator _score;
    private readonly IHealthRulesEngine _rules;
    private readonly ISettingsService _settings;
    private readonly IHardwareHealthService _health;
    private readonly IBootTimeService _boot;
    private readonly IOptimizationHistoryRepository _history;
    private readonly IActivityJournal _journal;
    private readonly IDiagnosticLogSource _logs;
    private readonly IAppInfo _app;
    private readonly IReportFileService _files;
    private readonly IShellService _shell;
    private DiagnosticReportData? _data;
    private bool _suppressRebuild;

    public DiagnosticReportViewModel(
        ViewModelContext context,
        ISystemAnalyzer analyzer,
        IPerformanceScoreCalculator score,
        IHealthRulesEngine rules,
        ISettingsService settings,
        IHardwareHealthService health,
        IBootTimeService boot,
        IOptimizationHistoryRepository history,
        IActivityJournal journal,
        IDiagnosticLogSource logs,
        IAppInfo app,
        IReportFileService files,
        IShellService shell)
        : base(context)
    {
        _analyzer = analyzer;
        _score = score;
        _rules = rules;
        _settings = settings;
        _health = health;
        _boot = boot;
        _history = history;
        _journal = journal;
        _logs = logs;
        _app = app;
        _files = files;
        _shell = shell;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePdfCommand), nameof(SaveHtmlCommand), nameof(RefreshCommand))]
    public partial bool IsPreparing { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SavePdfCommand), nameof(SaveHtmlCommand))]
    public partial bool IsSaving { get; private set; }

    /// <summary>Document HTML autonome affiché dans l'aperçu et enregistré tel quel.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(SavePdfCommand), nameof(SaveHtmlCommand))]
    public partial string PreviewHtml { get; private set; } = string.Empty;

    public bool HasPreview => !string.IsNullOrEmpty(PreviewHtml);

    [ObservableProperty]
    public partial bool IncludeComputerName { get; set; }

    [ObservableProperty]
    public partial bool IncludeLogs { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeHistory { get; set; } = true;

    public bool CanExportPdf => _files.CanExportPdf;

    /// <summary>Chemin du dernier fichier enregistré (bouton « Afficher dans l'Explorateur »).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSavedFile))]
    public partial string? SavedPath { get; private set; }

    public bool HasSavedFile => !string.IsNullOrEmpty(SavedPath);

    public DiagnosticReportOptions Options => new(IncludeComputerName, IncludeLogs, IncludeHistory);

    protected override Task OnActivatedAsync(object? parameter, CancellationToken cancellationToken) => PrepareAsync(cancellationToken);

    partial void OnIncludeComputerNameChanged(bool value) => Rebuild();

    partial void OnIncludeLogsChanged(bool value) => Rebuild();

    partial void OnIncludeHistoryChanged(bool value) => Rebuild();

    /// <summary>Rassemble les données (analyse de la session ou nouvelle analyse, santé, démarrage, historique, journaux).</summary>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync(CancellationToken cancellationToken) => PrepareAsync(cancellationToken, forceAnalysis: true);

    private bool CanRefresh() => !IsPreparing;

    internal async Task PrepareAsync(CancellationToken cancellationToken, bool forceAnalysis = false)
    {
        IsPreparing = true;
        SavedPath = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var analysis = forceAnalysis ? null : _analyzer.LastReport;
                analysis ??= await _analyzer.AnalyzeAsync(new AnalysisOptions(), null, ct).ConfigureAwait(true);
                var thresholds = _settings.Current.Thresholds;

                var health = analysis.Health ?? _health.Latest;
                if (health is null || Context.Clock.UtcNow - health.Timestamp > TimeSpan.FromMinutes(10))
                    health = await _health.RefreshAsync(ct).ConfigureAwait(true);
                var report = analysis with { Health = health };

                var boot = await Try(() => _boot.GetReportAsync(ct)).ConfigureAwait(true);
                var sessions = await Try(() => _history.GetRecentSessionsAsync(HistorySessions, ct)).ConfigureAwait(true) ?? [];
                var journal = await Try(() => _journal.GetRecentAsync(JournalEntries, ct)).ConfigureAwait(true) ?? [];
                var logs = await Try(() => _logs.ReadRecentLinesAsync(MaxLogLines, ct)).ConfigureAwait(true) ?? [];

                _data = new DiagnosticReportData(
                    Context.Clock.UtcNow,
                    _app.ProductName,
                    _app.Version,
                    MachineName(),
                    report,
                    _score.Calculate(report, thresholds),
                    _rules.Evaluate(report, thresholds),
                    health,
                    boot,
                    sessions,
                    journal,
                    logs);
                Rebuild();
            }, cancellationToken, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsPreparing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSavePdf))]
    private Task SavePdfAsync() => SaveAsync(ReportFileFormat.Pdf);

    private bool CanSavePdf() => CanSave() && _files.CanExportPdf;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private Task SaveHtmlAsync() => SaveAsync(ReportFileFormat.Html);

    private bool CanSave() => HasPreview && !IsPreparing && !IsSaving;

    [RelayCommand]
    private void RevealSavedFile()
    {
        if (SavedPath is { } path) CheckResult(_shell.RevealInExplorer(path));
    }

    private async Task SaveAsync(ReportFileFormat format)
    {
        if (_data is null || !HasPreview) return;
        IsSaving = true;
        StatusMessage = null;
        try
        {
            await RunSafeAsync(async ct =>
            {
                var path = await _files.PickSavePathAsync(DiagnosticReportHtml.SuggestedFileName(_data.GeneratedAt, _data.ProductName), format).ConfigureAwait(true);
                if (path is null) return;
                var result = await _files.SaveAsync(path, PreviewHtml, format, ct).ConfigureAwait(true);
                if (!CheckResult(result)) return;
                SavedPath = path;
                StatusMessage = T("Report_Saved", StartupViewModel.FileNameOf(path));
            }, linkToPage: false, trackBusy: false).ConfigureAwait(true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Rebuild()
    {
        if (_suppressRebuild || _data is null) return;
        var redactor = new SensitiveDataRedactor(ProfilePath(), UserName(), IncludeComputerName ? null : _data.ComputerName);
        PreviewHtml = new DiagnosticReportHtml(Localizer, Formatter, redactor).Build(_data, Options);
        SavedPath = null;
    }

    /// <summary>Remplace les options sans reconstruire à chaque changement (tests, restauration d'état).</summary>
    internal void SetOptions(DiagnosticReportOptions options)
    {
        _suppressRebuild = true;
        try
        {
            IncludeComputerName = options.IncludeComputerName;
            IncludeLogs = options.IncludeLogs;
            IncludeHistory = options.IncludeHistory;
        }
        finally
        {
            _suppressRebuild = false;
        }
        Rebuild();
    }

    private async Task<T?> Try<T>(Func<Task<T>> read)
        where T : class
    {
        try
        {
            return await read().ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            Logger.LogDebug(ex, "Rapport : une source de données est indisponible");
            return null;
        }
    }

    internal Func<string?> MachineName { get; set; } = () => SafeEnvironment(() => Environment.MachineName);

    internal Func<string?> UserName { get; set; } = () => SafeEnvironment(() => Environment.UserName);

    internal Func<string?> ProfilePath { get; set; } = () => SafeEnvironment(() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify));

    private static string? SafeEnvironment(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }
}
