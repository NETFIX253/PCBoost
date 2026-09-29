using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Optimizations;
using PCBoost.Gaming.Services;
using PCBoost.Gaming.Settings;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

/// <summary>Assemble un <see cref="GamingService"/> réel avec ses modules réels et des fournisseurs simulés.</summary>
internal sealed class GamingHarness : IAsyncDisposable
{
    public const int GamePid = 5000;
    public const string GameDir = @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive";
    public const string GameExe = GameDir + @"\game\bin\win64\cs2.exe";

    public FakeClock Clock { get; } = new();
    public FakeProcessProvider Processes { get; } = new();
    public FakePowerProvider Power { get; } = new();
    public FakeSystemInfoProvider SystemInfo { get; } = new();
    public InMemoryRegistryProvider Registry { get; } = new();
    public InMemoryFileSystemProvider FileSystem { get; } = new();
    public FakeSettingsService Settings { get; } = new();
    public FakeNotificationService Notifications { get; } = new();
    public FakeActivityJournal Journal { get; } = new();
    public InMemoryGamingSessionRepository Sessions { get; } = new();
    public FakePerformanceMonitor Monitor { get; } = new();
    public FakeFrameTimeSource Frames { get; } = new();
    public FakeForegroundWindowProvider Foreground { get; } = new();
    public FakeGameDetectionService Detection { get; } = new();
    public FakeRollbackManager Rollback { get; } = new();
    public GamingOptions Options { get; } = new()
    {
        LiveMetricsInterval = TimeSpan.FromHours(1), // les tests pilotent TickAsync eux-mêmes
        Delay = static (_, _) => Task.CompletedTask,
    };
    public DetectedGameProcess Game { get; }
    public GamingService Service { get; }
    public WindowsGameSettingsChecker Checker { get; }
    public List<IOptimization> Modules { get; }

    public GamingHarness(Action<GamingHarness>? configure = null)
    {
        Rollback.Power = Power;
        Rollback.Processes = Processes;
        Rollback.Registry = Registry;
        Processes.Add(GamePid, "cs2.exe", GameExe, 3L * 1024 * 1024 * 1024, hasWindow: true);
        Processes.Add(100, "OneDrive.exe", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        Processes.Add(101, "Discord.exe", @"C:\Users\Test\AppData\Local\Discord\Discord.exe", hasWindow: true);
        Foreground.ForegroundProcessId = GamePid;
        Monitor.SetRunning(true, MonitoringMode.Background);
        Monitor.Latest = FakePerformanceMonitor.Sample(Clock.UtcNow, 45, 60, gpu: 80);
        Game = new DetectedGameProcess(
            new GameInfo { Id = "steam:730", Name = "Counter-Strike 2", Source = GameSource.Steam, InstallDirectory = GameDir, ExecutableNames = ["cs2.exe"] },
            GamePid, GameExe, Clock.UtcNow);
        Detection.Running = Game;
        configure?.Invoke(this);

        var services = new SimpleServiceProvider().Add<IRollbackManager>(Rollback);
        var signatures = new GameSignatureDatabase(FileSystem);
        var protection = new CriticalProcessProtection();
        var gpu = new GamingGpuPreferenceOptimization(Registry, SystemInfo, services);
        Modules =
        [
            new GamingPowerOptimization(Power, SystemInfo, services),
            new GamingPriorityOptimization(Processes, Processes, protection, signatures, services),
            new GamingBackgroundOptimization(Processes, Processes, protection, Foreground, SystemInfo, Settings, FileSystem, signatures, Clock, Options, services),
            gpu,
        ];
        Checker = new WindowsGameSettingsChecker(Registry, SystemInfo, Rollback, gpu);
        Service = new GamingService(Detection, Rollback, Sessions, Settings, Monitor, SystemInfo, Processes, Frames, Notifications, Checker,
            Modules, Clock, Options, Journal);
    }

    public Guid OptimizationSessionId => Service.CurrentSession!.OptimizationSessionId!.Value;

    public ValueTask DisposeAsync() => Service.DisposeAsync();
}
