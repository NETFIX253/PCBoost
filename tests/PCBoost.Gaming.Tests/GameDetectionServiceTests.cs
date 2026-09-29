using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Security;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Detection.Scanners;
using PCBoost.Gaming.Tests.Fakes;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class GameDetectionServiceTests
{
    private const string HadesDir = @"D:\SteamLibrary\steamapps\common\Hades";
    private const long MiB = 1024 * 1024;

    private readonly FakeProcessProvider _processes = new();
    private readonly InMemoryFileSystemProvider _fs = new();
    private readonly FakeClock _clock = new();
    private readonly List<IGameLibraryScanner> _scanners = [];
    private readonly CountingProcessProvider _counting;

    public GameDetectionServiceTests()
    {
        _counting = new CountingProcessProvider(_processes);
        _scanners.Add(new StaticScanner(GameSource.Steam,
            ScannerSupport.Create("steam:1145360", "Hades", GameSource.Steam, HadesDir, null)));
        _processes.Add(100, "explorer.exe", @"C:\Windows\explorer.exe");
        _processes.Add(101, "svchost.exe", @"C:\Windows\System32\svchost.exe");
        _processes.Add(102, "chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", 400 * MiB);
    }

    private GameDetectionService CreateService(GamingOptions? options = null)
        => new(_scanners, new GameSignatureDatabase(_fs), _counting, new CriticalProcessProtection(), _fs, _clock, options);

    [Fact]
    public async Task Launchers_are_never_detected_even_under_a_game_folder()
    {
        _processes.Add(200, "steam.exe", @"C:\Program Files (x86)\Steam\steam.exe");
        _processes.Add(201, "EpicGamesLauncher.exe", @"C:\Program Files (x86)\Epic Games\Launcher\EpicGamesLauncher.exe");
        _processes.Add(202, "UnityCrashHandler64.exe", HadesDir + @"\UnityCrashHandler64.exe");
        using var service = CreateService();

        Assert.Null(await service.DetectRunningGameAsync());
    }

    [Fact]
    public async Task Detects_known_signature_by_process_name()
    {
        _processes.Add(300, "cs2.exe", @"E:\Jeux\cs2\game\bin\win64\cs2.exe", 3000 * MiB);
        using var service = CreateService();

        var game = await service.DetectRunningGameAsync();

        Assert.NotNull(game);
        Assert.Equal("Counter-Strike 2", game.Game.Name);
        Assert.Equal(GameSource.Signature, game.Game.Source);
        Assert.Equal(300, game.ProcessId);
        Assert.Equal(@"E:\Jeux\cs2\game\bin\win64\cs2.exe", game.ExecutablePath);
    }

    [Fact]
    public async Task Detects_unknown_executable_by_path_under_install_directory()
    {
        _processes.Add(400, "Hades.exe", HadesDir + @"\x64\Hades.exe", 900 * MiB);
        using var service = CreateService();

        var game = await service.DetectRunningGameAsync();

        Assert.NotNull(game);
        Assert.Equal("steam:1145360", game.Game.Id);
        Assert.Equal("Hades", game.Game.Name);
        Assert.Equal(HadesDir, game.Game.InstallDirectory);
    }

    [Fact]
    public async Task Keeps_the_process_with_the_largest_working_set_for_the_same_game()
    {
        _processes.Add(500, "HadesBootstrap.exe", HadesDir + @"\HadesBootstrap.exe", 40 * MiB);
        _processes.Add(501, "Hades.exe", HadesDir + @"\x64\Hades.exe", 1200 * MiB);
        using var service = CreateService();

        var game = await service.DetectRunningGameAsync();

        Assert.Equal(501, game!.ProcessId);
    }

    [Fact]
    public async Task Library_install_directory_wins_over_signature_for_the_same_process()
    {
        _scanners.Add(new StaticScanner(GameSource.Steam,
            ScannerSupport.Create("steam:730", "Counter-Strike 2 (Steam)", GameSource.Steam, @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive", null)));
        _processes.Add(600, "cs2.exe", @"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe", 3000 * MiB);
        using var service = CreateService();

        var game = await service.DetectRunningGameAsync();

        Assert.Equal("steam:730", game!.Game.Id);
    }

    [Fact]
    public async Task Critical_and_system_processes_are_ignored()
    {
        _scanners.Add(new StaticScanner(GameSource.Custom,
            ScannerSupport.Create("custom:bad", "Mauvaise entrée", GameSource.Custom, null, @"C:\Windows\explorer.exe")));
        using var service = CreateService();

        Assert.Null(await service.DetectRunningGameAsync());
    }

    [Fact]
    public async Task Overly_broad_install_directories_are_never_used_for_path_matching()
    {
        _scanners.Add(new StaticScanner(GameSource.EA,
            ScannerSupport.Create("ea:broken", "Entrée cassée", GameSource.EA, @"C:\Program Files", null)));
        using var service = CreateService();

        Assert.Null(await service.DetectRunningGameAsync());
        var games = await service.GetInstalledGamesAsync();
        Assert.Null(Assert.Single(games, g => g.Id == "ea:broken").InstallDirectory);
    }

    [Fact]
    public async Task Executable_path_is_read_once_per_process()
    {
        _processes.Add(700, "Hades.exe", HadesDir + @"\x64\Hades.exe", 900 * MiB);
        using var service = CreateService();

        await service.PollAsync(CancellationToken.None);
        await service.PollAsync(CancellationToken.None);
        await service.PollAsync(CancellationToken.None);

        Assert.All(_counting.PathLookups.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, _counting.FullSnapshotCalls);
        Assert.Equal(3, _counting.IdentityCalls);
    }

    [Fact]
    public async Task Watch_raises_a_single_started_and_a_single_exited_event()
    {
        using var service = CreateService();
        var started = new List<DetectedGameProcess>();
        var exited = new List<DetectedGameProcess>();
        service.GameStarted += (_, g) => started.Add(g);
        service.GameExited += (_, g) => exited.Add(g);

        await service.PollAsync(CancellationToken.None);
        Assert.Empty(started);

        _processes.Add(800, "Hades.exe", HadesDir + @"\x64\Hades.exe", 900 * MiB);
        await service.PollAsync(CancellationToken.None);
        await service.PollAsync(CancellationToken.None);
        // Un second processus du même jeu n'engendre pas un second démarrage.
        _processes.Add(801, "HadesHelper.exe", HadesDir + @"\HadesHelper.exe", 2000 * MiB);
        await service.PollAsync(CancellationToken.None);

        var game = Assert.Single(started);
        Assert.Equal(800, game.ProcessId);
        Assert.Empty(exited);

        _processes.Remove(800);
        _processes.Remove(801);
        await service.PollAsync(CancellationToken.None);
        await service.PollAsync(CancellationToken.None);

        Assert.Equal(800, Assert.Single(exited).ProcessId);
        Assert.Single(started);
    }

    [Fact]
    public async Task Tracked_process_exit_hands_over_to_another_process_of_the_same_game()
    {
        using var service = CreateService();
        var events = new List<string>();
        service.GameStarted += (_, g) => events.Add($"start:{g.ProcessId}");
        service.GameExited += (_, g) => events.Add($"exit:{g.ProcessId}");
        _processes.Add(950, "HadesBootstrap.exe", HadesDir + @"\HadesBootstrap.exe", 40 * MiB);
        await service.PollAsync(CancellationToken.None);
        _processes.Add(951, "Hades.exe", HadesDir + @"\x64\Hades.exe", 1200 * MiB);
        await service.PollAsync(CancellationToken.None);

        _processes.Remove(950);
        await service.PollAsync(CancellationToken.None);

        Assert.Equal(["start:950", "exit:950", "start:951"], events);
    }

    [Fact]
    public async Task Pid_reuse_by_another_program_counts_as_exit()
    {
        using var service = CreateService();
        var exited = 0;
        service.GameExited += (_, _) => exited++;
        _processes.Add(900, "Hades.exe", HadesDir + @"\x64\Hades.exe", 900 * MiB);
        await service.PollAsync(CancellationToken.None);

        _processes.Remove(900);
        _processes.Add(900, "notepad.exe", @"C:\Windows\notepad.exe");
        await service.PollAsync(CancellationToken.None);

        Assert.Equal(1, exited);
    }

    [Fact]
    public async Task A_failing_scanner_does_not_prevent_the_others()
    {
        _scanners.Insert(0, new ThrowingScanner());
        using var service = CreateService();

        var games = await service.GetInstalledGamesAsync();

        Assert.Contains(games, g => g.Id == "steam:1145360");
    }

    [Fact]
    public async Task Library_is_cached_and_refreshed_hourly_or_on_demand()
    {
        var counting = new CountingScanner(ScannerSupport.Create("gog:1", "Jeu GOG", GameSource.Gog, @"D:\GOG\Jeu", null));
        _scanners.Add(counting);
        using var service = CreateService();

        await service.GetInstalledGamesAsync();
        await service.GetInstalledGamesAsync();
        Assert.Equal(1, counting.Calls);

        await service.GetInstalledGamesAsync(refresh: true);
        Assert.Equal(2, counting.Calls);

        _clock.Advance(TimeSpan.FromMinutes(61));
        await service.GetInstalledGamesAsync();
        Assert.Equal(3, counting.Calls);
    }

    [Fact]
    public async Task Duplicates_are_merged_by_install_directory_and_executable()
    {
        _scanners.Add(new StaticScanner(GameSource.WindowsGameConfig,
            ScannerSupport.Create("gcs:1", "x64", GameSource.WindowsGameConfig, null, HadesDir + @"\x64\Hades.exe"),
            ScannerSupport.Create("gcs:2", "Autre", GameSource.WindowsGameConfig, null, @"D:\Autre\autre.exe")));
        _scanners.Add(new StaticScanner(GameSource.Custom,
            ScannerSupport.Create("custom:autre", "Autre (perso)", GameSource.Custom, null, @"D:\Autre\autre.exe")));
        using var service = CreateService();

        var games = await service.GetInstalledGamesAsync();

        Assert.Equal(2, games.Count);
        var hades = Assert.Single(games, g => g.Id == "steam:1145360");
        Assert.Contains("hades.exe", hades.ExecutableNames);
        Assert.Equal("Autre (perso)", Assert.Single(games, g => g.Id == "custom:autre").Name);
    }

    [Fact]
    public async Task Start_and_stop_watching_are_idempotent()
    {
        using var service = CreateService(new GamingOptions { WatchInterval = TimeSpan.FromMilliseconds(20) });
        var started = new TaskCompletionSource<DetectedGameProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.GameStarted += (_, g) => started.TrySetResult(g);
        _processes.Add(1000, "cs2.exe", @"E:\cs2\cs2.exe", 3000 * MiB);

        service.StartWatching();
        service.StartWatching();
        Assert.True(service.IsWatching);
        var game = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        service.StopWatching();
        service.StopWatching();

        Assert.Equal(1000, game.ProcessId);
        Assert.False(service.IsWatching);
    }

    private sealed class StaticScanner(GameSource source, params GameInfo[] games) : IGameLibraryScanner
    {
        public GameSource Source => source;
        public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GameInfo>>(games);
    }

    private sealed class CountingScanner(params GameInfo[] games) : IGameLibraryScanner
    {
        public int Calls { get; private set; }
        public GameSource Source => GameSource.Gog;
        public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<GameInfo>>(games);
        }
    }

    private sealed class ThrowingScanner : IGameLibraryScanner
    {
        public GameSource Source => GameSource.Xbox;
        public Task<IReadOnlyList<GameInfo>> ScanAsync(CancellationToken cancellationToken) => throw new UnauthorizedAccessException("refusé");
    }
}
