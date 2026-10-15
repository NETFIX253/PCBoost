using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Mises à jour (§51) : interroge les sources dans l'ordre d'enregistrement (flux local, puis GitHub) — chacune ne
/// propose qu'une version décrite par un manifeste signé par une clé de <see cref="UpdateTrust"/> —, copie ou télécharge
/// le paquet dans le dossier des mises à jour puis vérifie sa taille et son empreinte SHA-256 (échec → fichier supprimé).
/// Seul un .msi vérifié par cette instance, situé dans ce dossier, peut être installé ; son empreinte est recontrôlée
/// juste avant le lancement, le paquet restant verrouillé en écriture entre ce contrôle et le lancement.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    private readonly IUpdateProvider[] _providers;
    private readonly IAppInfo _appInfo;
    private readonly ILogger<UpdateService> _logger;
    private readonly UpdateOptions _options;
    private readonly string _downloadDirectory;
    private readonly ConcurrentDictionary<string, IUpdateProvider> _sourceByPackage = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, VerifiedPackage> _verifiedPackages = new(PathContainment.Comparer);

    private sealed record VerifiedPackage(string Sha256, Version Version);

    public UpdateService(IEnumerable<IUpdateProvider> providers, IAppInfo appInfo, UpdateOptions options, ILogger<UpdateService> logger)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _options = options ?? throw new ArgumentNullException(nameof(options));
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

        return await VerifyLocalPackageAsync(update, localPath, deleteOnMismatch: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<UpdateDownloadResult> VerifyDownloadedAsync(UpdateInfo update, string packagePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (string.IsNullOrWhiteSpace(packagePath)) return Failure(OperationErrorKind.InvalidInput, "Infra_Update_InvalidPackage");

        // Authenticité d'abord : le manifeste conservé doit toujours porter une signature valide et décrire ce paquet.
        var manifest = SignedUpdateManifest.Verify(update.SignedManifest, _options.TrustedKeys, UpdateSourceRules.Local);
        if (!manifest.Success || manifest.Value is not { } verified)
        {
            _logger.LogWarning("Paquet de mise à jour en attente refusé : manifeste signé absent ou invalide ({Detail}).", manifest.TechnicalDetail);
            return Failure(OperationErrorKind.Blocked, "Infra_Update_SignatureInvalid");
        }
        string localPath;
        try
        {
            localPath = Path.GetFullPath(packagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(OperationErrorKind.InvalidInput, "Infra_Update_InvalidPackage");
        }
        var package = verified.PackageFor(SignedUpdateManifest.CurrentArchitecture);
        if (package is null || UpdateVersions.Normalize(verified.Version) != UpdateVersions.Normalize(update.Version)
            || !Sha256Hash.Matches(package.Sha256, update.Sha256)
            || !string.Equals(Path.GetFileName(localPath), package.FileName, StringComparison.OrdinalIgnoreCase)
            || !PathContainment.IsInside(localPath, _downloadDirectory))
        {
            return Failure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");
        }
        if (!File.Exists(localPath)) return Failure(OperationErrorKind.NotFound, "Infra_Update_PackageNotFound");
        return await VerifyLocalPackageAsync(update with { Size = package.Size }, localPath, deleteOnMismatch: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Taille (si connue) puis SHA-256 du paquet ; en cas de réussite, le paquet devient installable par cette instance.</summary>
    private async Task<UpdateDownloadResult> VerifyLocalPackageAsync(UpdateInfo update, string localPath, bool deleteOnMismatch, CancellationToken cancellationToken)
    {
        string actual;
        try
        {
            if ((File.GetAttributes(localPath) & FileAttributes.ReparsePoint) != 0)
                return Failure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");
            if (update.Size is { } size && new FileInfo(localPath).Length != size)
            {
                if (deleteOnMismatch) TryDelete(localPath);
                _verifiedPackages.TryRemove(localPath, out _);
                _logger.LogWarning("Taille du paquet de mise à jour incorrecte : paquet supprimé, rien n'a été installé.");
                return Failure(OperationErrorKind.Blocked, "Infra_Update_SizeMismatch");
            }
            actual = await Sha256Hash.ComputeAsync(localPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (deleteOnMismatch) TryDelete(localPath);
            _logger.LogWarning(ex, "Impossible de lire le paquet de mise à jour pour le vérifier.");
            return new UpdateDownloadResult(OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_CopyFailed") }, null);
        }

        if (!Sha256Hash.Matches(actual, update.Sha256))
        {
            if (deleteOnMismatch) TryDelete(localPath);
            _verifiedPackages.TryRemove(localPath, out _);
            _logger.LogWarning("Empreinte SHA-256 du paquet de mise à jour incorrecte : paquet supprimé, rien n'a été installé.");
            return new UpdateDownloadResult(
                OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_HashMismatch"),
                    $"SHA-256 attendu {update.Sha256.ToUpperInvariant()}, obtenu {actual}"),
                null);
        }

        _verifiedPackages[localPath] = new VerifiedPackage(actual, update.Version);
        _logger.LogInformation("Paquet de mise à jour {Version} vérifié (taille et SHA-256).", UpdateVersions.Display(update.Version));
        return new UpdateDownloadResult(OperationResult.Ok(TextRef.Of("Infra_Update_Verified")), localPath);
    }

    /// <summary>
    /// Lance <c>msiexec.exe /i "&lt;paquet&gt;"</c> avec l'assistant de Windows Installer (chemin complet de msiexec dans
    /// System32, sans shell). Windows Installer exécute l'installation dans une transaction : si elle échoue ou est
    /// annulée, les actions déjà effectuées sont annulées (rollback MSI) et la version actuellement installée reste intacte.
    /// La mise à niveau majeure (WiX MajorUpgrade) ne retire l'ancienne version qu'au sein de cette même transaction.
    /// </summary>
    public OperationResult LaunchInstaller(string verifiedPackagePath)
    {
        var prepared = Prepare(verifiedPackagePath, options: null);
        if (!prepared.Success || prepared.Value is not { } install) return prepared.ToResult();
        using (install)
        {
            var started = install.Start();
            if (started.Success) _logger.LogInformation("Installation interactive de la mise à jour lancée (msiexec).");
            else _logger.LogError("Le programme d'installation de la mise à jour n'a pas pu être lancé ({Detail}).", started.TechnicalDetail);
            return started;
        }
    }

    public OperationResult<IPreparedUpdateInstall> PrepareInstall(string verifiedPackagePath, UpdateInstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Prepare(verifiedPackagePath, options);
    }

    private OperationResult<IPreparedUpdateInstall> Prepare(string verifiedPackagePath, UpdateInstallOptions? options)
    {
        if (string.IsNullOrWhiteSpace(verifiedPackagePath)) return PrepareFailure(OperationErrorKind.InvalidInput, "Infra_Update_InvalidPackage");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(verifiedPackagePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PrepareFailure(OperationErrorKind.InvalidInput, "Infra_Update_InvalidPackage");
        }

        if (!PathContainment.IsInside(fullPath, _downloadDirectory)
            || !string.Equals(Path.GetExtension(fullPath), ".msi", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Lancement d'installation refusé : seul un paquet .msi du dossier de mises à jour est accepté.");
            return PrepareFailure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");
        }
        if (!File.Exists(fullPath)) return PrepareFailure(OperationErrorKind.NotFound, "Infra_Update_PackageNotFound");
        if (!_verifiedPackages.TryGetValue(fullPath, out var expected))
        {
            _logger.LogWarning("Lancement d'installation refusé : paquet non vérifié par l'application.");
            return PrepareFailure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");
        }

        string arguments;
        if (options is null)
        {
            arguments = $"/i \"{fullPath}\"";
        }
        else if (BuildQuietArguments(fullPath, options) is { } quiet)
        {
            arguments = quiet;
        }
        else
        {
            return PrepareFailure(OperationErrorKind.InvalidInput, "Infra_Update_InvalidPackage");
        }

        FileStream? stream = null;
        try
        {
            if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                return PrepareFailure(OperationErrorKind.Blocked, "Infra_Update_InvalidPackage");

            // Le fichier reste ouvert en lecture partagée (écriture et suppression refusées) du contrôle jusqu'au lancement.
            stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!Sha256Hash.Matches(Sha256Hash.Compute(stream), expected.Sha256))
            {
                stream.Dispose();
                stream = null;
                _verifiedPackages.TryRemove(fullPath, out _);
                TryDelete(fullPath);
                _logger.LogWarning("Le paquet de mise à jour a été modifié après sa vérification : supprimé, rien n'a été installé.");
                return PrepareFailure(OperationErrorKind.Blocked, "Infra_Update_HashMismatch");
            }

            if (!OperatingSystem.IsWindows())
            {
                stream.Dispose();
                stream = null;
                return PrepareFailure(OperationErrorKind.NotSupported, "Error_NotSupported");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "msiexec.exe"),
                Arguments = arguments,
                UseShellExecute = false,
            };
            var prepared = new PreparedUpdateInstall(stream, startInfo, expected.Version);
            stream = null;
            return OperationResult<IPreparedUpdateInstall>.Ok(prepared);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream?.Dispose();
            _logger.LogError(ex, "Le paquet de mise à jour n'a pas pu être préparé pour l'installation.");
            return OperationResult<IPreparedUpdateInstall>.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_InstallerFailed"), ex.GetType().Name);
        }
    }

    /// <summary>
    /// Arguments de msiexec pour une installation sans question : interface réduite (<c>/qr</c> : progression seulement ;
    /// Windows demande l'autorisation administrateur), pas de redémarrage automatique, dossier d'installation actuel
    /// conservé, journal, relance éventuelle de PCBoost par le paquet (propriété <c>PCBOOST_RELAUNCH</c>, traitée par une
    /// action non élevée). Null si un chemin est invalide (guillemet, caractère de contrôle, chemin relatif).
    /// </summary>
    internal static string? BuildQuietArguments(string packagePath, UpdateInstallOptions options)
    {
        if (!IsSafeAbsolutePath(packagePath)) return null;
        var builder = new System.Text.StringBuilder($"/i \"{packagePath}\" /qr /norestart");
        switch (options.Relaunch)
        {
            case UpdateRelaunch.Window:
                builder.Append(" PCBOOST_RELAUNCH=window");
                break;
            case UpdateRelaunch.Background:
                builder.Append(" PCBOOST_RELAUNCH=background");
                break;
        }
        if (options.InstallFolder is { } folder)
        {
            var trimmed = folder.TrimEnd('\\', '/');
            if (!IsSafeAbsolutePath(trimmed)) return null;
            builder.Append(" INSTALLFOLDER=\"").Append(trimmed).Append('"');
        }
        if (options.LogPath is { } log)
        {
            if (!IsSafeAbsolutePath(log)) return null;
            builder.Append(" /l*v \"").Append(log).Append('"');
        }
        return builder.ToString();
    }

    private static bool IsSafeAbsolutePath(string? path)
        => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && path.Length < 260
           && !path.Contains('"') && !path.Any(char.IsControl);

    private static OperationResult<IPreparedUpdateInstall> PrepareFailure(OperationErrorKind kind, string messageKey)
        => OperationResult<IPreparedUpdateInstall>.Fail(kind, TextRef.Of(messageKey));

    /// <summary>Installation prête : paquet verrouillé jusqu'au lancement de msiexec (une seule fois).</summary>
    private sealed class PreparedUpdateInstall(FileStream packageLock, ProcessStartInfo startInfo, Version version) : IPreparedUpdateInstall
    {
        private FileStream? _lock = packageLock;
        private bool _started;

        public Version Version { get; } = version;

        public OperationResult Start()
        {
            if (_started || _lock is null) return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Infra_Update_InvalidPackage"));
            _started = true;
            try
            {
                using var process = Process.Start(startInfo);
                return process is null
                    ? OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_InstallerFailed"))
                    : OperationResult.Ok(TextRef.Of("Infra_Update_InstallerStarted"));
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_InstallerFailed") };
            }
            finally
            {
                Dispose();
            }
        }

        public void Dispose()
        {
            _lock?.Dispose();
            _lock = null;
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
