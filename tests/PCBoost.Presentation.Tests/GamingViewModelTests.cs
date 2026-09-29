using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class GamingViewModelTests
{
    private sealed class FakeDetection : IGameDetectionService
    {
        public DetectedGameProcess? Running { get; set; }

        public bool IsWatching => false;

        public event EventHandler<DetectedGameProcess>? GameStarted;

        public event EventHandler<DetectedGameProcess>? GameExited;

        public Task<IReadOnlyList<GameInfo>> GetInstalledGamesAsync(bool refresh = false, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<GameInfo>>([new GameInfo { Id = "epic:1", Name = "Fortnite", Source = GameSource.EpicGames }]);

        public Task<DetectedGameProcess?> DetectRunningGameAsync(CancellationToken cancellationToken = default) => Task.FromResult(Running);

        public void StartWatching()
        {
        }

        public void StopWatching()
        {
        }

        public void Start(DetectedGameProcess game) => GameStarted?.Invoke(this, game);

        public void Exit(DetectedGameProcess game) => GameExited?.Invoke(this, game);

        public void Dispose()
        {
        }
    }

    private readonly TestUi _ui = new();
    private readonly FakeGamingService _gaming = new();
    private readonly FakeDetection _detection = new();
    private readonly FakeSettingsService _settings = new();
    private readonly FakeMonitor _monitor = new();

    private GamingViewModel Create() => new(_ui.Context, _gaming, _detection, _settings, _monitor, new FakeShellService());

    [Fact]
    public async Task Inactive_ShowsNoGame_PlannedOptimizations_AndInstalledGames()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("Aucun jeu détecté", vm.CurrentGameText);
        Assert.Equal("Inactif", vm.StateText);
        Assert.Single(vm.PlannedOptimizations);
        Assert.Equal("Prévue", vm.PlannedOptimizations[0].StatusText);
        Assert.Equal("Fortnite", Assert.Single(vm.InstalledGames).Name);
        Assert.Equal("Epic Games", vm.InstalledGames[0].SourceText);
        Assert.Equal("Non disponible", vm.FpsTile.Value);
        Assert.Equal("Activez le mode Gaming avec un jeu en cours pour mesurer les images.", vm.FrameCaptureNotice);
        Assert.True(vm.ActivateCommand.CanExecute(null));
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task Activate_ShowsActiveOptimizations_AndHonestFrameCaptureReason()
    {
        _settings.Current.Gaming.MeasureFrameRate = true;
        var game = new DetectedGameProcess(new GameInfo { Id = "g", Name = "Hades", Source = GameSource.Steam }, 10, null, _ui.Clock.UtcNow);
        _detection.Running = game;
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("Hades", vm.CurrentGameText);

        await vm.ActivateCommand.ExecuteAsync(null);

        Assert.Same(game, _gaming.Activations.Single());
        Assert.True(vm.IsModeActive);
        Assert.Equal("Actif", vm.StateText);
        Assert.Equal("Appliquée", Assert.Single(vm.ActiveOptimizations).StatusText);
        Assert.Equal("Mode Gaming activé : 1 optimisation appliquée.", vm.ResultText);
        Assert.Equal("Autorisation administrateur requise pour mesurer les images.", vm.FrameCaptureNotice);
        Assert.True(vm.DeactivateCommand.CanExecute(null));

        _gaming.PublishMetrics(new GamingLiveMetrics(
            new FrameStats(120, 80, 60, 8.3, 12.5, 7200, TimeSpan.FromMinutes(1)), 45, null, 70, 3L * ByteSize.GiB,
            SensorReading.Of(71, "t"), SensorReading.Unavailable(Availability.NoSensor), FrameCaptureAvailability.Available));
        Assert.Equal("120 FPS", vm.FpsTile.Value);
        Assert.Equal("80 FPS", vm.OnePercentLowTile.Value);
        Assert.Equal("8,3 ms", vm.FrameTimeTile.Value);
        Assert.Equal("Non disponible", vm.GpuTile.Value);
        Assert.Equal("3 Go", vm.VramTile.Value);
        Assert.Equal("71 °C", vm.CpuTemperatureTile.Value);
        Assert.Equal(string.Empty, vm.FrameCaptureNotice);

        await vm.DeactivateCommand.ExecuteAsync(null);
        Assert.False(vm.IsModeActive);
        Assert.Equal("Mode Gaming désactivé : 1 réglage restauré.", vm.ResultText);
        vm.OnNavigatedFrom();
    }

    [Fact]
    public async Task AutoActivation_IsSavedImmediately()
    {
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal((int)AutoGamingBehavior.Ask, vm.AutoActivationIndex);
        vm.AutoActivationIndex = (int)AutoGamingBehavior.Off;
        await Task.Yield();
        Assert.Equal(AutoGamingBehavior.Off, _settings.Current.Gaming.AutoActivation);
        vm.OnNavigatedFrom();
    }
}
