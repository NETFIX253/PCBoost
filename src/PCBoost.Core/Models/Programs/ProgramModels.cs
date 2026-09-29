using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Programs;

public enum ProgramScope { Machine = 0, User = 1 }

/// <summary>Origine de la date de dernière utilisation (toujours affichée avec la valeur).</summary>
public enum LastUseSource
{
    Unknown = 0,
    /// <summary>Dernier accès au fichier programme (mis à jour par Windows ; approximatif).</summary>
    FileAccess,
    /// <summary>Dernière exécution enregistrée par Windows (Prefetch), lue avec autorisation administrateur.</summary>
    WindowsPrefetch,
}

/// <summary>Commande officielle de désinstallation, validée (exécutable réel ou Windows Installer).</summary>
public sealed record UninstallCommand(string FileName, string Arguments, bool IsWindowsInstaller, string? ProductCode);

/// <summary>Programme installé proposé à la désinstallation assistée.</summary>
public sealed record InstalledProgram(
    string Id,
    string Name,
    string? Publisher,
    string? Version,
    DateOnly? InstallDate,
    long? SizeBytes,
    bool SizeFromRegistry,
    string? InstallLocation,
    ProgramScope Scope,
    string? MainExecutable,
    DateTimeOffset? LastUsed,
    LastUseSource LastUseSource,
    UninstallCommand? Uninstall)
{
    /// <summary>Au-delà de cette durée sans utilisation connue, le programme est signalé « peu utilisé ».</summary>
    public static readonly TimeSpan RarelyUsedAfter = TimeSpan.FromDays(90);

    public bool CanUninstall => Uninstall is not null;

    public bool IsRarelyUsed(DateTimeOffset now) => LastUsed is { } t && now - t > RarelyUsedAfter;
}

public sealed record ProgramInventory(DateTimeOffset ReadAt, IReadOnlyList<InstalledProgram> Programs, int HiddenCount, DateTimeOffset? LastRunReadAt);

public enum UninstallOutcome
{
    /// <summary>Le programme n'apparaît plus dans la liste de Windows.</summary>
    Removed = 0,
    /// <summary>Le programme de désinstallation s'est fermé, mais le programme est toujours installé (annulé ou encore en cours).</summary>
    StillInstalled,
    /// <summary>Windows n'a pas pu lancer le programme de désinstallation (autorisation refusée, fichier absent…).</summary>
    Failed,
}

public sealed record UninstallResult(UninstallOutcome Outcome, OperationResult? Error = null);
