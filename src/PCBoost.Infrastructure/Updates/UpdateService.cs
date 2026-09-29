using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Mises à jour (§51) : interroge les sources dans l'ordre d'enregistrement, copie le paquet dans le dossier de
/// téléchargement puis vérifie son empreinte SHA-256 (échec → fichier supprimé). Seul un .msi vérifié par cette instance,
/// situé dans ce dossier, peut être installé ; son empreinte est recontrôlée juste avant le lancement.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly IUpdateProvider[] _providers;
    private readonly IAppInfo _appInfo;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _downloadDirectory;
    private readonly ConcurrentDictionary<string, IUpdateProvider> _sourceByPackage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _verifiedPackages = new(PathContainment.Comparer);

    public UpdateService(IEnumerable<IUpdateProvider> providers, IAppInfo appInfo, UpdateOptions options, ILogger<UpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(options);
        _providers = providers.ToArray();
        _appInfo = appInfo ?? throw new ArgumentNullException(nameof(appInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _downloadDirectory = options.ResolveDownloadDirectory(appInfo.DataDirectory);
    }

    /// <summary>Dossier où les paquets sont copiés et vérifiés.</summary>
    public string DownloadDirectory => _downloadDirectory;

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        UpdateCheckResult? upToDate = null;
        UpdateCheckResult? failed = null;
        foreach (var provider in _providers)
        {
            UpdateCheckResult result;
            try
            {
                result = await provider.CheckAsync(_appInfo.Version, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "La source de mise à jour « {Provider} » a échoué.", provider.Name);
                result = new UpdateCheckResult(UpdateCheckStatus.Failed, null, TextRef.Of("Error_" + OperationResult.ClassifyException(ex)));
            }

            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable when result.Update is not null:
                    _sourceByPackage[result.Update.PackageLocation] = provider;
                    return result;
                case UpdateCheckStatus.UpToDate:
                    upToDate ??= result;
                    break;
                case UpdateCheckStatus.Failed:
                case UpdateCheckStatus.UpdateAvailable:
                    failed ??= result.Status == UpdateCheckStatus.Failed
                        ? result
                        : new UpdateCheckResult(UpdateCheckStatus.Failed, null, TextRef.Of("Infra_Update_FeedInvalid"));
                    break;
            }
        }
        return upToDate ?? failed ?? new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, TextRef.Of("Infra_Update_NotConfigured"));
    }

    public async Task<UpdateDownloadResult> DownloadAndVerifyAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!Sha256Hash.IsValid(update.Sha256))
            return Failure(OperationErrorKind.InvalidInput, "Infra_Update_FeedInvalid");

        IReadOnlyList<IUpdateProvider> candidates = _sourceByPackage.TryGetValue(update.PackageLocation, out var known) ? [known] : _providers;
        UpdateDownloadResult? lastFailure = null;
        UpdateDownloadResult? download = null;
        foreach (var provider in candidates)
        {
            UpdateDownloadResult result;
            try
            {
                result = await provider.DownloadAsync(update, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "La source de mise à jour « {Provider} » n'a pas pu fournir le paquet.", provider.Name);
                result = new UpdateDownloadResult(OperationResult.FromException(ex), null);
            }

            if (result.Outcome.Success && result.LocalPath is not null)
            {
                download = result;
                break;
            }
            if (result.Outcome.Error != OperationErrorKind.NotSupported || lastFailure is null) lastFailure = result;
        }

        if (download?.LocalPath is null)
            return lastFailure ?? Failure(OperationErrorKind.NotSupported, "Infra_Update_NotConfigured");

        var localPath = Path.GetFullPath(download.LocalPath);
        if (!PathContainment.IsInside(localPath, _downloadDirectory))
        {
            // Fichier hors du dossier de téléchargement : il n'appartient pas à l'application, il n'est ni vérifié ni supprimé.
            _logger.LogWarning("Paquet de mise à jour refusé : il n'a pas été copié dans le dossier de téléchargement.");
            return Failure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");
        }

        string actual;
        try
        {
            actual = await Sha256Hash.ComputeAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(localPath);
            _logger.LogWarning(ex, "Impossible de lire le paquet de mise à jour pour le vérifier.");
            return new UpdateDownloadResult(OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_CopyFailed") }, null);
        }

        if (!Sha256Hash.Matches(actual, update.Sha256))
        {
            TryDelete(localPath);
            _verifiedPackages.TryRemove(localPath, out _);
            _logger.LogWarning("Empreinte SHA-256 du paquet de mise à jour incorrecte : paquet supprimé, rien n'a été installé.");
            return new UpdateDownloadResult(
                OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_HashMismatch"),
                    $"SHA-256 attendu {update.Sha256.ToUpperInvariant()}, obtenu {actual}"),
                null);
        }

        _verifiedPackages[localPath] = actual;
        _logger.LogInformation("Paquet de mise à jour {Version} vérifié (SHA-256).", UpdateVersions.Display(update.Version));
        return new UpdateDownloadResult(OperationResult.Ok(TextRef.Of("Infra_Update_Verified")), localPath);
    }

    /// <summary>
    /// Lance <c>msiexec.exe /i "&lt;paquet&gt;"</c> (chemin complet de msiexec dans System32, arguments passés via
    /// <see cref="ProcessStartInfo.ArgumentList"/>, sans shell). Windows Installer exécute l'installation dans une
    /// transaction : si elle échoue ou est annulée, les actions déjà effectuées sont annulées (rollback MSI) et la version
    /// actuellement installée reste intacte. La mise à niveau majeure (WiX MajorUpgrade) ne retire l'ancienne version
    /// qu'au sein de cette même transaction. L'appelant doit ensuite fermer l'application pour libérer ses fichiers
    /// (le Gestionnaire de redémarrage de Windows Installer le demande sinon).
    /// </summary>
    public OperationResult LaunchInstaller(string verifiedPackagePath)
    {
        if (string.IsNullOrWhiteSpace(verifiedPackagePath))
            return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Infra_Update_InvalidPackage"));

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(verifiedPackagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Infra_Update_InvalidPackage"));
        }

        if (!PathContainment.IsInside(fullPath, _downloadDirectory)
            || !string.Equals(Path.GetExtension(fullPath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Lancement d'installation refusé : seul un paquet .msi du dossier de mises à jour est accepté.");
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage"));
        }
        if (!File.Exists(fullPath))
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Infra_Update_PackageNotFound"));
        if (!_verifiedPackages.TryGetValue(fullPath, out var expectedHash))
        {
            _logger.LogWarning("Lancement d'installation refusé : paquet non vérifié par l'application.");
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage"));
        }

        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage"));

            // Le fichier reste ouvert en lecture partagée (écriture et suppression refusées) du contrôle jusqu'au lancement.
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Sha256Hash.Matches(Sha256Hash.Compute(stream), expectedHash))
            {
                stream.Dispose();
                _verifiedPackages.TryRemove(fullPath, out _);
                TryDelete(fullPath);
                _logger.LogWarning("Le paquet de mise à jour a été modifié après sa vérification : supprimé, rien n'a été installé.");
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_HashMismatch"));
            }

            if (!OperatingSystem.IsWindows())
                return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Error_NotSupported"));

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("/i");
            startInfo.ArgumentList.Add(fullPath);

            using var process = Process.Start(startInfo);
            if (process is null)
                return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_InstallerFailed"));

            _logger.LogInformation("Installation de la mise à jour lancée (msiexec, PID {ProcessId}).", process.Id);
            return OperationResult.Ok(TextRef.Of("Infra_Update_InstallerStarted"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Le programme d'installation de la mise à jour n'a pas pu être lancé.");
            return OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_InstallerFailed") };
        }
    }

    private static UpdateDownloadResult Failure(OperationErrorKind kind, string messageKey)
        => new(OperationResult.Fail(kind, TextRef.Of(messageKey)), null);

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Le paquet de mise à jour refusé n'a pas pu être supprimé.");
        }
    }
}
