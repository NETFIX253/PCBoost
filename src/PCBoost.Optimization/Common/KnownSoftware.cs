using System.Text.RegularExpressions;

namespace PCBoost.Optimization.Common;

/// <summary>Famille d'une application connue lancée au démarrage (sert à la recommandation et à sa raison).</summary>
internal enum KnownAppFamily { None = 0, CloudSync, GameLauncher, Communication, Media, Updater, Utility, BrowserHelper }

/// <summary>
/// Connaissances factuelles et limitées sur des logiciels courants : éditeurs de sécurité, pilotes matériels,
/// applications optionnelles au démarrage, processus d'arrière-plan réputés lourds. Aucune qualification « malveillant ».
/// </summary>
internal static partial class KnownSoftware
{
    // Éditeurs de logiciels de sécurité (mots entiers, insensible à la casse).
    [GeneratedRegex(@"\b(avast|avg|avira|bitdefender|eset|kaspersky|mcafee|norton|nortonlifelock|gen digital|symantec|sophos|malwarebytes|trend micro|f-secure|withsecure|panda security|watchguard|webroot|g data|emsisoft|comodo|bullguard|k7 computing|quick heal|total defense|zonealarm|check point)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecurityPublisherRegex();

    private static readonly HashSet<string> SecurityExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "securityhealthsystray.exe", "securityhealthservice.exe", "msmpeng.exe", "msascuil.exe", "mpcmdrun.exe", "nissrv.exe",
        "avastui.exe", "avgui.exe", "avguard.exe", "avgnt.exe", "bdagent.exe", "seccenter.exe", "egui.exe", "ekrn.exe", "avp.exe", "avpui.exe",
        "mcuicnt.exe", "mcshield.exe", "nortonsecurity.exe", "ns.exe", "savservice.exe", "sophosui.exe", "mbam.exe", "mbamtray.exe",
        "uiseagnt.exe", "fsorsp64.exe", "psuaservice.exe", "wrsa.exe",
    };

    private static readonly string[] SecurityNameFragments =
    [
        "SecurityHealth", "Windows Security", "Sécurité Windows", "Windows Defender", "Microsoft Defender",
    ];

    // Pilotes et utilitaires matériels à conserver (audio, pavé tactile, graphiques).
    private static readonly string[] DriverPublisherFragments =
    [
        "Realtek", "Synaptics", "ELAN Microelectronics", "Elantech", "NVIDIA", "Advanced Micro Devices", "Conexant", "Cirrus Logic", "Dolby",
    ];

    private static readonly string[] DriverNameFragments =
    [
        "RtkAudUService", "RAVCpl", "RAVBg", "RtHDVCpl", "Realtek", "SynTPEnh", "Synaptics", "ETDCtrl", "ELAN", "igfxtray", "igfxpers", "igfxhk",
        "igfxem", "IntelGraphics", "Intel(R) Graphics", "GfxUI", "NvBackend", "NVIDIA", "nvtray", "RadeonSoftware", "AMD", "Dolby", "WavesSvc",
        "MaxxAudio", "SmartAudio", "IAStorIcon", "Thunderbolt",
    ];

    private static readonly (string Fragment, KnownAppFamily Family)[] OptionalApps =
    [
        ("OneDrive", KnownAppFamily.CloudSync),
    ];

    private static readonly (string Fragment, KnownAppFamily Family)[] DisableableApps =
    [
        ("Discord", KnownAppFamily.Communication),
        ("Spotify", KnownAppFamily.Media),
        ("Steam", KnownAppFamily.GameLauncher),
        ("EpicGamesLauncher", KnownAppFamily.GameLauncher),
        ("Epic Games", KnownAppFamily.GameLauncher),
        ("EADesktop", KnownAppFamily.GameLauncher),
        ("EA app", KnownAppFamily.GameLauncher),
        ("Origin", KnownAppFamily.GameLauncher),
        ("UbisoftConnect", KnownAppFamily.GameLauncher),
        ("Ubisoft Connect", KnownAppFamily.GameLauncher),
        ("Uplay", KnownAppFamily.GameLauncher),
        ("upc.exe", KnownAppFamily.GameLauncher),
        ("Battle.net", KnownAppFamily.GameLauncher),
        ("GOG Galaxy", KnownAppFamily.GameLauncher),
        ("GalaxyClient", KnownAppFamily.GameLauncher),
        ("Teams", KnownAppFamily.Communication),
        ("Skype", KnownAppFamily.Communication),
        ("Zoom", KnownAppFamily.Communication),
        ("Slack", KnownAppFamily.Communication),
        ("WhatsApp", KnownAppFamily.Communication),
        ("Telegram", KnownAppFamily.Communication),
        ("Dropbox", KnownAppFamily.CloudSync),
        ("GoogleDriveFS", KnownAppFamily.CloudSync),
        ("Google Drive", KnownAppFamily.CloudSync),
        ("iCloud", KnownAppFamily.CloudSync),
        ("AdobeGCInvoker", KnownAppFamily.Updater),
        ("AdobeARM", KnownAppFamily.Updater),
        ("Adobe Creative Cloud", KnownAppFamily.Updater),
        ("CCXProcess", KnownAppFamily.Updater),
        ("AdobeCollabSync", KnownAppFamily.Updater),
        ("Acrobat Assistant", KnownAppFamily.Updater),
        ("jusched", KnownAppFamily.Updater),
        ("SunJavaUpdateSched", KnownAppFamily.Updater),
        ("Java Update", KnownAppFamily.Updater),
        ("APSDaemon", KnownAppFamily.Updater),
        ("Apple Push", KnownAppFamily.Updater),
        ("iTunesHelper", KnownAppFamily.Media),
        ("Apple Software Update", KnownAppFamily.Updater),
        ("SoftwareUpdate", KnownAppFamily.Updater),
        ("GoogleUpdate", KnownAppFamily.Updater),
        ("CCleaner", KnownAppFamily.Utility),
        ("MicrosoftEdgeAutoLaunch", KnownAppFamily.BrowserHelper),
        ("Opera Browser Assistant", KnownAppFamily.BrowserHelper),
        ("BraveSoftware Update", KnownAppFamily.Updater),
        ("Cortana", KnownAppFamily.Utility),
        ("uTorrent", KnownAppFamily.Utility),
        ("qBittorrent", KnownAppFamily.Utility),
        ("Viber", KnownAppFamily.Communication),
    ];

    /// <summary>Processus d'arrière-plan réputés gourmands (noms d'image en minuscules).</summary>
    public static readonly IReadOnlySet<string> HeavyBackgroundProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "onedrive.exe", "dropbox.exe", "googledrivefs.exe", "teams.exe", "ms-teams.exe", "skype.exe", "adobecollabsync.exe",
        "adobearm.exe", "adobeupdateservice.exe", "acrotray.exe", "ccxprocess.exe", "adobe desktop service.exe", "creative cloud.exe",
        "adobegcclient.exe", "jusched.exe", "jucheck.exe", "googleupdate.exe", "googlecrashhandler.exe", "googlecrashhandler64.exe",
        "microsoftedgeupdate.exe", "ituneshelper.exe", "apsdaemon.exe", "icloudservices.exe", "idrivesync.exe",
    };

    /// <summary>Processus que PCBoost ne touche jamais, indépendamment de la liste de protection configurable (défense en profondeur).</summary>
    public static readonly IReadOnlySet<string> NeverTouchProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "memory compression", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "services.exe", "lsass.exe", "lsaiso.exe", "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe",
        "msmpeng.exe", "securityhealthservice.exe", "pcboost.exe", "pcboost.elevator.exe",
    };

    public static bool IsSecuritySoftware(string? name, string? executablePath, string? publisher)
    {
        var fileName = executablePath is null ? null : PathUtil.FileName(executablePath);
        if (fileName is not null && SecurityExecutables.Contains(fileName)) return true;
        if (name is not null && SecurityExecutables.Contains(name)) return true;
        if (ContainsAny(name, SecurityNameFragments) || ContainsAny(fileName, SecurityNameFragments)) return true;
        return publisher is not null && SecurityPublisherRegex().IsMatch(publisher);
    }

    public static bool IsHardwareDriverUtility(string? name, string? executablePath, string? publisher)
    {
        var fileName = executablePath is null ? null : PathUtil.FileName(executablePath);
        if (ContainsAny(publisher, DriverPublisherFragments)) return true;
        // Intel publie aussi des utilitaires non graphiques : seul le nom identifie le pilote graphique.
        return ContainsAny(name, DriverNameFragments) || ContainsAny(fileName, DriverNameFragments);
    }

    public static KnownAppFamily OptionalFamily(string? name, string? executablePath) => Match(OptionalApps, name, executablePath);

    public static KnownAppFamily DisableableFamily(string? name, string? executablePath) => Match(DisableableApps, name, executablePath);

    private static KnownAppFamily Match((string Fragment, KnownAppFamily Family)[] table, string? name, string? executablePath)
    {
        var fileName = executablePath is null ? null : PathUtil.FileName(executablePath);
        foreach (var (fragment, family) in table)
        {
            if (Contains(name, fragment) || Contains(fileName, fragment)) return family;
        }
        return KnownAppFamily.None;
    }

    private static bool ContainsAny(string? text, string[] fragments)
        => text is not null && fragments.Any(f => text.Contains(f, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(string? text, string fragment)
        => text is not null && text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
