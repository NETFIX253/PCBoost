using System.Text.RegularExpressions;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Core.Drivers;

/// <summary>Comparaison d'une mise à jour proposée avec le pilote installé.</summary>
public enum DriverVersionComparison
{
    /// <summary>Plus récente que le pilote installé (ou aucun pilote installé).</summary>
    Newer = 0,
    /// <summary>Identique ou plus ancienne : jamais installée.</summary>
    NotNewer,
    /// <summary>Impossible à comparer de façon fiable.</summary>
    Unknown,
}

/// <summary>
/// Politique de PCBoost pour les mises à jour de pilotes (« la stabilité passe avant la performance ») :
/// <list type="bullet">
/// <item>seule source : Windows Update (pilotes signés et validés par Microsoft) — jamais de site tiers ;</item>
/// <item>jamais de microprogramme (BIOS/UEFI) ni de version identique ou plus ancienne que celle installée ;</item>
/// <item>jamais de mise à jour sans périphérique présent identifié (son état « avant » ne pourrait pas être noté) ;</item>
/// <item>présélection limitée aux mises à jour recommandées par Windows, publiées depuis au moins <see cref="MaturityPeriod"/> ;</item>
/// <item>mises à jour facultatives, récentes, pilotes de démarrage ou de sécurité, versions non comparables : affichées
/// « à examiner », jamais cochées d'office.</item>
/// </list>
/// Appliquée à l'affichage puis de nouveau par l'assistant administrateur juste avant l'installation.
/// </summary>
public static partial class DriverUpdatePolicy
{
    /// <summary>Délai depuis la publication sur Windows Update avant présélection (versions éprouvées).</summary>
    public static readonly TimeSpan MaturityPeriod = TimeSpan.FromDays(14);

    /// <summary>Nombre maximal de périphériques rattachés à une mise à jour (restauration ciblée).</summary>
    public const int MaxDevicesPerUpdate = 8;

    /// <summary>Classes jamais installées : microprogrammes (BIOS/UEFI, capsules de mise à jour).</summary>
    private static readonly string[] FirmwareClasses = ["Firmware"];

    /// <summary>
    /// Classes qui complètent un autre pilote (extension, composant logiciel) : « Restaurer le pilote » de Windows vise le
    /// pilote principal du périphérique, pas ce paquet ; seul le point de restauration permet d'y revenir.
    /// </summary>
    private static readonly string[] AddOnClasses = ["Extension", "SoftwareComponent"];

    /// <summary>Code 14 du Gestionnaire de périphériques : redémarrage nécessaire (attendu après certaines mises à jour).</summary>
    public const int ProblemNeedsRestart = 14;

    /// <summary>Classes essentielles au démarrage ou à la sécurité : contrôleurs de stockage, disques, processeur, HAL, TPM.</summary>
    private static readonly string[] SystemCriticalClasses =
        ["HDC", "SCSIAdapter", "DiskDrive", "Volume", "VolumeSnapshot", "Processor", "Computer", "SecurityDevices"];

    /// <summary>
    /// Évalue une mise à jour : périphériques concernés (meilleure correspondance d'abord), classement et raisons.
    /// </summary>
    public static DriverUpdateCandidate Evaluate(DriverUpdateOffer offer, IReadOnlyList<InstalledDriver> installed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(installed);
        var devices = MatchDevices(offer, installed);
        // Microprogramme : toujours exclu, sans autre raison (la comparaison avec un pilote n'aurait pas de sens).
        if (IsFirmware(offer)) return new DriverUpdateCandidate(offer, devices, DriverUpdateTier.Excluded, [DriverUpdateReason.Firmware]);
        var device = devices.Count > 0 ? devices[0] : null;
        var excluded = new List<DriverUpdateReason>();
        var review = new List<DriverUpdateReason>();
        var notes = new List<DriverUpdateReason>();

        if (offer.RequiresUserInput) excluded.Add(DriverUpdateReason.RequiresUserInput);
        if (!offer.EulaAccepted) excluded.Add(DriverUpdateReason.LicenseNotAccepted);

        if (device is null)
        {
            // Aucun périphérique identifié : ni pertinence vérifiable, ni état « avant » à noter → jamais installée par PCBoost.
            excluded.Add(DriverUpdateReason.DeviceNotFound);
        }
        else
        {
            switch (Compare(offer, device))
            {
                case DriverVersionComparison.NotNewer:
                    excluded.Add(DriverUpdateReason.NotNewer);
                    break;
                case DriverVersionComparison.Unknown:
                    review.Add(DriverUpdateReason.VersionNotComparable);
                    break;
            }
            if (devices.Any(d => d.ProblemCode != 0)) notes.Add(DriverUpdateReason.DeviceHasProblem);
        }

        if (offer.IsOptional) review.Add(DriverUpdateReason.Optional);
        if (IsRecentlyPublished(offer, now)) review.Add(DriverUpdateReason.RecentlyPublished);
        if (IsSystemCritical(offer, device)) review.Add(DriverUpdateReason.SystemCritical);

        var tier = excluded.Count > 0 ? DriverUpdateTier.Excluded : review.Count > 0 ? DriverUpdateTier.Review : DriverUpdateTier.Recommended;
        return new DriverUpdateCandidate(offer, devices, tier, [.. excluded, .. review, .. notes]);
    }

    /// <summary>
    /// Périphériques présents correspondant à l'identifiant matériel proposé : identifiants matériels d'abord (du plus
    /// précis au plus général), puis identifiants compatibles ; au plus <see cref="MaxDevicesPerUpdate"/>.
    /// </summary>
    public static IReadOnlyList<InstalledDriver> MatchDevices(DriverUpdateOffer offer, IReadOnlyList<InstalledDriver> installed)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(installed);
        if (string.IsNullOrWhiteSpace(offer.HardwareId)) return [];
        return installed
            .Select(d => (Device: d, Rank: d.MatchRank(offer.HardwareId)))
            .Where(x => x.Rank >= 0)
            .OrderBy(x => x.Rank)
            .ThenBy(x => x.Device.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Device)
            .Take(MaxDevicesPerUpdate)
            .ToList();
    }

    /// <summary>
    /// Compare avec le pilote installé : même éditeur → numéro de version (sinon date) ; éditeurs différents → date du
    /// pilote (les numéros de version de deux éditeurs ne sont pas comparables) ; aucun pilote installé → plus récente.
    /// Une extension (classe « Extension ») complète le pilote principal : sa version ne lui est pas comparable.
    /// </summary>
    public static DriverVersionComparison Compare(DriverUpdateOffer offer, InstalledDriver device)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(device);
        if (device.HasNoDriver && device.Version is null) return DriverVersionComparison.Newer;
        if (string.Equals(offer.DriverClass, "Extension", StringComparison.OrdinalIgnoreCase)) return DriverVersionComparison.Unknown;

        if (SameProvider(offer.Provider, device.Provider)
            && DriverIdentifiers.TryParseVersion(offer.Version, out var offered)
            && DriverIdentifiers.TryParseVersion(device.Version, out var current))
        {
            return offered > current ? DriverVersionComparison.Newer : DriverVersionComparison.NotNewer;
        }

        if (offer.DriverDate is { } offeredDate && device.Date is { } currentDate)
        {
            if (offeredDate > currentDate) return DriverVersionComparison.Newer;
            if (offeredDate < currentDate) return DriverVersionComparison.NotNewer;
        }
        return DriverVersionComparison.Unknown;
    }

    /// <summary>
    /// Retour ciblé possible après installation (« Restaurer le pilote ») : pilote principal (pas une extension ni un
    /// composant logiciel), version installée connue, et CHAQUE périphérique concerné avec un pilote précédent de version
    /// connue — sinon un périphérique resterait sur le nouveau pilote alors que l'annulation serait annoncée réussie.
    /// Dans les autres cas, seul le point de restauration Windows créé avant l'installation permet de revenir en arrière.
    /// </summary>
    public static bool SupportsTargetedRollback(DriverUpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return !IsAddOn(candidate.Offer)
               && DriverIdentifiers.IsValidVersion(candidate.Offer.Version)
               && candidate.Devices.Count > 0
               && candidate.Devices.All(d => DriverIdentifiers.IsValidInstanceId(d.InstanceId) && DriverIdentifiers.IsValidVersion(d.Version) && !d.HasNoDriver);
    }

    /// <summary>
    /// Plusieurs mises à jour visent-elles un même périphérique ? Elles sont alors installées une à la fois : l'état
    /// « avant » de la seconde (et sa revérification) dépend du résultat de la première.
    /// </summary>
    public static bool SharesDevice(IEnumerable<DriverUpdateCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            var instances = candidate.Devices.Select(d => d.InstanceId).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (instances.Any(seen.Contains)) return true;
            seen.UnionWith(instances);
        }
        return false;
    }

    public static bool IsAddOn(DriverUpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return AddOnClasses.Any(c => string.Equals(c, offer.DriverClass, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Nouveau problème signalé par un périphérique après une installation (null si aucun) : code non nul différent de
    /// celui d'avant (ex. 28 « aucun pilote » → 43), hors code 14 (redémarrage nécessaire), attendu quand Windows demande
    /// de redémarrer.
    /// </summary>
    public static int? NewProblemCode(int before, int after)
        => after != 0 && after != ProblemNeedsRestart && after != before ? after : null;

    /// <summary>
    /// Raison de NE PAS toucher un périphérique lors du retour au pilote précédent (règle appliquée par l'application puis
    /// par l'assistant administrateur), ou null s'il faut le ramener : absent → <see cref="DriverRollbackStatus.DeviceGone"/> ;
    /// déjà sur la version précédente → <see cref="DriverRollbackStatus.AlreadyPrevious"/> ; sur une autre version que
    /// celle installée par PCBoost → <see cref="DriverRollbackStatus.Changed"/> (une autre mise à jour n'est jamais annulée).
    /// </summary>
    public static DriverRollbackStatus? RollbackSkip(DriverRollbackTarget target, InstalledDriver? device, string installedVersion)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (device is null) return DriverRollbackStatus.DeviceGone;
        if (SameVersion(device.Version, target.PreviousVersion)) return DriverRollbackStatus.AlreadyPrevious;
        if (!SameVersion(device.Version, installedVersion)) return DriverRollbackStatus.Changed;
        return null;
    }

    /// <summary>Versions identiques (« 31.0.101.2125 » = « 31.0.101.2125.0 ») ; false si l'une est absente ou invalide.</summary>
    public static bool SameVersion(string? a, string? b)
        => DriverIdentifiers.TryParseVersion(a, out var first) && DriverIdentifiers.TryParseVersion(b, out var second) && first == second;

    /// <summary>Microprogramme : classe Firmware, ou titre/modèle évoquant un BIOS, un UEFI ou un firmware.</summary>
    public static bool IsFirmware(DriverUpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (FirmwareClasses.Any(c => string.Equals(c, offer.DriverClass, StringComparison.OrdinalIgnoreCase))) return true;
        return FirmwareWords().IsMatch(offer.Title) || (offer.Model is not null && FirmwareWords().IsMatch(offer.Model));
    }

    public static bool IsSystemCritical(DriverUpdateOffer offer, InstalledDriver? device)
    {
        ArgumentNullException.ThrowIfNull(offer);
        return IsCriticalClass(offer.DriverClass) || IsCriticalClass(device?.DeviceClass);
    }

    public static bool IsRecentlyPublished(DriverUpdateOffer offer, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(offer);
        // Date inconnue : pas de pénalité (Windows Update renseigne cette date pour ses mises à jour publiées).
        return offer.PublishedAt is { } published && published > now - MaturityPeriod;
    }

    /// <summary>
    /// Même éditeur : premier mot alphanumérique identique (« Intel » = « Intel Corporation », « Realtek » =
    /// « Realtek Semiconductor Corp. »).
    /// </summary>
    public static bool SameProvider(string? a, string? b)
    {
        var first = FirstWord(a);
        var second = FirstWord(b);
        return first.Length > 0 && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    }

    private static string FirstWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var match = WordRegex().Match(value);
        return match.Success ? match.Value : string.Empty;
    }

    private static bool IsCriticalClass(string? deviceClass)
        => deviceClass is not null && SystemCriticalClasses.Any(c => string.Equals(c, deviceClass, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"\b(BIOS|UEFI|Firmware|Capsule)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareWords();

    [GeneratedRegex(@"[A-Za-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
