namespace PCBoost.Core.Optimization;

/// <summary>
/// Identifiants des optimisations qui modifient les programmes lancés au démarrage (page Démarrage et module
/// « startup-apps »). Permet de dater le dernier changement pour comparer les durées de démarrage avant / après.
/// </summary>
public static class StartupChangeSources
{
    public const string StartupApps = "startup-apps";
    public const string StartupManager = "startup-manager";

    public static bool IsStartupChange(string optimizationId)
        => optimizationId is StartupApps or StartupManager;
}
