using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Safety;

/// <summary>
/// Point de restauration Windows avant les optimisations avancées : une demande à l'assistant administrateur
/// (liste blanche fermée), qui vérifie qu'un point a réellement été créé. Un point de moins de 24 heures est réutilisé,
/// comme le fait Windows. Le résultat est inscrit au journal des modifications. PCBoost conserve en plus sa propre
/// restauration de chaque modification, indépendante de ce point.
/// </summary>
public sealed class RestorePointService : IRestorePointService
{
    private readonly IElevationService _elevation;
    private readonly IActivityJournal _journal;
    private readonly ILogger<RestorePointService> _logger;

    public RestorePointService(IElevationService elevation, IActivityJournal journal, ILogger<RestorePointService>? logger = null)
    {
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _logger = logger ?? NullLogger<RestorePointService>.Instance;
    }

    public async Task<RestorePointResult> CreateAsync(CancellationToken cancellationToken = default)
    {
        var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedHealthOperations.RestorePointCreate, new Dictionary<string, string>()), cancellationToken)
            .ConfigureAwait(false);

        RestorePointResult result;
        if (!response.Outcome.Success)
        {
            result = new RestorePointResult(RestorePointStatus.Failed, null,
                response.Outcome.Error == OperationErrorKind.ElevationCancelled
                    ? TextRef.Of("Opt_RestorePoint_Cancelled")
                    : TextRef.Of("Opt_RestorePoint_Failed"));
        }
        else
        {
            var (status, createdAt) = HealthElevatedData.DecodeRestorePoint(response.Data);
            result = new RestorePointResult(status, createdAt, TextRef.Of(status switch
            {
                RestorePointStatus.Created => "Opt_RestorePoint_Created",
                RestorePointStatus.RecentExists => "Opt_RestorePoint_Recent",
                RestorePointStatus.Disabled => "Opt_RestorePoint_Disabled",
                _ => "Opt_RestorePoint_Failed",
            }));
        }

        _logger.LogInformation("Point de restauration Windows : {Status}", result.Status);
        if (result.Status != RestorePointStatus.Failed || response.Outcome.Error != OperationErrorKind.ElevationCancelled)
        {
            try
            {
                await _journal.LogAsync(result.IsAvailable ? ActivityKind.Optimization : ActivityKind.Warning, result.Message!, null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                _logger.LogDebug(ex, "Journal : point de restauration non inscrit");
            }
        }
        return result;
    }
}
