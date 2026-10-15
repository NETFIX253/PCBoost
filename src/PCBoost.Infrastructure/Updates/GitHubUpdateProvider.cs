using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Source en ligne : Releases GitHub du dépôt configuré. Le manifeste signé de la dernière Release est lu à
/// <c>https://github.com/&lt;dépôt&gt;/releases/latest/download/pcboost-update.json</c> (fichier public, aucune donnée
/// envoyée, aucun compte), puis le paquet MSI qu'il désigne est téléchargé dans le dossier des mises à jour. Seules des
/// adresses HTTPS du dépôt sont acceptées ; signature, taille et empreinte sont vérifiées avant toute installation.
/// </summary>
public sealed class GitHubUpdateProvider : IUpdateProvider, IDisposable
{
    private static readonly TimeSpan FeedTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private const int CopyBufferSize = 81920;

    private readonly UpdateOptions _options;
    private readonly IAppInfo _appInfo;
    private readonly ILogger<GitHubUpdateProvider> _logger;
    private readonly HttpClient _http;

    public GitHubUpdateProvider(UpdateOptions options, IAppInfo appInfo, ILogger<GitHubUpdateProvider> logger)
        : this(options, appInfo, logger, null)
    {
    }

    internal GitHubUpdateProvider(UpdateOptions options, IAppInfo appInfo, ILogger<GitHubUpdateProvider> logger, HttpMessageHandler? handler)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _appInfo = appInfo ?? throw new ArgumentNullException(nameof(appInfo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        // Proxy du système (paramètres Internet de Windows) avec les identifiants de la session ; redirections HTTPS
        // uniquement (le stockage des fichiers de GitHub est sur un autre domaine).
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        };
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PCBoost", UpdateVersions.Display(appInfo.Version)));
    }

    public string Name => "github";

    /// <summary>Adresse du manifeste de la dernière Release, ou null si aucun dépôt n'est configuré.</summary>
    public Uri? FeedUri => _options.ResolveRepository() is { } repository
        ? new Uri($"https://github.com/{repository}/releases/latest/download/{SignedUpdateManifest.FileName}")
        : null;

    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (_options.ResolveRepository() is not { } repository || FeedUri is not { } feed)
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, TextRef.Of("Infra_Update_NotConfigured"));

        string json;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(FeedTimeout);
            try
            {
                using var response = await _http.GetAsync(feed, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // Pas encore de Release publiée avec un manifeste : rien à proposer.
                    _logger.LogInformation("Aucun manifeste de mise à jour publié sur GitHub.");
                    return Failed("Infra_Update_FeedNotFound");
                }
                if (!response.IsSuccessStatusCode || !IsHttps(response.RequestMessage?.RequestUri))
                {
                    _logger.LogWarning("Manifeste de mise à jour : réponse {Status}.", (int)response.StatusCode);
                    return Failed("Infra_Update_Offline");
                }
                if (response.Content.Headers.ContentLength is > SignedUpdateManifest.MaxBytes) return Failed("Infra_Update_FeedInvalid");
                json = await ReadLimitedTextAsync(response.Content, SignedUpdateManifest.MaxBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Failed("Infra_Update_Offline");
            }
            catch (HttpRequestException ex)
            {
                _logger.LogInformation("Manifeste de mise à jour injoignable : {Message}", ex.Message);
                return Failed("Infra_Update_Offline");
            }
            catch (InvalidDataException)
            {
                return Failed("Infra_Update_FeedInvalid");
            }
        }

        var verified = SignedUpdateManifest.Verify(json, _options.TrustedKeys, UpdateSourceRules.ForGitHub(repository));
        if (!verified.Success || verified.Value is not { } manifest)
        {
            _logger.LogWarning("Manifeste de mise à jour refusé ({Detail}).", verified.TechnicalDetail);
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, verified.Message ?? TextRef.Of("Infra_Update_FeedInvalid"));
        }
        if (manifest.PackageFor(SignedUpdateManifest.CurrentArchitecture) is not { } package)
            return Failed("Infra_Update_NoPackageForArchitecture");

        if (!UpdateVersions.IsNewer(manifest.Version, currentVersion))
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, TextRef.Of("Infra_Update_UpToDate", UpdateVersions.Display(currentVersion)));

        _logger.LogInformation("Mise à jour disponible sur GitHub : {Version} (signée par {KeyId}).", UpdateVersions.Display(manifest.Version), manifest.KeyId);
        return new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, manifest.ToUpdateInfo(package, package.Url),
            TextRef.Of("Infra_Update_Available", UpdateVersions.Display(manifest.Version)));
    }

    public async Task<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_options.ResolveRepository() is not { } repository)
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Infra_Update_NotConfigured")), null);
        var prefix = UpdateSourceRules.ForGitHub(repository).PackageUrlPrefix!;
        if (!Uri.TryCreate(update.PackageLocation, UriKind.Absolute, out var source) || !IsHttps(source)
            || !update.PackageLocation.StartsWith(prefix, StringComparison.Ordinal))
        {
            // Paquet d'une autre source (flux local) : pas pour cette source.
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Infra_Update_InvalidPackage")), null);
        }
        var fileName = Uri.UnescapeDataString(source.Segments[^1]);
        if (!PathContainment.IsPlainFileName(fileName) || !fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) || update.Size is not > 0)
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage")), null);

        if (!Sha256Hash.IsValid(update.Sha256))
            return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_InvalidPackage")), null);

        var directory = _options.ResolveDownloadDirectory(_appInfo.DataDirectory);
        var destination = Path.Combine(directory, fileName);
        // Téléchargement partiel propre à ce paquet (empreinte signée dans le nom) : repris à la tentative suivante après
        // une coupure ; un partiel d'un autre paquet n'est jamais complété par erreur.
        var partial = $"{destination}.{update.Sha256[..16].ToLowerInvariant()}.partial";
        var expected = update.Size.Value;
        // Délai d'inactivité (et non durée totale) : une connexion lente mais vivante n'est jamais interrompue.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Directory.CreateDirectory(directory);
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existing >= expected)
            {
                TryDelete(partial);
                existing = 0;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
            idle.CancelAfter(IdleTimeout);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // Partiel incohérent avec le fichier publié : recommencé entièrement à la prochaine tentative.
                TryDelete(partial);
                return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_DownloadFailed")), null);
            }
            if (!response.IsSuccessStatusCode || !IsHttps(response.RequestMessage?.RequestUri))
            {
                _logger.LogWarning("Téléchargement de la mise à jour : réponse {Status}.", (int)response.StatusCode);
                return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_DownloadFailed")), null);
            }
            // 206 : reprise à la position demandée ; toute autre réponse réussie : fichier complet, recommencé de zéro.
            var resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent
                          && response.Content.Headers.ContentRange is { From: { } from, HasRange: true } && from == existing;
            if (response.StatusCode == HttpStatusCode.PartialContent && !resumed)
            {
                TryDelete(partial);
                return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_DownloadFailed")), null);
            }
            var offset = resumed ? existing : 0;
            if (response.Content.Headers.ContentLength is { } length && length != expected - offset)
            {
                TryDelete(partial);
                return new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_SizeMismatch")), null);
            }
            if (resumed) _logger.LogInformation("Reprise du téléchargement de la mise à jour à {Offset} octets sur {Expected}.", offset, expected);

            long copied = offset;
            await using (var input = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, useAsync: true))
            {
                var buffer = new byte[CopyBufferSize];
                int read;
                idle.CancelAfter(IdleTimeout);
                while ((read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false)) > 0)
                {
                    idle.CancelAfter(IdleTimeout);
                    copied += read;
                    // Jamais plus que la taille annoncée par le manifeste signé.
                    if (copied > expected) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), idle.Token).ConfigureAwait(false);
                    progress?.Report(copied * 100d / expected);
                }
                await output.FlushAsync(idle.Token).ConfigureAwait(false);
            }
            if (copied != expected)
            {
                // Trop d'octets : partiel inutilisable. Trop peu (flux coupé) : conservé pour reprise.
                if (copied > expected) TryDelete(partial);
                _logger.LogWarning("Taille du paquet de mise à jour incorrecte ({Copied} au lieu de {Expected} octets).", copied, expected);
                return copied > expected
                    ? new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_SizeMismatch")), null)
                    : new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_DownloadFailed")), null);
            }
            File.Move(partial, destination, overwrite: true);
            progress?.Report(100d);
            return new UpdateDownloadResult(OperationResult.Ok(), destination);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Fermeture ou annulation : le partiel est gardé pour reprise (nettoyé après 7 jours sans reprise).
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // Coupure réseau ou inactivité prolongée : partiel conservé, repris à la prochaine vérification.
            _logger.LogWarning("Téléchargement de la mise à jour interrompu : {Type} {Message}", ex.GetType().Name, ex.Message);
            var outcome = ex is OperationCanceledException
                ? OperationResult.Fail(OperationErrorKind.Timeout, TextRef.Of("Infra_Update_DownloadFailed"))
                : OperationResult.FromException(ex) with { Message = TextRef.Of("Infra_Update_DownloadFailed") };
            return new UpdateDownloadResult(outcome, null);
        }
    }

    public void Dispose() => _http.Dispose();

    private static bool IsHttps(Uri? uri) => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Lit au plus <paramref name="maxBytes"/> octets (UTF-8 strict) ; au-delà, <see cref="InvalidDataException"/>.</summary>
    private static async Task<string> ReadLimitedTextAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > maxBytes) throw new InvalidDataException("Manifeste trop volumineux.");
            memory.Write(buffer, 0, read);
        }
        try
        {
            return SignedUpdateManifest.Utf8.GetString(memory.GetBuffer(), 0, (int)memory.Length);
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new InvalidDataException("Manifeste illisible.", ex);
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
