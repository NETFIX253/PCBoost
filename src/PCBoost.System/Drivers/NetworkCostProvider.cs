using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;

namespace PCBoost.Platform.Drivers;

/// <summary>Connexion Internet limitée (forfait, données mobiles) d'après Windows (NetworkInformation, lecture seule).</summary>
public sealed class NetworkCostProvider : INetworkCostProvider
{
    private readonly ILogger<NetworkCostProvider> _logger;

    public NetworkCostProvider(ILogger<NetworkCostProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<NetworkCostProvider>.Instance;
    }

    public bool? IsMeteredConnection()
    {
        try
        {
            var profile = Windows.Networking.Connectivity.NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;
            var cost = profile.GetConnectionCost();
            return IsMetered(cost.NetworkCostType, cost.Roaming, cost.OverDataLimit);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or TypeLoadException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Coût de la connexion illisible");
            return null;
        }
    }

    /// <summary>Fixe, Variable (facturée à l'usage), itinérance ou forfait dépassé = limitée ; Illimitée = non limitée.</summary>
    internal static bool? IsMetered(Windows.Networking.Connectivity.NetworkCostType type, bool roaming, bool overLimit) => type switch
    {
        Windows.Networking.Connectivity.NetworkCostType.Unrestricted => roaming || overLimit,
        Windows.Networking.Connectivity.NetworkCostType.Fixed or Windows.Networking.Connectivity.NetworkCostType.Variable => true,
        _ => roaming || overLimit ? true : null,
    };
}
