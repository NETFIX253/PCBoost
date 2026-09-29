namespace PCBoost.Optimization;

/// <summary>Identifiants stables des modules d'optimisation et clés de contexte reconnues.</summary>
public static class OptimizationIds
{
    public const string TemporaryFiles = "temp-files";
    public const string StartupApps = "startup-apps";
    public const string PowerPlan = "power-plan";
    public const string VisualEffects = "visual-effects";
    public const string BackgroundApps = "background-apps";

    /// <summary>Identifiant de profil implicite du plan « Optimiser mon PC ».</summary>
    public const string OneClickProfileId = "one-click";

    /// <summary>Clé de <c>OptimizationContext.Items</c> : applique le module même sur une configuration non modeste (visual-effects).</summary>
    public const string ForceItemKey = "force";

    /// <summary>Clé de <c>OptimizationContext.Items</c> : impact minimal (StartupImpact) des entrées proposées par startup-apps.</summary>
    public const string StartupMinimumImpactItemKey = "startup.minimumImpact";

    /// <summary>Consignations faites hors des modules (actions manuelles de l'utilisateur).</summary>
    internal const string CleanupManual = "cleanup";
    internal const string StartupManual = "startup-manager";
    internal const string AutomaticCleanup = "automatic-cleanup";
}
