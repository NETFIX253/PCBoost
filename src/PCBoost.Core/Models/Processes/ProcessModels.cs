using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Models.Processes;

public enum SignatureStatus { Unknown = 0, Signed, Unsigned, Invalid, NotChecked }

public sealed record SignatureInfo(SignatureStatus Status, string? Signer)
{
    public static SignatureInfo NotChecked { get; } = new(SignatureStatus.NotChecked, null);
    public static SignatureInfo Unknown { get; } = new(SignatureStatus.Unknown, null);
    public bool IsMicrosoft => Status == SignatureStatus.Signed && Signer is not null
        && Signer.Contains("Microsoft", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Niveau de confiance affiché. PCBoost n'est pas un antivirus : il ne qualifie jamais un fichier de malveillant (§36).
/// </summary>
public enum TrustLevel { Unknown = 0, WindowsComponent, SignedPublisher, Unsigned, InvalidSignature }

public sealed record FileVersionMetadata(string? CompanyName, string? ProductName, string? FileDescription, string? FileVersion);

public sealed record TrustAssessment(
    TrustLevel Level,
    SignatureInfo Signature,
    string? Publisher,
    string? Description,
    bool IsInWindowsDirectory,
    bool IsInProgramFiles);

public enum ProtectionLevel
{
    /// <summary>Aucune protection particulière.</summary>
    None = 0,
    /// <summary>Composant système : PCBoost déconseille d'y toucher mais l'utilisateur peut fermer la fenêtre.</summary>
    Sensitive = 1,
    /// <summary>Processus critique : PCBoost refuse toute fermeture ou modification.</summary>
    Critical = 2,
}

public sealed record ProtectionInfo(ProtectionLevel Level, TextRef? Reason)
{
    public static ProtectionInfo None { get; } = new(ProtectionLevel.None, null);
    public bool IsCritical => Level == ProtectionLevel.Critical;
}

/// <summary>Processus enrichi pour l'affichage (gestionnaire de processus).</summary>
public sealed record ProcessInfo(
    int ProcessId,
    string Name,
    string? ExecutablePath,
    double CpuPercent,
    long MemoryBytes,
    double? DiskBytesPerSec,
    ProcessPriority Priority,
    bool HasWindow,
    string? WindowTitle,
    bool IsCurrentUser,
    int SessionId,
    DateTimeOffset? StartTime,
    TrustAssessment Trust,
    ProtectionInfo Protection);
