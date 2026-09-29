using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class OldPcRestorePointTests
{
    private readonly TestUi _ui = new();
    private readonly FakeOptimizationManager _manager = new();
    private readonly FakeAnalyzer _analyzer = new();
    private readonly FakeRollbackManager _rollback = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeRestorePointService _restore = new();

    private sealed class FakeRestorePointService : IRestorePointService
    {
        public RestorePointResult Result { get; set; } = new(RestorePointStatus.Created, new DateTimeOffset(2026, 9, 28, 11, 59, 0, TimeSpan.Zero), TextRef.Of("Opt_RestorePoint_Created"));

        public int Calls { get; private set; }

        public Task<RestorePointResult> CreateAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Result);
        }
    }

    private async Task<OldPcViewModel> PreviewAsync(OldPcLevel level)
    {
        var vm = new OldPcViewModel(_ui.Context, new FakeOldPcAssistant(), _analyzer, _manager, _rollback, _settings, _restore);
        await vm.OnNavigatedToAsync(null);
        await vm.AssessCommand.ExecuteAsync(null);
        await vm.Levels.Single(l => l.Level == level).SelectCommand.ExecuteAsync(null);
        Assert.True(vm.IsPreview);
        return vm;
    }

    [Fact]
    public async Task Advanced_level_creates_a_restore_point_before_applying()
    {
        var vm = await PreviewAsync(OldPcLevel.Advanced);
        Assert.True(vm.ShowRestorePointNotice);

        await vm.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, _restore.Calls);
        Assert.NotNull(_manager.Executed);
        Assert.True(vm.IsReport);
        Assert.StartsWith("Point de restauration Windows créé le ", vm.RestorePointText);
    }

    [Fact]
    public async Task Other_levels_do_not_create_a_restore_point()
    {
        var vm = await PreviewAsync(OldPcLevel.Standard);
        Assert.False(vm.ShowRestorePointNotice);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, _restore.Calls);
        Assert.False(vm.HasRestorePointText);
    }

    [Fact]
    public async Task Setting_off_skips_the_restore_point()
    {
        _settings.Current.CreateRestorePointBeforeAdvanced = false;
        var vm = await PreviewAsync(OldPcLevel.Advanced);
        Assert.False(vm.ShowRestorePointNotice);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.Equal(0, _restore.Calls);
        Assert.NotNull(_manager.Executed);
    }

    [Fact]
    public async Task Recent_point_is_reused_and_reported()
    {
        _restore.Result = new RestorePointResult(RestorePointStatus.RecentExists, new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero), TextRef.Of("Opt_RestorePoint_Recent"));
        var vm = await PreviewAsync(OldPcLevel.Advanced);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.StartsWith("Un point de restauration Windows du ", vm.RestorePointText);
        Assert.NotNull(_manager.Executed);
    }

    [Fact]
    public async Task Failed_point_asks_before_continuing_and_cancel_applies_nothing()
    {
        _restore.Result = new RestorePointResult(RestorePointStatus.Disabled, null, TextRef.Of("Opt_RestorePoint_Disabled"));
        // Première confirmation (niveau Avancé) acceptée, seconde (continuer sans point) refusée.
        var dialogs = new SequenceDialogs(_ui.Dialogs, DialogResultKind.Primary, DialogResultKind.Cancel);
        var vm2 = new OldPcViewModel(new PCBoost.Presentation.Common.ViewModelContext(_ui.Localizer, _ui.Formatter, _ui.Dispatcher, _ui.Navigation, dialogs,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, _ui.Clock), new FakeOldPcAssistant(), _analyzer, _manager, _rollback, _settings, _restore);
        await vm2.OnNavigatedToAsync(null);
        await vm2.AssessCommand.ExecuteAsync(null);
        await vm2.Levels.Single(l => l.Level == OldPcLevel.Advanced).SelectCommand.ExecuteAsync(null);

        await vm2.ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, _restore.Calls);
        Assert.Null(_manager.Executed);
        Assert.True(vm2.IsPreview);
        var ask = dialogs.Requests.Last();
        Assert.Equal("OldPc_RestorePoint_ContinueTitle", ask.Title.Key);
        Assert.Equal("Opt_RestorePoint_Disabled", Assert.Single(ask.Details!).Key);
    }

    [Fact]
    public async Task Failed_point_can_be_skipped_explicitly()
    {
        _restore.Result = new RestorePointResult(RestorePointStatus.Failed, null, TextRef.Literal("Service indisponible."));
        _ui.Dialogs.NextResult = DialogResultKind.Primary;
        var vm = await PreviewAsync(OldPcLevel.Advanced);
        await vm.ApplyCommand.ExecuteAsync(null);
        Assert.NotNull(_manager.Executed);
        Assert.Equal("Aucun point de restauration Windows n'a été créé. Service indisponible.", vm.RestorePointText);
    }

    /// <summary>Renvoie une suite de réponses (une par confirmation), en conservant les demandes.</summary>
    private sealed class SequenceDialogs(FakeDialogService inner, params DialogResultKind[] answers) : IDialogService
    {
        private int _index;

        public List<ConfirmationRequest> Requests { get; } = [];

        public Task<DialogResultKind> ConfirmAsync(ConfirmationRequest request)
        {
            Requests.Add(request);
            return Task.FromResult(_index < answers.Length ? answers[_index++] : DialogResultKind.Cancel);
        }

        public Task ShowErrorAsync(OperationResult result, TextRef? context = null) => inner.ShowErrorAsync(result, context);

        public Task ShowMessageAsync(TextRef title, TextRef message) => inner.ShowMessageAsync(title, message);
    }
}
