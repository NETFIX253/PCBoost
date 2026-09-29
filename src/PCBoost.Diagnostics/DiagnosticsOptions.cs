namespace PCBoost.Diagnostics;

/// <summary>Réglages de l'analyse système.</summary>
public sealed class SystemAnalyzerOptions
{
    /// <summary>Durée d'échantillonnage de la charge quand <c>AnalysisOptions.LoadSamplingDuration</c> est absent.</summary>
    public TimeSpan DefaultLoadSamplingDuration { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Pas d'échantillonnage direct de la charge.</summary>
    public TimeSpan LoadSamplingInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Nombre minimal d'échantillons récents du moniteur pour éviter un échantillonnage direct.</summary>
    public int MinimumMonitorSamples { get; set; } = 3;
}

/// <summary>Réglages du diagnostic « Pourquoi mon PC est lent ? ».</summary>
public sealed class SlowPcDiagnosticOptions
{
    /// <summary>Âge maximal d'une analyse réutilisée au lieu d'en lancer une nouvelle.</summary>
    public TimeSpan ReportMaxAge { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Durée d'échantillonnage de la charge lors d'une nouvelle analyse.</summary>
    public TimeSpan LoadSamplingDuration { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Réglages de l'analyse de l'espace disque.</summary>
public sealed class StorageAnalyzerOptions
{
    /// <summary>Budget de temps pour mesurer Program Files ; au-delà, la catégorie est « non mesurée ».</summary>
    public TimeSpan ApplicationsTimeBudget { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Budget de temps pour mesurer les dossiers de premier niveau du profil ; au-delà, la liste est partielle.</summary>
    public TimeSpan LargestFoldersTimeBudget { get; set; } = TimeSpan.FromSeconds(15);

    public int LargestFolderCount { get; set; } = 8;
}

/// <summary>Réglages de la surveillance temps réel (§22, §47).</summary>
public sealed class PerformanceMonitorOptions
{
    public TimeSpan ActiveInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan BackgroundInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ActiveTemperatureInterval { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan BackgroundTemperatureInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Profondeur de l'historique en mémoire.</summary>
    public TimeSpan HistoryDuration { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>Réglages de l'historique persistant des performances (§66).</summary>
public sealed class PerformanceHistoryOptions
{
    /// <summary>Durée de conservation des instantanés.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>Fréquence de la purge des instantanés trop anciens.</summary>
    public TimeSpan PurgeInterval { get; set; } = TimeSpan.FromDays(1);
}
