using PCBoost.Core.Abstractions.Platform;
using PCBoost.Gaming.Detection;
using PCBoost.TestUtilities;

namespace PCBoost.Gaming.Tests;

public sealed class GameSignatureDatabaseTests
{
    private const string UserFile = @"C:\Users\Test\AppData\Local\PCBoost\game-signatures.user.json";

    [Fact]
    public void Embedded_database_contains_popular_games()
    {
        var db = new GameSignatureDatabase(new InMemoryFileSystemProvider());

        Assert.True(db.GameCount >= 80, $"{db.GameCount} signatures seulement");
        Assert.True(db.TryGetGameName("cs2.exe", out var cs2));
        Assert.Equal("Counter-Strike 2", cs2);
        Assert.True(db.TryGetGameName("VALORANT-Win64-Shipping.exe", out var valorant));
        Assert.Equal("VALORANT", valorant);
        Assert.True(db.TryGetGameName("League of Legends.exe", out _));
        Assert.True(db.TryGetGameName("bg3_dx11", out var bg3));
        Assert.Equal("Baldur's Gate 3", bg3);
        Assert.True(db.TryGetGameName(@"D:\Jeux\PUBG\TslGame\Binaries\Win64\TslGame.exe", out var pubg));
        Assert.Equal("PUBG: BATTLEGROUNDS", pubg);
        Assert.All(db.Games.Keys, k => Assert.Equal(k.ToLowerInvariant(), k));
        Assert.False(db.TryGetGameName("notepad.exe", out _));
    }

    [Theory]
    [InlineData("steam.exe")]
    [InlineData("steamwebhelper.exe")]
    [InlineData("EpicGamesLauncher.exe")]
    [InlineData("Battle.net.exe")]
    [InlineData("RiotClientServices.exe")]
    [InlineData("EADesktop.exe")]
    [InlineData("upc.exe")]
    [InlineData("GalaxyClient.exe")]
    [InlineData("XboxPcApp.exe")]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("CrashReportClient.exe")]
    [InlineData("EasyAntiCheat_EOS.exe")]
    [InlineData("vgc.exe")]
    [InlineData("BEService_x64.exe")]
    [InlineData("VC_redist.x64.exe")]
    [InlineData("Game_Setup_v2.exe")]
    [InlineData("unins000.exe")]
    [InlineData("MyGameInstaller.exe")]
    [InlineData("dxsetup.exe")]
    [InlineData("wallpaper64.exe")]
    [InlineData("webwallpaper32.exe")]
    [InlineData("obs64.exe")]
    [InlineData("deluge.exe")]
    [InlineData("vlc.exe")]
    public void Launchers_utilities_and_installers_are_never_games(string exe)
    {
        var db = new GameSignatureDatabase(new InMemoryFileSystemProvider());

        Assert.True(db.IsExcluded(exe));
        Assert.False(db.TryGetGameName(exe, out _));
    }

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("wallpaper64.exe")]
    [InlineData("deluge.exe")]
    public void Everyday_applications_are_never_games_but_remain_eligible_for_background_throttling(string exe)
    {
        var db = new GameSignatureDatabase(new InMemoryFileSystemProvider());

        Assert.True(db.IsExcluded(exe));
        Assert.False(db.IsLauncherOrUtility(exe));
        Assert.False(db.IsAntiCheat(exe));
    }

    [Theory]
    [InlineData("vgc.exe", true)]
    [InlineData("EasyAntiCheat.exe", true)]
    [InlineData("EAAntiCheat.GameService.exe", true)]
    [InlineData("FACEIT.exe", true)]
    [InlineData("BEService.exe", true)]
    [InlineData("steam.exe", false)]
    [InlineData("cs2.exe", false)]
    public void Anti_cheat_processes_are_recognised(string exe, bool expected)
        => Assert.Equal(expected, new GameSignatureDatabase(new InMemoryFileSystemProvider()).IsAntiCheat(exe));

    [Fact]
    public void User_extension_adds_games_and_exclusions_but_cannot_unlock_launchers()
    {
        var fs = new InMemoryFileSystemProvider();
        fs.AddFile(UserFile, 100, content: """
            {
              // commentaires autorisés
              "games": { "MonJeu.exe": "Mon jeu indé", "setupwars.exe": "Setup Wars", "steam.exe": "Pas un jeu" },
              "nonGames": [ "outil_maison.exe", "helper*.exe" ],
            }
            """);

        var db = new GameSignatureDatabase(fs);

        Assert.True(db.TryGetGameName("monjeu.exe", out var mine));
        Assert.Equal("Mon jeu indé", mine);
        // Un jeu déclaré par l'utilisateur l'emporte sur le motif générique « *setup*.exe »…
        Assert.True(db.TryGetGameName("SetupWars.exe", out _));
        // … mais jamais sur un lanceur nommé exactement.
        Assert.False(db.TryGetGameName("steam.exe", out _));
        Assert.True(db.IsExcluded("outil_maison.exe"));
        Assert.True(db.IsExcluded("helper_x64.exe"));
        Assert.True(db.TryGetGameName("cs2.exe", out _));
        Assert.Equal(UserFile, db.UserFilePath);
    }

    [Fact]
    public void Invalid_user_extension_is_ignored()
    {
        var fs = new InMemoryFileSystemProvider();
        fs.AddFile(UserFile, 10, content: "{ invalide");

        var db = new GameSignatureDatabase(fs);

        Assert.True(db.TryGetGameName("cs2.exe", out _));
    }

    [Fact]
    public void User_extension_uses_branding_data_folder()
    {
        var fs = new InMemoryFileSystemProvider();
        fs.AddFile(@"C:\Users\Test\AppData\Local\Rebrand\game-signatures.user.json", 10, content: """{ "games": { "x.exe": "X" } }""");

        var db = new GameSignatureDatabase(fs, new Core.Branding.BrandingOptions { DataFolderName = "Rebrand" });

        Assert.True(db.TryGetGameName("x.exe", out _));
    }

    [Theory]
    [InlineData("easyanticheat_eos.exe", "easyanticheat*.exe", true)]
    [InlineData("vc_redist.x64.exe", "vc_redist*", true)]
    [InlineData("abc.exe", "a?c.exe", true)]
    [InlineData("abc.exe", "*setup*.exe", false)]
    [InlineData("x.exe", "*", true)]
    public void Wildcard_matching(string text, string pattern, bool expected)
        => Assert.Equal(expected, Wildcard.IsMatch(text, pattern));
}
