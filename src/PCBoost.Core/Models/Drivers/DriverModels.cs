using PCBoost.Core.Common;
using PCBoost.Core.Drivers;
using PCBoost.Core.Models.Health;

namespace PCBoost.Core.Models.Drivers;

/// <summary>
/// Périphérique présent et pilote actuellement installé (Win32_PnPEntity + Win32_PnPSignedDriver). Les valeurs absentes
/// restent nulles : aucune version ni date n'est supposée.
/// </summary>
public sealed record InstalledDriver(
    string InstanceId,
    string DeviceName,
    string? DeviceClass,
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<string> CompatibleIds,
    string? Version,
    DateOnly? Date,
    string? Provider,
    string? InfName,
    int ProblemCode)
{
    /// <summary>Rang minimal d'une correspondance par identifiant compatible (toujours après les identifiants matériels).</summary>
    public const int CompatibleRankOffset = 1000;

    /// <summary>
    /// Rang de correspondance d'un identifiant matériel proposé par Windows Update : 0 = identifiant matériel le plus
    /// précis ; les identifiants compatibles viennent toujours après ; -1 = aucune correspondance.
    /// </summary>
    public int MatchRank(string? hardwareId)
    {
        if (string.IsNullOrWhiteSpace(hardwareId)) return -1;
        for (var i = 0; i < HardwareIds.Count; i++)
        {
            if (string.Equals(HardwareIds[i], hardwareId, StringComparison.OrdinalIgnoreCase)) return i;
        }
        for (var i = 0; i < CompatibleIds.Count; i++)
        {
            if (string.Equals(CompatibleIds[i], hardwareId, StringComparison.OrdinalIgnoreCase)) return CompatibleRankOffset + i;
        }
        return -1;
    }

    /// <summary>Aucun pilote n'est installé (code 28 du Gestionnaire de périphériques).</summary>
    public bool HasNoDriver => ProblemCode == 28;
}

/// <summary>Comportement de redémarrage annoncé par Windows Update.</summary>
public enum DriverRebootBehavior { Never = 0, Always = 1, Possible = 2 }

/// <summary>
/// Mise à jour de pilote proposée par Windows Update pour ce PC : catalogue Microsoft, pilotes signés et validés (WHQL),
/// ciblés sur les identifiants matériels présents. <see cref="IsOptional"/> = « mise à jour facultative » de Windows
/// (non installée automatiquement par Windows).
/// </summary>
public sealed record DriverUpdateOffer(
    string UpdateId,
    int Revision,
    string Title,
    string? DriverClass,
    string? HardwareId,
    string? Manufacturer,
    string? Model,
    string? Provider,
    DateOnly? DriverDate,
    string? Version,
    bool IsOptional,
    DateTimeOffset? PublishedAt,
    long? DownloadBytes,
    bool EulaAccepted,
    bool RequiresUserInput,
    DriverRebootBehavior RebootBehavior);

/// <summary>Classement d'une mise à jour de pilote par la politique de PCBoost.</summary>
public enum DriverUpdateTier
{
    /// <summary>Recommandée par Windows, publiée depuis assez longtemps, plus récente que le pilote installé : présélectionnée.</summary>
    Recommended = 0,
    /// <summary>Installable, mais jamais présélectionnée (facultative, récente, pilote de démarrage, version non comparable).</summary>
    Review = 1,
    /// <summary>Jamais installée par PCBoost (microprogramme, version plus ancienne, périphérique introuvable, interaction ou licence requise).</summary>
    Excluded = 2,
}

/// <summary>Raisons affichées à côté d'une mise à jour de pilote.</summary>
public enum DriverUpdateReason
{
    /// <summary>Mise à jour facultative de Windows : utile surtout pour corriger un problème précis.</summary>
    Optional = 0,
    /// <summary>Publiée depuis moins de <see cref="Drivers.DriverUpdatePolicy.MaturityPeriod"/>.</summary>
    RecentlyPublished,
    /// <summary>Pilote essentiel au démarrage ou à la sécurité de Windows (contrôleur de disque, processeur, TPM…).</summary>
    SystemCritical,
    /// <summary>La version installée n'est pas comparable (éditeur différent, extension, date inconnue).</summary>
    VersionNotComparable,
    /// <summary>Aucun périphérique présent ne correspond à l'identifiant matériel proposé.</summary>
    DeviceNotFound,
    /// <summary>Microprogramme (BIOS/UEFI, firmware) : jamais installé par PCBoost.</summary>
    Firmware,
    /// <summary>La version installée est identique ou plus récente : jamais de retour à une version antérieure.</summary>
    NotNewer,
    /// <summary>L'installation demande une interaction : à faire depuis Windows Update.</summary>
    RequiresUserInput,
    /// <summary>Conditions de licence à accepter dans Windows Update : PCBoost ne les accepte pas à la place de l'utilisateur.</summary>
    LicenseNotAccepted,
    /// <summary>Information : le périphérique signale actuellement un problème (code du Gestionnaire de périphériques).</summary>
    DeviceHasProblem,
}

/// <summary>Mise à jour de pilote évaluée : périphériques concernés, classement et raisons.</summary>
public sealed record DriverUpdateCandidate(
    DriverUpdateOffer Offer,
    IReadOnlyList<InstalledDriver> Devices,
    DriverUpdateTier Tier,
    IReadOnlyList<DriverUpdateReason> Reasons)
{
    /// <summary>Périphérique principal (meilleure correspondance), s'il existe.</summary>
    public InstalledDriver? Device => Devices.Count > 0 ? Devices[0] : null;

    public bool SelectedByDefault => Tier == DriverUpdateTier.Recommended;

    public bool CanInstall => Tier != DriverUpdateTier.Excluded;

    public bool Has(DriverUpdateReason reason) => Reasons.Contains(reason);
}

/// <summary>État de la recherche de pilotes.</summary>
public enum DriverScanState
{
    Ready = 0,
    /// <summary>Une stratégie de l'organisation exclut les pilotes de Windows Update : PCBoost la respecte.</summary>
    ExcludedByPolicy,
    /// <summary>Le service Windows Update est désactivé : PCBoost ne le modifie pas.</summary>
    UpdateServiceDisabled,
    /// <summary>La recherche a échoué (pas de connexion, serveur indisponible…).</summary>
    SearchFailed,
}

/// <summary>Protection du système (points de restauration) du lecteur Windows.</summary>
public enum SystemProtectionState
{
    Unknown = 0,
    Enabled,
    Disabled,
    /// <summary>Désactivée par une stratégie de l'organisation : PCBoost ne la réactive pas et n'installe aucun pilote.</summary>
    DisabledByPolicy,
}

public sealed record DriverScanResult(
    DriverScanState State,
    IReadOnlyList<DriverUpdateCandidate> Candidates,
    DateTimeOffset ScannedAt,
    bool RebootPending,
    bool InstallerBusy,
    bool ManagedUpdateServer,
    SystemProtectionState Protection,
    OperationResult? Error,
    ComputerIdentity? Computer = null,
    IReadOnlyList<OfficialDriverSource>? OfficialSources = null)
{
    /// <summary>
    /// Installation possible : recherche réussie, liste des périphériques lue (sinon <see cref="Error"/> est renseigné et
    /// rien ne peut être vérifié), aucun redémarrage en attente, restauration du système autorisée.
    /// </summary>
    public bool CanInstall => State == DriverScanState.Ready && Error is null && !RebootPending && Protection != SystemProtectionState.DisabledByPolicy;

    /// <summary>Sources officielles complémentaires (fabricants du PC et de la carte graphique), jamais installées par PCBoost.</summary>
    public IReadOnlyList<OfficialDriverSource> Sources => OfficialSources ?? [];
}

/// <summary>Point de restauration créé à la demande (avant l'utilisation de l'outil d'un fabricant).</summary>
public sealed record DriverRestorePointResult(OperationResult Outcome, RestorePointStatus Status, DateTimeOffset? CreatedAt, bool ProtectionEnabled)
{
    public bool IsCreated => Status == RestorePointStatus.Created;
}

/// <summary>Résultat de l'installation d'une mise à jour de pilote.</summary>
public enum DriverInstallStatus
{
    Installed = 0,
    Failed,
    /// <summary>Non exécutée : l'installation s'est arrêtée avant (point de restauration impossible, problème détecté…).</summary>
    NotRun,
    /// <summary>Windows Update ne la propose plus (déjà installée, remplacée).</summary>
    NoLongerOffered,
    /// <summary>Refusée par la politique de PCBoost lors de la revérification (version installée plus récente, microprogramme…).</summary>
    Refused,
    /// <summary>
    /// Résultat inconnu : l'assistant administrateur n'a rendu aucun résultat (délai dépassé, arrêt inattendu). La
    /// modification reste « en attente » dans le journal : le retour au pilote précédent reste proposé.
    /// </summary>
    Unknown,
}

public sealed record DriverInstallOutcome(
    string UpdateId,
    DriverInstallStatus Status,
    int HResult,
    bool RebootRequired,
    string? NewVersion,
    int? ProblemCodeAfter);

/// <summary>Raison d'un arrêt avant ou pendant l'installation.</summary>
public enum DriverInstallStop
{
    None = 0,
    /// <summary>Le point de restauration n'a pas pu être créé : aucun pilote n'est installé.</summary>
    RestorePointUnavailable,
    /// <summary>La protection du système est désactivée et son activation n'a pas été demandée.</summary>
    ProtectionDisabled,
    /// <summary>Un redémarrage est en attente : à faire avant d'installer des pilotes.</summary>
    RebootPending,
    /// <summary>Windows Update installe déjà des mises à jour.</summary>
    InstallerBusy,
    /// <summary>La recherche Windows Update de l'assistant administrateur a échoué.</summary>
    SearchFailed,
    /// <summary>Un périphérique signale un problème après une installation : les suivantes ne sont pas faites.</summary>
    DeviceProblem,
    /// <summary>Une stratégie de l'organisation exclut les pilotes de Windows Update : rien n'est installé.</summary>
    ExcludedByPolicy,
    /// <summary>Le service Windows Update est désactivé : PCBoost ne le modifie pas et n'installe rien.</summary>
    UpdateServiceDisabled,
    /// <summary>
    /// La liste des périphériques n'a pas pu être lue (avant l'installation, ou pour vérifier la précédente) : sans état
    /// connu, aucune installation (supplémentaire) n'est faite.
    /// </summary>
    DeviceReadFailed,
}

public sealed record DriverInstallResult(
    OperationResult Outcome,
    DriverInstallStop Stop,
    RestorePointStatus RestorePoint,
    DateTimeOffset? RestorePointAt,
    bool ProtectionEnabled,
    IReadOnlyList<DriverInstallOutcome> Drivers,
    bool RebootRequired,
    Guid? SessionId)
{
    public int InstalledCount => Drivers.Count(d => d.Status == DriverInstallStatus.Installed);

    public int FailedCount => Drivers.Count(d => d.Status is DriverInstallStatus.Failed);

    public static DriverInstallResult NotStarted(OperationResult outcome, DriverInstallStop stop = DriverInstallStop.None)
        => new(outcome, stop, RestorePointStatus.Failed, null, false, [], false, null);
}

/// <summary>
/// Étapes de l'installation, dans l'ordre (progression transmise par l'assistant administrateur) : revérification d'abord,
/// point de restauration seulement s'il reste une mise à jour à installer.
/// </summary>
public enum DriverInstallStage { Searching = 0, RestorePoint, Downloading, Installing, Verifying }

public sealed record DriverInstallProgress(DriverInstallStage Stage, int Index, int Total);

/// <summary>Résultat du retour au pilote précédent pour un périphérique.</summary>
public enum DriverRollbackStatus
{
    /// <summary>Le pilote précédent est réinstallé (« Restaurer le pilote » de Windows).</summary>
    RolledBack = 0,
    /// <summary>Le pilote précédent était déjà en place.</summary>
    AlreadyPrevious,
    /// <summary>Le périphérique n'est plus présent : rien n'est fait (le rebrancher puis réessayer).</summary>
    DeviceGone,
    /// <summary>
    /// Le pilote en place n'est ni le précédent ni celui installé par PCBoost (modifié depuis) : il n'est pas remplacé,
    /// pour ne jamais annuler une autre mise à jour.
    /// </summary>
    Changed,
    /// <summary>Échec (y compris liste des périphériques illisible : rien n'est tenté sans état connu).</summary>
    Failed,
}

public sealed record DriverRollbackOutcome(string InstanceId, DriverRollbackStatus Status, string? VersionAfter, int Win32Error)
{
    /// <summary>Le pilote précédent est en place (réinstallé, ou déjà là).</summary>
    public bool IsRestored => Status is DriverRollbackStatus.RolledBack or DriverRollbackStatus.AlreadyPrevious;
}

/// <summary>Périphérique à ramener à son pilote précédent : version en place avant la mise à jour.</summary>
public sealed record DriverRollbackTarget(string InstanceId, string PreviousVersion);

/// <summary>Pilote en place sur un périphérique juste avant la mise à jour (relu au moment de l'installation).</summary>
public sealed record DriverDeviceState(
    string InstanceId,
    string? PreviousVersion,
    DateOnly? PreviousDate,
    string? PreviousProvider,
    string? PreviousInfName);

/// <summary>
/// État « avant » d'une mise à jour de pilote, conservé dans le journal de restauration : version installée par la mise à
/// jour et, pour chaque périphérique concerné, le pilote précédent. Le retour ciblé ne vise que les périphériques dont la
/// version précédente est connue, et seulement s'ils sont encore sur <see cref="NewVersion"/>.
/// </summary>
public sealed record DriverUpdateState(
    string UpdateId,
    string Title,
    string DeviceName,
    string? DriverClass,
    string? NewVersion,
    IReadOnlyList<DriverDeviceState> Devices)
{
    /// <summary>Périphériques concernés par « Restaurer le pilote » : identifiant et version précédente valides.</summary>
    public IReadOnlyList<DriverRollbackTarget> RollbackTargets()
        => (Devices ?? [])
            .Where(d => DriverIdentifiers.IsValidInstanceId(d.InstanceId) && DriverIdentifiers.IsValidVersion(d.PreviousVersion))
            .DistinctBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Take(DriverUpdatePolicy.MaxDevicesPerUpdate)
            .Select(d => new DriverRollbackTarget(d.InstanceId, d.PreviousVersion!))
            .ToList();
}

/// <summary>État « avant » de l'activation de la protection du système (consignée à l'Historique).</summary>
public sealed record SystemProtectionChangeState(SystemProtectionState Before);
