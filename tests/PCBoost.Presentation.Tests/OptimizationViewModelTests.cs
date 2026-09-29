using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class OptimizationViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeOptimizationManager _manager = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeProfileService _profiles = new();

    private OptimizationViewModel Create()
    {
        var profilesVm = new ProfilesViewModel(_ui.Context, _profiles, _manager, _settings);
        var oldPcVm = new OldPcViewModel(_ui.Context, new FakeOldPcAssistant(), _analyzer, _manager, _rollback, _settings);
        return new OptimizationViewModel(_ui.Context, _manager, _analyzer, _rollback, _settings, _profiles, profilesVm, oldPcVm);
    }

    private static OptimizationPlan Plan(bool includeIrreversible, RiskLevel risk = RiskLevel.Low) => new(
        SessionType.OneClick,
        [
            new OptimizationPreview("power", TextRef.Literal("Alimentation"), true, null,
                [new PlannedChange("p1", TextRef.Literal("Plan Performances"), "power", true, true, risk)],
                risk, ImpactLevel.Medium, true, false, false),
            new OptimizationPreview("temp", TextRef.Literal("Fichiers temporaires"), true, null,
                [new PlannedChange("t1", TextRef.Literal("Supprimer Temp"), "%TEMP%", includeIrreversible, false, RiskLevel.Low, 200L * ByteSize.MiB)],
                RiskLevel.Low, ImpactLevel.Low, false, false, false),
            OptimizationPreview.NotApplicable("gpu", TextRef.Literal("GPU"), TextRef.Literal("Aucun GPU dédié"), RiskLevel.Low, ImpactLevel.Low, true),
        ],
        new HashSet<string>());

    [Fact]
    public async Task Starts_Idle_ThenScan_ShowsPreview_WithoutApplyingAnything()
    {
        _manager.Plan = Plan(includeIrreversible: false);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.True(vm.IsIdle);
        await vm.ScanCommand.ExecuteAsync(null);

        Assert.True(vm.IsPreview);
        Assert.NotNull(vm.Preview);
        Assert.Equal(2, vm.Preview!.Items.Count);
        Assert.Single(vm.Preview.NotApplicableItems);
        Assert.Equal(1, vm.Preview.SelectedChangeCount);
        Assert.False(vm.Preview.HasIrreversibleSelected);
        Assert.Null(_manager.Executed);
        Assert.Equal(1, _analyzer.Calls);
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task Scan_ReusesRecentAnalysis()
    {
        _analyzer.LastReport = Reports.Create(_ui.Clock.UtcNow.AddMinutes(-2));
        _manager.Plan = Plan(false);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.Equal(0, _analyzer.Calls);
        Assert.True(vm.IsPreview);
    }

    [Fact]
    public async Task Apply_WithoutIrreversible_ExecutesWithoutConfirmation_AndShowsReport()
    {
        _manager.Plan = Plan(false);
        _settings.Current.ConfirmSensitiveOperations = false;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Empty(_ui.Dialogs.Confirmations);
        Assert.True(vm.IsReport);
        Assert.NotNull(_manager.Executed);
        Assert.Equal(["p1"], _manager.Executed!.SelectedChangeIds.ToArray());
        Assert.False(_manager.Executed.IrreversibleActionsConfirmed);
        Assert.Equal("Votre PC est prêt.", vm.Report!.StatusTitle);
        Assert.Contains(vm.Report.Lines, l => l.Value == "\u22122 applications");
        Assert.All(vm.Steps, s => Assert.True(s.IsDone));
        Assert.True(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task Apply_WithIrreversibleSelection_RequiresConfirmation_DeclineKeepsPreview()
    {
        _manager.Plan = Plan(includeIrreversible: true);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        Assert.True(vm.Preview!.HasIrreversibleSelected);

        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await vm.ApplyCommand.ExecuteAsync(null);

        var request = Assert.Single(_ui.Dialogs.Confirmations);
        Assert.True(request.IsDestructive);
        Assert.Contains(request.Details!, d => d.Key == "Optimization_Confirm_IrreversibleItem");
        Assert.True(vm.IsPreview);
        Assert.Null(_manager.Executed);
    }

    [Fact]
    public async Task Apply_WithIrreversibleSelection_ConfirmedPlanCarriesConfirmation()
    {
        _manager.Plan = Plan(includeIrreversible: true);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        _ui.Dialogs.NextResult = DialogResultKind.Primary;
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.IsReport);
        Assert.True(_manager.Executed!.IrreversibleActionsConfirmed);
        Assert.Equal(2, _manager.Executed.SelectedChangeIds.Count);
    }

    [Fact]
    public async Task HighRisk_RequiresConfirmation()
    {
        _manager.Plan = Plan(false, RiskLevel.High);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Single(_ui.Dialogs.Confirmations);
        Assert.True(_manager.Executed!.HighRiskActionsConfirmed);
    }

    [Fact]
    public async Task EmptySelection_DisablesApply()
    {
        _manager.Plan = Plan(false);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);

        vm.Preview!.SelectNoneCommand.Execute(null);

        Assert.Equal(0, vm.Preview.SelectedChangeCount);
        Assert.False(vm.ApplyCommand.CanExecute(null));
        Assert.Equal(false, vm.Preview.Items[0].SelectionState);
    }

    [Fact]
    public async Task Restore_CallsRollbackManager_ForReportSession()
    {
        _manager.Plan = Plan(false);
        _settings.Current.ConfirmSensitiveOperations = false;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        await vm.RestoreCommand.ExecuteAsync(null);

        Assert.Equal([_manager.Report.SessionId], _rollback.Restored);
        Assert.True(vm.IsRestored);
        Assert.Contains("3 modifications restaurées.", vm.RestoreResultText);
        Assert.False(vm.RestoreCommand.CanExecute(null));
    }

    [Fact]
    public async Task CancelPreview_ReturnsToIdle()
    {
        _manager.Plan = Plan(false);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        vm.CancelPreviewCommand.Execute(null);
        Assert.True(vm.IsIdle);
        Assert.Null(vm.Preview);
        Assert.Null(_manager.Executed);
    }

    [Fact]
    public async Task NavigationParameter_OptimizationId_BuildsTargetedPreview()
    {
        _manager.Plan = Plan(false);
        var vm = Create();
        await vm.OnNavigatedToAsync("power");
        Assert.True(vm.IsPreview);
        Assert.Equal(["power"], _manager.BuiltPlans.Single().ToArray());
    }

    [Fact]
    public async Task NavigationParameter_OldPc_SelectsSectionBeforeChildSectionsFinishLoading()
    {
        // Régression : la section « Ancien PC » n'était choisie qu'après le chargement des profils et l'évaluation ;
        // la page affichait « Optimiser mon PC » en attendant (et pouvait y rester si la sélection de l'onglet était écrasée).
        _analyzer.LastReport = _analyzer.Next;
        var assistant = new BlockingOldPcAssistant();
        var vm = new OptimizationViewModel(_ui.Context, _manager, _analyzer, _rollback, _settings, _profiles,
            new ProfilesViewModel(_ui.Context, _profiles, _manager, _settings),
            new OldPcViewModel(_ui.Context, assistant, _analyzer, _manager, _rollback, _settings));

        var navigation = vm.OnNavigatedToAsync(PCBoost.Presentation.Navigation.PageKeys.OldPc);
        await assistant.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, vm.SelectedSectionIndex);
        assistant.Release();
        await navigation.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, vm.SelectedSectionIndex);
        Assert.True(vm.OldPc.IsAssessed);
        Assert.Equal(3, vm.OldPc.Levels.Count);
    }

    /// <summary>Évaluation « PC ancien » suspendue jusqu'à <see cref="Release"/> (chargement lent).</summary>
    private sealed class BlockingOldPcAssistant : IOldPcAssistant
    {
        private readonly FakeOldPcAssistant _inner = new();
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _release.TrySetResult();

        public async Task<OldPcAssessment> AssessAsync(PCBoost.Core.Models.Analysis.SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await _inner.AssessAsync(analysis, cancellationToken).ConfigureAwait(false);
        }

        public Task<OptimizationPlan> BuildPlanAsync(OldPcLevel level, PCBoost.Core.Models.Analysis.SystemAnalysisReport analysis, CancellationToken cancellationToken = default)
            => _inner.BuildPlanAsync(level, analysis, cancellationToken);
    }

    [Fact]
    public async Task NavigationParameter_Profiles_SelectsSection()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(PCBoost.Presentation.Navigation.PageRegistry.Profiles);
        Assert.Equal(1, vm.SelectedSectionIndex);
        Assert.Equal(3, vm.Profiles.Profiles.Count);
    }

    [Fact]
    public async Task FailedRun_ReportShowsPartialStatus()
    {
        _manager.Plan = Plan(false);
        _manager.Report = new OptimizationRunReport(Guid.NewGuid(), SessionStatus.PartiallyCompleted, 1, 1, 0, 0, 0, true, null,
            [new OptimizationResult("power", OperationResult.Fail(OperationErrorKind.AccessDenied), 0, 1, 0, false, [])], []);
        _settings.Current.ConfirmSensitiveOperations = false;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ScanCommand.ExecuteAsync(null);
        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.True(vm.Report!.IsPartial);
        Assert.True(vm.Report.RequiresRestart);
        var result = Assert.Single(vm.Report.Results);
        Assert.Equal("Optimisation power", result.Text);
        Assert.Equal("Accès refusé.", result.Detail);
    }
}
