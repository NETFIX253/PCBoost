using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Services;

namespace PCBoost.Gaming.Services;

/// <summary>
/// Au démarrage de l'application : les sessions Gaming encore « Active » (application arrêtée brutalement pendant une partie)
/// passent « Interrupted ». La restauration elle-même est proposée par le RecoveryManager via la session de restauration
/// associée (<see cref="GamingSession.OptimizationSessionId"/>), restée « en cours ».
/// </summary>
public sealed class GamingSessionReconciler
{
    private readonly IGamingSessionRepository _sessions;
    private readonly IGamingService? _gaming;
    private readonly ILogger<GamingSessionReconciler> _logger;

    public GamingSessionReconciler(IGamingSessionRepository sessions, IGamingService? gaming = null, ILogger<GamingSessionReconciler>? logger = null)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _gaming = gaming;
        _logger = logger ?? NullLogger<GamingSessionReconciler>.Instance;
    }

    /// <summary>Marque les sessions interrompues et les renvoie. La session en cours de ce processus n'est jamais touchée.</summary>
    public async Task<IReadOnlyList<GamingSession>> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var current = _gaming?.CurrentSession?.Id;
        var active = await _sessions.GetByStatusAsync(GamingSessionStatus.Active, cancellationToken).ConfigureAwait(false);
        var interrupted = new List<GamingSession>();
        foreach (var session in active)
        {
            if (session.Id == current) continue;
            // L'heure de fin réelle est inconnue (arrêt brutal) : elle reste vide plutôt qu'estimée.
            var updated = session with { Status = GamingSessionStatus.Interrupted };
            await _sessions.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            interrupted.Add(updated);
        }
        if (interrupted.Count > 0)
            _logger.LogWarning("{Count} session(s) Gaming interrompue(s) détectée(s) au démarrage", interrupted.Count);
        return interrupted;
    }
}
