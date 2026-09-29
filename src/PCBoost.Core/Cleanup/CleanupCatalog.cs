using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Cleanup;

namespace PCBoost.Core.Cleanup;

/// <summary>
/// Emplacement nettoyable : dossier connu + sous-chemin relatif (un segment « * » autorisé pour les profils de navigateur),
/// filtres de noms de fichiers facultatifs. Aucun chemin arbitraire n'est accepté.
/// </summary>
public sealed record CleanupTargetSpec(
    KnownFolder Root,
    string RelativePath,
    bool Recursive = true,
    IReadOnlyList<string>? FilePatterns = null);

public sealed record CleanupCategoryDefinition(
    string Id,
    SafetyCategory Safety,
    bool RequiresElevation,
    bool SelectedByDefault,
    TimeSpan MinimumFileAge,
    IReadOnlyList<CleanupTargetSpec> Targets,
    // Processus qui doivent être fermés pour nettoyer (ex. navigateur).
    IReadOnlyList<string> BlockingProcesses,
    // Catégorie spéciale traitée par un fournisseur dédié (corbeille).
    bool IsRecycleBin = false)
{
    public CleanupCategory ToCategory() => new(
        Id,
        TextRef.Of($"Cleanup_{Id}_Name"),
        TextRef.Of($"Cleanup_{Id}_Description"),
        Safety,
        RequiresElevation,
        SelectedByDefault,
        MinimumFileAge);
}

/// <summary>
/// Catalogue fermé des catégories de nettoyage (§11). Partagé par le moteur de nettoyage et PCBoost.Elevator :
/// l'assistant élevé ne reçoit qu'un identifiant de catégorie, jamais un chemin.
/// Les documents, images, vidéos, téléchargements et fichiers inconnus ne figurent dans aucune catégorie.
/// </summary>
public static class CleanupCatalog
{
    public const string UserTemp = "user-temp";
    public const string WindowsTemp = "windows-temp";
    public const string InternetCache = "internet-cache";
    public const string UserErrorReports = "user-error-reports";
    public const string UserCrashDumps = "user-crash-dumps";
    public const string SystemErrorReports = "system-error-reports";
    public const string SystemCrashDumps = "system-crash-dumps";
    public const string RecycleBin = "recycle-bin";
    public const string ThumbnailCache = "thumbnail-cache";
    public const string EdgeCache = "browser-edge";
    public const string ChromeCache = "browser-chrome";
    public const string FirefoxCache = "browser-firefox";
    public const string ShaderCache = "directx-shader-cache";

    private static readonly TimeSpan OneDay = TimeSpan.FromDays(1);
    private static readonly TimeSpan OneWeek = TimeSpan.FromDays(7);

    public static IReadOnlyList<CleanupCategoryDefinition> All { get; } =
    [
        new(UserTemp, SafetyCategory.Safe, false, true, OneDay,
            [new(KnownFolder.UserTemp, "")], []),

        new(WindowsTemp, SafetyCategory.Safe, true, true, OneDay,
            [new(KnownFolder.WindowsTemp, "")], []),

        new(InternetCache, SafetyCategory.Safe, false, true, OneDay,
            [new(KnownFolder.LocalAppData, @"Microsoft\Windows\INetCache")], []),

        new(UserErrorReports, SafetyCategory.Safe, false, true, OneWeek,
            [
                new(KnownFolder.LocalAppData, @"Microsoft\Windows\WER\ReportArchive"),
                new(KnownFolder.LocalAppData, @"Microsoft\Windows\WER\ReportQueue"),
            ], []),

        new(UserCrashDumps, SafetyCategory.Safe, false, true, OneWeek,
            [new(KnownFolder.LocalAppData, "CrashDumps", Recursive: false, FilePatterns: ["*.dmp"])], []),

        new(SystemErrorReports, SafetyCategory.Safe, true, true, OneWeek,
            [
                new(KnownFolder.ProgramData, @"Microsoft\Windows\WER\ReportArchive"),
                new(KnownFolder.ProgramData, @"Microsoft\Windows\WER\ReportQueue"),
            ], []),

        new(SystemCrashDumps, SafetyCategory.Caution, true, false, OneWeek,
            [
                new(KnownFolder.WindowsDirectory, "Minidump", Recursive: false, FilePatterns: ["*.dmp"]),
                new(KnownFolder.WindowsDirectory, "", Recursive: false, FilePatterns: ["MEMORY.DMP"]),
            ], []),

        new(RecycleBin, SafetyCategory.Caution, false, false, TimeSpan.Zero, [], [], IsRecycleBin: true),

        new(ThumbnailCache, SafetyCategory.Caution, false, false, TimeSpan.Zero,
            [new(KnownFolder.LocalAppData, @"Microsoft\Windows\Explorer", Recursive: false, FilePatterns: ["thumbcache_*.db"])], []),

        new(EdgeCache, SafetyCategory.Caution, false, false, TimeSpan.Zero,
            [
                new(KnownFolder.LocalAppData, @"Microsoft\Edge\User Data\*\Cache\Cache_Data"),
                new(KnownFolder.LocalAppData, @"Microsoft\Edge\User Data\*\Code Cache"),
                new(KnownFolder.LocalAppData, @"Microsoft\Edge\User Data\*\GPUCache"),
            ], ["msedge.exe"]),

        new(ChromeCache, SafetyCategory.Caution, false, false, TimeSpan.Zero,
            [
                new(KnownFolder.LocalAppData, @"Google\Chrome\User Data\*\Cache\Cache_Data"),
                new(KnownFolder.LocalAppData, @"Google\Chrome\User Data\*\Code Cache"),
                new(KnownFolder.LocalAppData, @"Google\Chrome\User Data\*\GPUCache"),
            ], ["chrome.exe"]),

        new(FirefoxCache, SafetyCategory.Caution, false, false, TimeSpan.Zero,
            [new(KnownFolder.LocalAppData, @"Mozilla\Firefox\Profiles\*\cache2")], ["firefox.exe"]),

        new(ShaderCache, SafetyCategory.Advanced, false, false, OneDay,
            [new(KnownFolder.LocalAppData, "D3DSCache")], []),
    ];

    public static CleanupCategoryDefinition? Find(string id)
        => All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
}
