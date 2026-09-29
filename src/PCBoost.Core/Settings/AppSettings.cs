namespace PCBoost.Core.Settings;

public enum ThemePreference { System = 0, Light = 1, Dark = 2 }

public enum AutoGamingBehavior { Off = 0, Ask = 1, Automatic = 2 }

/// <summary>
/// Préférences utilisateur persistées localement (SQLite). Aucune n'est envoyée hors du PC.
/// </summary>
public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    // Général
    /// <summary>"system", "fr" ou "en".</summary>
    public string Language { get; set; } = "system";
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    public bool LaunchAtStartup { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool NotificationsEnabled { get; set; } = true;
    public bool MonitoringEnabled { get; set; } = true;
    public bool AllowAutomaticSafeOptimizations { get; set; }
    public bool ExpertMode { get; set; }
    public bool FirstRunCompleted { get; set; }

    // Sécurité
    /// <summary>Confirmation avant les opérations sensibles. Ne peut pas être désactivée pour les actions irréversibles.</summary>
    public bool ConfirmSensitiveOperations { get; set; } = true;
    public bool VerboseLogging { get; set; }
    /// <summary>Durée de conservation de l'historique de restauration (jours).</summary>
    public int HistoryRetentionDays { get; set; } = 90;

    /// <summary>Télémétrie : aucune n'est implémentée. Toujours false (§33).</summary>
    public bool TelemetryEnabled { get; set; }

    public GamingSettings Gaming { get; set; } = new();

    public HealthThresholds Thresholds { get; set; } = new();

    /// <summary>Recommandations masquées par l'utilisateur (identifiants).</summary>
    public List<string> DismissedRecommendations { get; set; } = [];

    public AppSettings Clone()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(this);
        return System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
    }
}

public sealed class GamingSettings
{
    public AutoGamingBehavior AutoActivation { get; set; } = AutoGamingBehavior.Ask;
    public bool AutoRestore { get; set; } = true;
    public bool MonitorDuringSession { get; set; } = true;
    /// <summary>Mesure des FPS via ETW. Nécessite une autorisation administrateur ponctuelle.</summary>
    public bool MeasureFrameRate { get; set; }
    public bool SwitchPowerPlan { get; set; } = true;
    public bool RaiseGamePriority { get; set; } = true;
    public bool ThrottleBackgroundApps { get; set; } = true;
    public string? PreferredGameId { get; set; }
    /// <summary>Jeux ajoutés manuellement (chemins d'exécutables).</summary>
    public List<CustomGameEntry> CustomGames { get; set; } = [];
    /// <summary>Processus que l'utilisateur ne veut jamais voir ralentis pendant le jeu.</summary>
    public List<string> BackgroundExclusions { get; set; } = ["discord.exe", "obs64.exe", "spotify.exe"];
}

public sealed class CustomGameEntry
{
    public string Name { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
}

/// <summary>Seuils configurables du moteur de règles de santé (§26).</summary>
public sealed class HealthThresholds
{
    public double RamWarningPercent { get; set; } = 80;
    public double RamCriticalPercent { get; set; } = 90;
    public double SystemDriveFreeWarningPercent { get; set; } = 15;
    public double SystemDriveFreeCriticalPercent { get; set; } = 10;
    public int StartupWarningCount { get; set; } = 8;
    public int StartupCriticalCount { get; set; } = 15;
    public double CpuSustainedWarningPercent { get; set; } = 70;
    public double CpuSustainedCriticalPercent { get; set; } = 90;
    public double DiskActiveWarningPercent { get; set; } = 80;
    public double DiskActiveCriticalPercent { get; set; } = 95;
    public double CpuTemperatureWarningC { get; set; } = 85;
    public double GpuTemperatureWarningC { get; set; } = 85;
    public double StorageTemperatureWarningC { get; set; } = 65;
    public int UptimeWarningDays { get; set; } = 7;
    public long CleanableWarningBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int BackgroundProcessWarningCount { get; set; } = 180;
}
