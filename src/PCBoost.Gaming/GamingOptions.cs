namespace PCBoost.Gaming;

/// <summary>Réglages techniques du module Gaming (intervalles, limites). Les valeurs par défaut sont celles du produit.</summary>
public sealed class GamingOptions
{
    /// <summary>Intervalle de surveillance des processus pour la détection des jeux.</summary>
    public TimeSpan WatchInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Durée de validité de la bibliothèque de jeux en cache.</summary>
    public TimeSpan LibraryCacheDuration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Intervalle de mise à jour des métriques en direct pendant une session Gaming.</summary>
    public TimeSpan LiveMetricsInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Fenêtre glissante des statistiques d'images en direct.</summary>
    public TimeSpan LiveFrameWindow { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Au-delà de ce délai sans nouvelle image (jeu en pause, réduit…), les FPS en direct sont « non disponibles ».</summary>
    public TimeSpan FrameStaleAfter { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Durée de mesure de l'utilisation processeur des processus d'arrière-plan.</summary>
    public TimeSpan BackgroundCpuSampleDuration { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Seuil d'utilisation processeur (en % du total) au-delà duquel un processus sans fenêtre est ralenti.</summary>
    public double BackgroundCpuThresholdPercent { get; set; } = 2.0;

    /// <summary>Nombre maximal de processus d'arrière-plan ajustés pendant une session.</summary>
    public int MaxBackgroundProcesses { get; set; } = 15;

    /// <summary>Durée pendant laquelle l'aperçu du module d'arrière-plan est réutilisé par l'application.</summary>
    public TimeSpan BackgroundPreviewReuse { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Attente injectable (tests déterministes).</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = static (d, ct) => Task.Delay(d, ct);
}

/// <summary>Clés de <see cref="Core.Optimization.OptimizationContext.Items"/> utilisées par les optimisations Gaming.</summary>
public static class GamingContextKeys
{
    /// <summary>PID du jeu (int).</summary>
    public const string GameProcessId = "GameProcessId";

    /// <summary>Chemin complet de l'exécutable du jeu (string).</summary>
    public const string GameExecutablePath = "GameExecutablePath";

    /// <summary>Dossier d'installation du jeu, s'il est connu (string).</summary>
    public const string GameInstallDirectory = "GameInstallDirectory";
}

/// <summary>Identifiants des modules d'optimisation du mode Gaming.</summary>
public static class GamingOptimizationIds
{
    public const string Power = "gaming-power";
    public const string Priority = "gaming-priority";
    public const string Background = "gaming-background";
    public const string GpuPreference = "gaming-gpu-preference";

    public static IReadOnlyList<string> All { get; } = [Power, Priority, Background, GpuPreference];
}

/// <summary>Actions de notification émises par le module Gaming.</summary>
public static class GamingActions
{
    /// <summary>Activer le mode Gaming pour le jeu détecté (notification « Demander »).</summary>
    public const string Activate = "gaming.activate";

    /// <summary>Étiquette de regroupement des notifications Gaming.</summary>
    public const string NotificationTag = "gaming";
}

/// <summary>Identifiants des vérifications des paramètres de jeu de Windows.</summary>
public static class GameSettingCheckIds
{
    public const string GameMode = "game-mode";
    public const string WindowedOptimizations = "windowed-optimizations";
    public const string HardwareGpuScheduling = "hardware-gpu-scheduling";
    public const string GpuPreference = "gpu-preference";
}
