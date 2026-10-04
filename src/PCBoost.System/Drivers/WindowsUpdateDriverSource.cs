using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;

namespace PCBoost.Platform.Drivers;

/// <summary>
/// Recherche des mises à jour de pilotes avec l'agent Windows Update, sans autorisation administrateur. L'appel de
/// l'agent est synchrone et ne peut pas être interrompu : il s'exécute sur un fil dédié et une annulation cesse
/// seulement de l'attendre (son résultat est alors ignoré).
/// </summary>
public sealed class WindowsUpdateDriverSource : IDriverUpdateSource
{
    private readonly ILogger<WindowsUpdateDriverSource> _logger;

    public WindowsUpdateDriverSource(ILogger<WindowsUpdateDriverSource>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsUpdateDriverSource>.Instance;
    }

    public Task<DriverSearchResult> SearchAsync(CancellationToken cancellationToken = default)
    {
        var search = Task.Factory.StartNew(Search, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return search.WaitAsync(cancellationToken);
    }

    private DriverSearchResult Search()
    {
        using var tracker = new ComTracker();
        try
        {
            var started = DateTimeOffset.UtcNow;
            var session = WindowsUpdateAgent.CreateSession(tracker);
            var (code, updates) = WindowsUpdateAgent.SearchDrivers(session, tracker);
            if (code is not (WindowsUpdateAgent.ResultSucceeded or WindowsUpdateAgent.ResultSucceededWithErrors))
            {
                _logger.LogWarning("Recherche Windows Update : code de résultat {Code}", code);
                return DriverSearchResult.Failed(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Error_SearchFailed"), $"ResultCode {code}"));
            }

            var offers = new List<DriverUpdateOffer>();
            foreach (var update in updates)
            {
                if (WindowsUpdateAgent.ReadOffer(update, tracker) is { } offer) offers.Add(offer);
            }

            var reboot = SafeRead(() => WindowsUpdateAgent.IsRebootRequired(tracker));
            var busy = SafeRead(() => WindowsUpdateAgent.IsInstallerBusy(session, tracker));
            _logger.LogInformation("Recherche Windows Update : {Count} pilote(s) proposé(s) en {Seconds:0.0} s", offers.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
            return new DriverSearchResult(OperationResult.Ok(), offers, reboot, busy);
        }
        catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
        {
            var failure = WindowsUpdateAgent.Classify(ex);
            _logger.LogWarning("Recherche Windows Update impossible : {Detail}", failure.TechnicalDetail);
            return DriverSearchResult.Failed(failure);
        }
    }

    private static bool SafeRead(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (WindowsUpdateAgent.IsComFailure(ex))
        {
            return false;
        }
    }
}
