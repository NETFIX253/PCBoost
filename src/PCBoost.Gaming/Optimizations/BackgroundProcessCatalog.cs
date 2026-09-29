using PCBoost.Gaming.Detection;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Listes du module « applications d'arrière-plan » : programmes non essentiels connus (synchronisation cloud, mises à jour,
/// navigateurs) et programmes à ne jamais ralentir (communication vocale, diffusion, pilotes audio/graphiques, périphériques,
/// accessibilité, superpositions).
/// </summary>
internal static class BackgroundProcessCatalog
{
    private static readonly NamePatternSet KnownNonEssential = Build(
        // Synchronisation cloud
        "onedrive.exe", "dropbox.exe", "googledrivefs.exe", "googledrivesync.exe", "icloudservices.exe", "iclouddrive.exe",
        "icloudphotos.exe", "megasync.exe", "box.exe", "pcloud.exe", "nextcloud.exe", "owncloud.exe", "synologydrive*.exe",
        // Mises à jour et assistants
        "googleupdate.exe", "adobearm.exe", "jusched.exe", "acrotray.exe", "adobeipcbroker.exe", "ccxprocess.exe",
        "creative cloud.exe", "adobe desktop service.exe", "ituneshelper.exe", "update.exe", "*updater*.exe", "*updatechecker*.exe",
        // Navigateurs (hors premier plan)
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe");

    private static readonly NamePatternSet NeverThrottle = Build(
        // Communication vocale et visio
        "discord*.exe", "teamspeak*.exe", "ts3client*.exe", "mumble.exe", "zoom.exe", "teams.exe", "ms-teams.exe", "skype*.exe",
        "slack.exe", "whatsapp*.exe", "signal.exe", "telegram.exe",
        // Diffusion, enregistrement, superpositions
        "obs*.exe", "streamlabs*.exe", "xsplit*.exe", "nvidia share.exe", "nvidia overlay.exe", "nvidia app.exe", "nvsphelper*.exe",
        "medal*.exe", "overwolf*.exe", "rtss*.exe", "msiafterburner.exe", "gameoverlayui*.exe", "spotify.exe",
        // Pilotes graphiques et audio
        "nvcontainer.exe", "nvdisplay.container.exe", "nvidia*.exe", "amdrs*.exe", "radeonsoftware.exe", "amdow.exe", "atieclxx.exe",
        "atiesrxx.exe", "igfx*.exe", "intelgraphics*.exe", "igcc*.exe", "rtkaud*.exe", "realtek*.exe", "nahimic*.exe", "dolby*.exe",
        "waves*.exe", "maxxaudio*.exe", "voicemeeter*.exe", "audiodg.exe", "sonic*.exe",
        // Périphériques de jeu
        "lghub*.exe", "logi*.exe", "razer*.exe", "icue*.exe", "steelseries*.exe", "armourycrate*.exe", "armoury*.exe", "hyperx*.exe",
        "wooting*.exe", "ds4windows.exe", "x360ce*.exe",
        // Accessibilité et saisie
        "osk.exe", "narrator.exe", "magnify.exe", "tabtip.exe", "ctfmon.exe", "textinputhost.exe");

    public static bool IsKnownNonEssential(string normalizedName) => KnownNonEssential.Matches(normalizedName);

    public static bool IsNeverThrottle(string normalizedName) => NeverThrottle.Matches(normalizedName);

    private static NamePatternSet Build(params string[] entries)
    {
        var set = new NamePatternSet();
        foreach (var e in entries) set.Add(e);
        return set;
    }
}
