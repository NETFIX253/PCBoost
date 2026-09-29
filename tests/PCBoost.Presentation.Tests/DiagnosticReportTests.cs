using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Reports;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Privacy;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Reporting;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class DiagnosticReportHtmlTests
{
    private readonly TestUi _ui = new();

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    private DiagnosticReportData Data(IReadOnlyList<string>? logs = null, string computer = "BUREAU-AMIN")
    {
        var analysis = Reports.Create(Now) with
        {
            TopMemoryProcesses = [new ProcessUsage(1, "chrome", @"C:\Users\amin\AppData\Local\chrome.exe", 1, 512L * ByteSize.MiB, true)],
        };
        var disk = new DiskHealthInfo("0", "Samsung SSD 870 <script>", StorageMediaType.Ssd, StorageBusType.Sata, 500_107_862_016, DiskHealthStatus.Healthy, true,
            new DiskReliability("0", 12, 38, 55, 4200, 0, 0, 0, Now));
        var health = new HardwareHealthReport(Now, [disk], Availability.Available, Now, [], Availability.Available,
            [new DeviceProblem("Contrôleur & co", "Net", 28, null)], Availability.Available, new ThermalLimitInfo(0, null, 0, null));
        var session = new OptimizationSession
        {
            Id = Guid.NewGuid(), Type = SessionType.OldPcAssistant, StartedAt = Now.AddHours(-2), Status = SessionStatus.Completed,
            Changes =
            [
                new ChangeRecord
                {
                    Id = Guid.NewGuid(), SessionId = Guid.Empty, OptimizationId = "visual", Kind = "registry.value", Target = "HKCU",
                    Description = TextRef.Literal(@"Effets visuels réduits pour C:\Users\amin"), Reversible = true, Status = ChangeStatus.Applied, RecordedAt = Now,
                },
            ],
        };
        return new DiagnosticReportData(Now, "PCBoost", new Version(1, 1, 0, 0), computer, analysis,
            new PerformanceScore(72, [], Now),
            [new HealthFinding("r", Severity.High, TextRef.Literal("Mémoire saturée"), TextRef.Literal("92 % utilisés"), 92, 85, "memory")],
            health,
            new BootTimeReport([], new BootPerformanceData(Now, [new BootRecord(Now.AddDays(-1), TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(12), 9)], []), null, null),
            [session],
            [new ActivityLogEntry(Guid.NewGuid(), Now, ActivityKind.Optimization, TextRef.Literal("Optimisation appliquée"), null)],
            logs ?? ["2026-09-29 [INFO] Démarrage de PCBoost sur BUREAU-AMIN", @"2026-09-29 [DEBUG] Lecture de C:\Users\amin\Documents"]);
    }

    private static SensitiveDataRedactor Redactor(bool hideComputer = true)
        => new(@"C:\Users\amin", "amin", hideComputer ? "BUREAU-AMIN" : null);

    [Fact]
    public void Report_is_self_contained_and_escapes_text()
    {
        var html = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor()).Build(Data(), new DiagnosticReportOptions());

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("default-src 'none'", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.Contains("Samsung SSD 870 &lt;script&gt;", html);
        Assert.Contains("Contrôleur &amp; co", html);
        Assert.Contains("Rapport de diagnostic PCBoost", html);
        Assert.Contains("Mémoire saturée", html);
        Assert.Contains("Pilote non installé (code 28)", html);
        Assert.Contains("12 %", html);
    }

    [Fact]
    public void Personal_data_is_masked_and_computer_name_hidden_by_default()
    {
        var html = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor()).Build(Data(), new DiagnosticReportOptions());

        Assert.DoesNotContain("BUREAU-AMIN", html);
        Assert.DoesNotContain(@"C:\Users\amin", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chrome.exe", html);
        Assert.Contains("(masqué)", html);
        Assert.Contains("%USERPROFILE%", html);
        Assert.Contains("Démarrage de PCBoost sur &lt;machine&gt;", html);
    }

    [Fact]
    public void Computer_name_is_shown_only_when_chosen()
    {
        var html = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor(hideComputer: false))
            .Build(Data(), new DiagnosticReportOptions(IncludeComputerName: true));
        Assert.Contains("BUREAU-AMIN", html);
        Assert.DoesNotContain(@"C:\Users\amin", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Logs_and_history_can_be_excluded()
    {
        var builder = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor());
        var full = builder.Build(Data(), new DiagnosticReportOptions());
        Assert.Contains("Journaux techniques (anonymisés)", full);
        Assert.Contains("Dernières modifications", full);

        var minimal = builder.Build(Data(), new DiagnosticReportOptions(IncludeLogs: false, IncludeHistory: false));
        Assert.DoesNotContain("Journaux techniques (anonymisés)", minimal);
        Assert.DoesNotContain("Dernières modifications", minimal);
        Assert.DoesNotContain("Optimisation appliquée", minimal);
        Assert.Contains("Constats", minimal);
    }

    [Fact]
    public void Missing_sources_are_stated_not_invented()
    {
        var data = Data() with { Analysis = null, Score = null, Health = null, Boot = null, Sessions = [], Journal = [], LogLines = [] };
        var html = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor()).Build(data, new DiagnosticReportOptions());
        Assert.Contains("Analyse non disponible", html);
        Assert.Contains("Santé du matériel non relevée.", html);
        Assert.Contains("Aucune modification enregistrée par PCBoost.", html);
        Assert.Contains("Aucune ligne de journal disponible.", html);
    }

    [Fact]
    public void Identical_changes_are_grouped()
    {
        var data = Data();
        var session = data.Sessions[0];
        var change = session.Changes[0];
        var many = Enumerable.Range(0, 5).Select(i => change with { Id = Guid.NewGuid(), Description = TextRef.Literal("Mode efficacité pour chrome"), Sequence = i }).ToList();
        var html = new DiagnosticReportHtml(_ui.Localizer, _ui.Formatter, Redactor()).Build(data with { Sessions = [session with { Changes = many }] }, new DiagnosticReportOptions());
        Assert.Equal(1, html.Split("Mode efficacité pour chrome").Length - 1);
        Assert.Contains("(× 5)", html);
    }

    [Fact]
    public void Suggested_file_name_is_safe()
        => Assert.Equal("Rapport-PCBoost-2026-09-28-" + new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).ToLocalTime().ToString("HHmm"),
            DiagnosticReportHtml.SuggestedFileName(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero), "PC Boost/../x"[..2] + "Boost"));
}

public sealed class DiagnosticReportViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeHardwareHealthService _health = new();
    private readonly FakeReportFiles _files = new();
    private readonly FakeShellService _shell = new();
    private readonly FakeLogSource _logs = new();

    private sealed class FakeReportFiles : IReportFileService
    {
        public bool CanExportPdf { get; set; } = true;

        public string? NextPath { get; set; } = @"C:\Rapports\rapport.pdf";

        public List<(string Path, ReportFileFormat Format, string Html)> Saved { get; } = [];

        public OperationResult Result { get; set; } = OperationResult.Ok();

        public Task<string?> PickSavePathAsync(string suggestedFileName, ReportFileFormat format) => Task.FromResult(NextPath);

        public Task<OperationResult> SaveAsync(string path, string html, ReportFileFormat format, CancellationToken cancellationToken = default)
        {
            Saved.Add((path, format, html));
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeLogSource : IDiagnosticLogSource
    {
        public List<string> Lines { get; } = ["ligne 1"];

        public Task<IReadOnlyList<string>> ReadRecentLinesAsync(int maxLines, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<string>>(Lines.Take(maxLines).ToList());
    }

    private DiagnosticReportViewModel Create()
    {
        _health.Report = HardwareHealthReport.Empty(_ui.Clock.UtcNow);
        return new DiagnosticReportViewModel(_ui.Context, _analyzer, new FakeScore(), new FakeRules(), new FakeSettingsService(), _health, new FakeBootTimeService(),
            new InMemoryOptimizationHistoryRepository(), new FakeActivityJournal(), _logs, new FakeAppInfo(), _files, _shell)
        {
            MachineName = () => "BUREAU-TEST",
            UserName = () => "testeur",
            ProfilePath = () => @"C:\Users\testeur",
        };
    }

    [Fact]
    public async Task Preview_uses_session_analysis_without_new_scan()
    {
        _analyzer.LastReport = _analyzer.Next;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(0, _analyzer.Calls);
        Assert.True(vm.HasPreview);
        Assert.Contains("Rapport de diagnostic PCBoost", vm.PreviewHtml);
        Assert.DoesNotContain("BUREAU-TEST", vm.PreviewHtml);
        Assert.Contains("ligne 1", vm.PreviewHtml);
    }

    [Fact]
    public async Task Without_session_analysis_a_scan_is_run()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(1, _analyzer.Calls);
        Assert.True(vm.HasPreview);
    }

    [Fact]
    public async Task Options_rebuild_the_preview()
    {
        _analyzer.LastReport = _analyzer.Next;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        vm.IncludeLogs = false;
        Assert.DoesNotContain("ligne 1", vm.PreviewHtml);
        vm.IncludeComputerName = true;
        Assert.Contains("BUREAU-TEST", vm.PreviewHtml);
    }

    [Fact]
    public async Task Saving_writes_exactly_the_preview_and_offers_to_reveal()
    {
        _analyzer.LastReport = _analyzer.Next;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        await vm.SavePdfCommand.ExecuteAsync(null);

        var saved = Assert.Single(_files.Saved);
        Assert.Equal(ReportFileFormat.Pdf, saved.Format);
        Assert.Equal(vm.PreviewHtml, saved.Html);
        Assert.Equal("Rapport enregistré : rapport.pdf", vm.StatusMessage);
        Assert.True(vm.HasSavedFile);
        vm.RevealSavedFileCommand.Execute(null);
        Assert.Equal(@"reveal:C:\Rapports\rapport.pdf", Assert.Single(_shell.Calls));
    }

    [Fact]
    public async Task Cancelled_picker_saves_nothing()
    {
        _analyzer.LastReport = _analyzer.Next;
        _files.NextPath = null;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.SaveHtmlCommand.ExecuteAsync(null);
        Assert.Empty(_files.Saved);
        Assert.Null(vm.StatusMessage);
        Assert.False(vm.HasSavedFile);
    }

    [Fact]
    public async Task Pdf_is_disabled_without_webview_but_html_remains()
    {
        _analyzer.LastReport = _analyzer.Next;
        _files.CanExportPdf = false;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.False(vm.SavePdfCommand.CanExecute(null));
        Assert.True(vm.SaveHtmlCommand.CanExecute(null));
    }

    [Fact]
    public async Task Save_failure_is_reported()
    {
        _analyzer.LastReport = _analyzer.Next;
        _files.Result = OperationResult.Fail(OperationErrorKind.AccessDenied);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.SaveHtmlCommand.ExecuteAsync(null);
        Assert.Equal("Accès refusé.", vm.ErrorText);
        Assert.False(vm.HasSavedFile);
    }
}
