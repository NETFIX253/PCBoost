using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Source de mise à jour locale : un dossier (local ou partage d'entreprise) contenant le manifeste signé
/// <c>pcboost-update.json</c> — le même que celui publié sur GitHub — et le paquet MSI qu'il désigne (nom de fichier
/// simple, dans ce dossier). La signature est vérifiée avec les mêmes clés que pour GitHub : déposer un fichier dans le
/// partage ne suffit pas pour faire installer un programme. Le paquet est copié dans le dossier de téléchargement ;
/// taille et empreinte SHA-256 sont vérifiées par <see cref="UpdateService"/>.
/// </summary>
public sealed class LocalUpdateProvider : IUpdateProvider
{
    public const string FeedFileName = SignedUpdateManifest.FileName;
    private const long MaxFeedBytes = SignedUpdateManifest.MaxBytes;
    private const int CopyBufferSize = 81920;

    private readonly UpdateOptions _options;
    private readonly IAppInfo _appInfo;
    private readonly ILogger<LocalUpdateProvider> _logger;

    public LocalUpdateProvider(UpdateOptions options, IAppInfo appInfo, ILogger<LocalUpdateProvider> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _appInfo = appInfo ?? throw new ArgumentNullException(nameof(appInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Name => "local";

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (!TryGetFeed(out var feedDirectory, out var feedFile))
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, TextRef.Of("Infra_Update_NotConfigured"));

        try
        {
            var info = new FileInfo(feedFile);
            if (!info.Exists)
                return Failed("Infra_Update_FeedNotFound");
            if (info.Length > MaxFeedBytes)
                return Failed("Infra_Update_FeedInvalid");

            var json = await File.ReadAllTextAsync(feedFile, SignedUpdateManifest.Utf8, cancellationToken).ConfigureAwait(false);
            var verified = SignedUpdateManifest.Verify(json, _options.TrustedKeys, UpdateSourceRules.Local);
            if (!verified.Success || verified.Value is not { } manifest)
            {
                _logger.LogWarning("Manifeste de mise à jour local refusé ({Detail}).", verified.TechnicalDetail);
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, verified.Message ?? TextRef.Of("Infra_Update_FeedInvalid"));
            }
            if (manifest.PackageFor(SignedUpdateManifest.CurrentArchitecture) is not { } package)
                return Failed("Infra_Update_NoPackageForArchitecture");
            var update = manifest.ToUpdateInfo(package, Path.Combine(feedDirectory, package.FileName));

            if (UpdateVersions.IsNewer(update.Version, currentVersion))
            {
                _logger.LogInformation("Mise à jour disponible : {Version}.", UpdateVersions.Display(update.Version));
                return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, update,
                    TextRef.Of("Infra_Update_Available", UpdateVersions.Display(update.Version)));
            }
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null,
                TextRef.Of("Infra_Update_UpToDate", UpdateVersions.Display(currentVersion)));
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            _logger.LogWarning(ex, "Le fichier de mise à jour local est illisible.");
            return Failed("Infra_Update_FeedInvalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Impossible de lire la source de mise à jour locale.");
            return ex is FileNotFoundException or DirectoryNotFoundException
                ? Failed("Infra_Update_FeedNotFound")
                : new UpdateCheckResult(UpdateCheckStatus.Failed, null, TextRef.Of("Error_" + OperationResult.ClassifyException(ex)));
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!TryGetFeed(out var feedDirectory, out _))
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Infra_Update_NotConfigured")), null);

        string source;
        try
        {
            source = Path.GetFullPath(update.PackageLocation);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Infra_Update_InvalidPackage")), null);
        }

        // Seul un paquet situé dans le dossier du flux configuré peut être copié.
        if (!PathContainment.IsInside(source, feedDirectory) || !PathContainment.IsPlainFileName(Path.GetFileName(source)))
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage")), null);
        if (!File.Exists(source))
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Infra_Update_PackageNotFound")), null);

        var downloadDirectory = _options.ResolveDownloadDirectory(_appInfo.DataDirectory);
        var destination = Path.Combine(downloadDirectory, Path.GetFileName(source));
        var partial = destination + ".partial";
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            // Jamais plus que la taille signée (ou la taille maximale d'un paquet) : un fichier remplacé sur le partage par
            // un fichier énorme ne remplit pas le disque.
            var limit = update.Size is > 0 ? update.Size.Value : SignedUpdateManifest.MaxPackageSize;
            long copied = 0;
            await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, useAsync: true))
            {
                if (input.Length > limit || (update.Size is > 0 && input.Length != update.Size))
                    return SizeMismatch(input.Length, limit);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true);
                var total = input.Length;
                var buffer = new byte[CopyBufferSize];
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    copied += read;
                    if (copied > limit) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(total > 0 ? copied * 100d / total : 100d);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (copied > limit || (update.Size is > 0 && copied != update.Size))
            {
                TryDelete(partial);
                return SizeMismatch(copied, limit);
            }
            File.Move(partial, destination, overwrite: true);
            progress?.Report(100d);
            return new UpdateDownloadResult(OperationResult.Ok(), destination);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            _logger.LogWarning(ex, "La copie du paquet de mise à jour a échoué.");
            return new UpdateDownloadResult(OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_CopyFailed") }, null);
        }
    }

    private UpdateDownloadResult SizeMismatch(long actual, long limit)
    {
        _logger.LogWarning("Taille du paquet de mise à jour du flux local incorrecte ({Actual} octets, attendu au plus {Limit}).", actual, limit);
        return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_SizeMismatch")), null);
    }

    private bool TryGetFeed(out string feedDirectory, out string feedFile)
    {
        feedDirectory = feedFile = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.LocalFeedPath)) return false;
        try
        {
            var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.LocalFeedPath.Trim()));
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && !Directory.Exists(path))
            {
                feedFile = path;
                feedDirectory = Path.GetDirectoryName(path) ?? path;
            }
            else
            {
                feedDirectory = path;
                feedFile = Path.Combine(path, FeedFileName);
            }
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.LogWarning(ex, "Le chemin de la source de mise à jour locale est invalide.");
            return false;
        }
    }

    private static UpdateCheckResult Failed(string messageKey) => new(UpdateCheckStatus.Failed, null, TextRef.Of(messageKey));

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Fichier temporaire de mise à jour non supprimé.");
        }
    }
}
