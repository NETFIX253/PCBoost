using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Settings;
using PCBoost.Gaming.Detection;
using PCBoost.Gaming.Detection.Scanners;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class LibraryScannerTests
{
    private readonly InMemoryRegistryProvider _registry = new();
    private readonly InMemoryFileSystemProvider _fs = new();

    private static string Manifest(string appId, string name, string installDir) => $$"""
        "AppState"
        {
        	"appid"		"{{appId}}"
        	"name"		"{{name}}"
        	"StateFlags"		"4"
        	"installdir"		"{{installDir}}"
        }
        """;

    private void SetupSteam()
    {
        _registry.Set(RegistryHiveKind.CurrentUser, @"Software\Valve\Steam", "SteamPath", RegistryValueData.String("c:/program files (x86)/steam"));
        _fs.AddDirectory(@"C:\Program Files (x86)\Steam\steamapps");
        _fs.AddFile(@"C:\Program Files (x86)\Steam\steamapps\libraryfolders.vdf", 100, content: """
            "libraryfolders"
            {
            	"0" { "path" "C:\\Program Files (x86)\\Steam" }
            	"1" { "path" "D:\\SteamLibrary" }
            }
            """);
        _fs.AddFile(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_730.acf", 10, content: Manifest("730", "Counter-Strike 2", "Counter-Strike Global Offensive"));
        _fs.AddDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game");
        _fs.AddFile(@"C:\Program Files (x86)\Steam\steamapps\appmanifest_228980.acf", 10, content: Manifest("228980", "Steamworks Common Redistributables", "Steamworks Shared"));
        _fs.AddDirectory(@"C:\Program Files (x86)\Steam\steamapps\common\Steamworks Shared");
        _fs.AddFile(@"D:\SteamLibrary\steamapps\appmanifest_1245620.acf", 10, content: Manifest("1245620", "ELDEN RING", "ELDEN RING"));
        _fs.AddDirectory(@"D:\SteamLibrary\steamapps\common\ELDEN RING\Game");
        _fs.AddFile(@"D:\SteamLibrary\steamapps\appmanifest_1493710.acf", 10, content: Manifest("1493710", "Proton Experimental", "Proton - Experimental"));
        _fs.AddDirectory(@"D:\SteamLibrary\steamapps\common\Proton - Experimental");
        // Manifeste orphelin : dossier supprimé.
        _fs.AddFile(@"D:\SteamLibrary\steamapps\appmanifest_570.acf", 10, content: Manifest("570", "Dota 2", "dota 2 beta"));
    }

    [Fact]
    public async Task Steam_scans_all_libraries_and_skips_tools_and_missing_folders()
    {
        SetupSteam();
        var games = await new SteamLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        var cs2 = Assert.Single(games, g => g.Id == "steam:730");
        Assert.Equal("Counter-Strike 2", cs2.Name);
        Assert.Equal(GameSource.Steam, cs2.Source);
        Assert.Equal(@"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive", cs2.InstallDirectory);
        var elden = Assert.Single(games, g => g.Id == "steam:1245620");
        Assert.Equal(@"D:\SteamLibrary\steamapps\common\ELDEN RING", elden.InstallDirectory);
        Assert.DoesNotContain(games, g => g.Name.StartsWith("Proton", StringComparison.Ordinal) || g.Name.StartsWith("Steamworks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Steam_falls_back_to_machine_key_in_32_bit_view()
    {
        _registry.Set(RegistryHiveKind.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath", RegistryValueData.String(@"E:\Steam"), RegistryViewKind.Registry32);
        _fs.AddFile(@"E:\Steam\steamapps\appmanifest_1086940.acf", 10, content: Manifest("1086940", "Baldur's Gate 3", "Baldurs Gate 3"));
        _fs.AddDirectory(@"E:\Steam\steamapps\common\Baldurs Gate 3\bin");

        var games = await new SteamLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        Assert.Equal("Baldur's Gate 3", Assert.Single(games).Name);
    }

    [Fact]
    public async Task Steam_without_installation_returns_nothing()
        => Assert.Empty(await new SteamLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None));

    [Fact]
    public async Task Epic_reads_manifests_and_ignores_incomplete_dlc_and_non_games()
    {
        const string dir = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        _fs.AddFile($@"{dir}\A1.item", 10, content: """
            { "DisplayName": "Fortnite", "InstallLocation": "C:\\Program Files\\Epic Games\\Fortnite",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe",
              "AppName": "Fortnite", "MainGameAppName": "Fortnite", "AppCategories": ["public", "games", "applications"], "bIsIncompleteInstall": false }
            """);
        _fs.AddFile($@"{dir}\A2.item", 10, content: """{ "DisplayName": "Partiel", "InstallLocation": "D:\\Epic\\Partial", "bIsIncompleteInstall": true }""");
        _fs.AddFile($@"{dir}\A3.item", 10, content: """{ "DisplayName": "DLC", "InstallLocation": "D:\\Epic\\Base", "AppName": "BaseDlc1", "MainGameAppName": "Base" }""");
        _fs.AddFile($@"{dir}\A4.item", 10, content: """{ "DisplayName": "Unreal Engine", "InstallLocation": "D:\\Epic\\UE_5.4", "AppCategories": ["engines"] }""");
        _fs.AddFile($@"{dir}\A5.item", 10, content: "{ ceci n'est pas du JSON");
        _fs.AddFile($@"{dir}\A6.item", 10, content: """{ "DisplayName": "Désinstallé", "InstallLocation": "D:\\Epic\\Gone" }""");
        foreach (var d in new[] { @"C:\Program Files\Epic Games\Fortnite", @"D:\Epic\Partial", @"D:\Epic\Base", @"D:\Epic\UE_5.4" }) _fs.AddDirectory(d);

        var games = await new EpicLibraryScanner(_fs).ScanAsync(CancellationToken.None);

        var fortnite = Assert.Single(games);
        Assert.Equal("epic:Fortnite", fortnite.Id);
        Assert.Equal(@"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe", fortnite.ExecutablePath);
        Assert.Contains("fortniteclient-win64-shipping.exe", fortnite.ExecutableNames);
    }

    [Theory]
    [InlineData("431960", "Wallpaper Engine", true)]
    [InlineData("1905180", "OBS Studio", true)]
    [InlineData("0", "Wallpaper Engine: Workshop", true)]
    [InlineData("228980", "Steamworks Common Redistributables", true)]
    [InlineData("730", "Counter-Strike 2", false)]
    [InlineData("570", "Dota 2", false)]
    public void Steam_non_game_applications_are_not_listed_as_games(string appId, string name, bool isTool)
        => Assert.Equal(isTool, SteamLibraryScanner.IsTool(appId, name));

    [Fact]
    public async Task Gog_reads_32_bit_registry_and_skips_dlc()
    {
        const string key = @"SOFTWARE\GOG.com\Games";
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1207664643", "gameName", RegistryValueData.String("The Witcher 3: Wild Hunt"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1207664643", "path", RegistryValueData.String(@"D:\GOG Games\The Witcher 3"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1207664643", "exe", RegistryValueData.String(@"D:\GOG Games\The Witcher 3\bin\x64\witcher3.exe"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1640424747", "gameName", RegistryValueData.String("Blood and Wine"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1640424747", "path", RegistryValueData.String(@"D:\GOG Games\The Witcher 3"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, key + @"\1640424747", "dependsOn", RegistryValueData.String("1207664643"), RegistryViewKind.Registry32);
        _fs.AddDirectory(@"D:\GOG Games\The Witcher 3\bin\x64");

        var games = await new GogLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        var witcher = Assert.Single(games);
        Assert.Equal("gog:1207664643", witcher.Id);
        Assert.Equal(GameSource.Gog, witcher.Source);
        Assert.Equal(["witcher3.exe"], witcher.ExecutableNames);
    }

    [Fact]
    public async Task Ubisoft_uses_uninstall_name_or_folder()
    {
        _registry.Set(RegistryHiveKind.LocalMachine, @"SOFTWARE\Ubisoft\Launcher\Installs\635", "InstallDir", RegistryValueData.String("C:/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Tom Clancy's Rainbow Six Siege/"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Uplay Install 635", "DisplayName", RegistryValueData.String("Tom Clancy's Rainbow Six Siege"), RegistryViewKind.Registry32);
        _registry.Set(RegistryHiveKind.LocalMachine, @"SOFTWARE\Ubisoft\Launcher\Installs\720", "InstallDir", RegistryValueData.String(@"D:\Ubisoft\Far Cry 6"), RegistryViewKind.Registry32);
        _fs.AddDirectory(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Tom Clancy's Rainbow Six Siege");
        _fs.AddDirectory(@"D:\Ubisoft\Far Cry 6");

        var games = await new UbisoftLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Id == "ubisoft:635" && g.Name == "Tom Clancy's Rainbow Six Siege"
            && g.InstallDirectory == @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Tom Clancy's Rainbow Six Siege");
        Assert.Contains(games, g => g.Id == "ubisoft:720" && g.Name == "Far Cry 6");
    }

    [Fact]
    public async Task Publisher_scanners_read_uninstall_keys_and_exclude_launchers()
    {
        void Entry(RegistryHiveKind hive, RegistryViewKind view, string key, string name, string publisher, string location)
        {
            var path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + key;
            _registry.Set(hive, path, "DisplayName", RegistryValueData.String(name), view);
            _registry.Set(hive, path, "Publisher", RegistryValueData.String(publisher), view);
            _registry.Set(hive, path, "InstallLocation", RegistryValueData.String(location), view);
            _fs.AddDirectory(location);
        }
        Entry(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "{EA-BF}", "Battlefield 2042", "Electronic Arts", @"C:\Program Files\EA Games\Battlefield 2042");
        Entry(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "{EA-APP}", "EA app", "Electronic Arts", @"C:\Program Files\Electronic Arts\EA Desktop");
        Entry(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "Overwatch", "Overwatch", "Blizzard Entertainment", @"C:\Program Files (x86)\Overwatch");
        Entry(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry32, "Battle.net", "Battle.net", "Blizzard Entertainment", @"C:\Program Files (x86)\Battle.net");
        Entry(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, "Riot Game valorant.live", "VALORANT", "Riot Games, Inc", @"C:\Riot Games\VALORANT\live");
        Entry(RegistryHiveKind.CurrentUser, RegistryViewKind.Default, "Riot Vanguard", "Riot Vanguard", "Riot Games, Inc", @"C:\Program Files\Riot Vanguard");
        Entry(RegistryHiveKind.LocalMachine, RegistryViewKind.Registry64, "Other", "Tableur", "Autre éditeur", @"C:\Program Files\Tableur");

        var ea = await new EaLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);
        var bnet = await new BattleNetLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);
        var riot = await new RiotLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        Assert.Equal("Battlefield 2042", Assert.Single(ea).Name);
        Assert.Equal(GameSource.EA, ea[0].Source);
        Assert.Equal("Overwatch", Assert.Single(bnet).Name);
        Assert.Equal("VALORANT", Assert.Single(riot).Name);
    }

    [Fact]
    public async Task Riot_reads_product_settings_yaml()
    {
        _fs.AddFile(@"C:\ProgramData\Riot Games\Metadata\league_of_legends.live\league_of_legends.live.product_settings.yaml", 10,
            content: "product_install_full_path: \"C:/Riot Games/League of Legends\"\nproduct_install_root: \"C:/Riot Games\"\n");
        _fs.AddFile(@"C:\ProgramData\Riot Games\Metadata\riot_client\riot_client.product_settings.yaml", 10,
            content: "product_install_full_path: \"C:/Riot Games/Riot Client\"\n");
        _fs.AddDirectory(@"C:\Riot Games\League of Legends");
        _fs.AddDirectory(@"C:\Riot Games\Riot Client");

        var games = await new RiotLibraryScanner(_registry, _fs).ScanAsync(CancellationToken.None);

        var lol = Assert.Single(games);
        Assert.Equal("League of Legends", lol.Name);
        Assert.Equal(@"C:\Riot Games\League of Legends", lol.InstallDirectory);
        Assert.Equal(@"C:\Riot Games\VALORANT\live", RiotLibraryScanner.ReadInstallPath("product_install_full_path: 'C:/Riot Games/VALORANT/live'"));
    }

    [Fact]
    public async Task Xbox_reads_microsoft_game_config_on_fixed_drives()
    {
        _fs.AddFile(@"C:\XboxGames\Forza Horizon 5\Content\MicrosoftGame.config", 10, content: """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="Microsoft.SunriseBaseGame" Publisher="CN=Microsoft" Version="1.0.0.0" />
              <ExecutableList>
                <Executable Name="ForzaHorizon5.exe" Id="Game" />
              </ExecutableList>
              <ShellVisuals DefaultDisplayName="Forza Horizon 5" PublisherDisplayName="Xbox Game Studios" />
            </Game>
            """);
        _fs.AddFile(@"C:\XboxGames\Localized\Content\MicrosoftGame.config", 10, content: """
            <Game><ExecutableList><Executable Name="gamelaunchhelper.exe" /></ExecutableList><ShellVisuals DefaultDisplayName="ms-resource:AppName" /></Game>
            """);
        _fs.AddFile(@"C:\XboxGames\Broken\Content\MicrosoftGame.config", 10, content: "<Game><unclosed>");

        var games = await new XboxLibraryScanner(_fs).ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        var forza = Assert.Single(games, g => g.Name == "Forza Horizon 5");
        Assert.Equal("xbox:Microsoft.SunriseBaseGame", forza.Id);
        Assert.Equal(@"C:\XboxGames\Forza Horizon 5\Content", forza.InstallDirectory);
        Assert.Equal(["forzahorizon5.exe"], forza.ExecutableNames);
        Assert.Contains(games, g => g.Name == "Localized");
    }

    [Fact]
    public async Task GameConfigStore_keeps_existing_games_and_skips_launchers_system_and_stale_entries()
    {
        var signatures = new GameSignatureDatabase(_fs);
        void Child(string id, string exe) => _registry.Set(RegistryHiveKind.CurrentUser, @"System\GameConfigStore\Children\" + id, "MatchedExeFullPath", RegistryValueData.String(exe));
        Child("a", @"D:\Games\Hades II\Ship\Hades2.exe");
        Child("b", @"C:\Program Files (x86)\Steam\steam.exe");
        Child("c", @"C:\Windows\System32\notepad.exe");
        Child("d", @"D:\Games\Supprimé\old.exe");
        Child("e", @"D:\Jeux\Mon Jeu\bin\win64\MonJeu-Win64-Shipping.exe");
        _fs.AddFile(@"D:\Games\Hades II\Ship\Hades2.exe", 100);
        _fs.AddFile(@"C:\Program Files (x86)\Steam\steam.exe", 100);
        _fs.AddFile(@"C:\Windows\System32\notepad.exe", 100);
        _fs.AddFile(@"D:\Jeux\Mon Jeu\bin\win64\MonJeu-Win64-Shipping.exe", 100);

        var games = await new GameConfigStoreScanner(_registry, _fs, signatures).ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Name == "Ship" || g.Name == "Hades II");
        Assert.Contains(games, g => g.Name == "Mon Jeu" && g.ExecutablePath == @"D:\Jeux\Mon Jeu\bin\win64\MonJeu-Win64-Shipping.exe");
        Assert.All(games, g => Assert.Equal(GameSource.WindowsGameConfig, g.Source));
    }

    [Fact]
    public async Task Custom_games_come_from_settings()
    {
        var settings = new FakeSettingsService();
        settings.Current.Gaming.CustomGames.Add(new CustomGameEntry { Name = "Mon émulateur", ExecutablePath = @"D:\Emu\emu.exe" });
        settings.Current.Gaming.CustomGames.Add(new CustomGameEntry { Name = "", ExecutablePath = @"D:\Jeux\Rogue.exe" });
        settings.Current.Gaming.CustomGames.Add(new CustomGameEntry { Name = "Invalide", ExecutablePath = @"D:\Jeux\readme.txt" });

        var games = await new CustomGamesScanner(settings).ScanAsync(CancellationToken.None);

        Assert.Equal(2, games.Count);
        Assert.Contains(games, g => g.Name == "Mon émulateur" && g.ExecutableNames.Contains("emu.exe") && g.Source == GameSource.Custom);
        Assert.Contains(games, g => g.Name == "Rogue");
    }
}
