using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Emplacement réservé à une future source en ligne. Aucun serveur n'existe : cette source n'effectue aucun accès réseau
/// et répond toujours « non configurée ». Une implémentation réelle devra fournir un <see cref="UpdateInfo"/> avec
/// l'empreinte SHA-256 publiée, vérifiée par <see cref="UpdateService"/> avant toute installation.
/// </summary>
public sealed class PlaceholderRemoteUpdateProvider : IUpdateProvider
{
    public string Name => "remote";

    public Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        return Task.FromResult(new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, TextRef.Of("Infra_Update_RemoteNotConfigured")));
    }

    public Task<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        return Task.FromResult(new UpdateDownloadResult(
            OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Infra_Update_RemoteNotConfigured")), null));
    }
}
