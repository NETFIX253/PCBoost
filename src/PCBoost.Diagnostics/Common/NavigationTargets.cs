namespace PCBoost.Diagnostics;

/// <summary>Clés de navigation des pages de l'application (identiques aux clés de page de la couche présentation).</summary>
internal static class NavigationTargets
{
    public const string Home = "home";
    public const string Analysis = "analysis";
    public const string Optimization = "optimization";
    public const string Cleanup = "cleanup";
    public const string Startup = "startup";
    public const string Processes = "processes";
    public const string Gaming = "gaming";
    public const string Performance = "performance";
    public const string History = "history";
    public const string Settings = "settings";
    public const string Diagnosis = "diagnosis";
    public const string Storage = "storage";
    public const string OldPc = "oldpc";
    public const string Health = "health";
}

/// <summary>Identifiants des optimisations proposées par les recommandations (module Optimization).</summary>
internal static class OptimizationIds
{
    public const string TempFiles = "temp-files";
    public const string StartupApps = "startup-apps";
    public const string PowerPlan = "power-plan";
    public const string VisualEffects = "visual-effects";
    public const string BackgroundApps = "background-apps";
}
