using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Services;
using PCBoost.Optimization.Common;

namespace PCBoost.Optimization.Processes;

/// <summary>
/// Évaluation de confiance d'un exécutable (§36) : signature Authenticode, métadonnées de version, emplacement.
/// « Composant Windows » = signé Microsoft ET situé sous %WINDIR%. Jamais de qualification « malveillant » :
/// au pire « Non signé » ou « Signature invalide ». Résultats mis en cache par chemin (30 min).
/// </summary>
public sealed class SecurityService : ISecurityService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private const int MaxCacheEntries = 4096;

    private readonly ISignatureVerifier _signatures;
    private readonly IFileMetadataProvider _metadata;
    private readonly IFileSystemProvider _fileSystem;
    private readonly IClock _clock;
    private readonly ILogger<SecurityService> _logger;
    private readonly ConcurrentDictionary<string, (TrustAssessment Assessment, DateTimeOffset At)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public SecurityService(ISignatureVerifier signatures, IFileMetadataProvider metadata, IFileSystemProvider fileSystem, IClock clock,
        ILogger<SecurityService>? logger = null)
    {
        _signatures = signatures;
        _metadata = metadata;
        _fileSystem = fileSystem;
        _clock = clock;
        _logger = logger ?? NullLogger<SecurityService>.Instance;
    }

    public TrustAssessment Assess(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return new TrustAssessment(TrustLevel.Unknown, SignatureInfo.Unknown, null, null, false, false);

        var path = executablePath.Trim().Trim('"');
        var now = _clock.UtcNow;
        if (_cache.TryGetValue(path, out var cached) && now - cached.At < CacheDuration)
            return cached.Assessment;

        var assessment = Evaluate(path);
        if (_cache.Count >= MaxCacheEntries) _cache.Clear();
        _cache[path] = (assessment, now);
        return assessment;
    }

    private TrustAssessment Evaluate(string path)
    {
        var inWindows = PathUtil.IsUnder(path, _fileSystem.GetKnownFolder(KnownFolder.WindowsDirectory));
        var inProgramFiles = PathUtil.IsUnder(path, _fileSystem.GetKnownFolder(KnownFolder.ProgramFiles))
                             || PathUtil.IsUnder(path, _fileSystem.GetKnownFolder(KnownFolder.ProgramFilesX86));
        try
        {
            if (!_fileSystem.FileExists(path))
                return new TrustAssessment(TrustLevel.Unknown, SignatureInfo.Unknown, null, null, inWindows, inProgramFiles);

            var signature = _signatures.Verify(path) ?? SignatureInfo.Unknown;
            var metadata = _metadata.GetVersionInfo(path);
            var description = FirstNonEmpty(metadata?.FileDescription, metadata?.ProductName);

            // L'éditeur n'est affiché que s'il est attesté par une signature valide (le champ « CompanyName » est déclaratif).
            var publisher = signature.Status == SignatureStatus.Signed ? FirstNonEmpty(signature.Signer, metadata?.CompanyName) : null;
            var level = signature.Status switch
            {
                SignatureStatus.Signed when signature.IsMicrosoft && inWindows => TrustLevel.WindowsComponent,
                SignatureStatus.Signed => TrustLevel.SignedPublisher,
                SignatureStatus.Unsigned => TrustLevel.Unsigned,
                SignatureStatus.Invalid => TrustLevel.InvalidSignature,
                _ => TrustLevel.Unknown,
            };
            return new TrustAssessment(level, signature, publisher, description, inWindows, inProgramFiles);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Évaluation de confiance impossible");
            return new TrustAssessment(TrustLevel.Unknown, SignatureInfo.Unknown, null, null, inWindows, inProgramFiles);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(v => v?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
}
