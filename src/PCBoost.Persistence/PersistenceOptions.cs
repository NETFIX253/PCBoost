namespace PCBoost.Persistence;

/// <summary>Options du stockage local.</summary>
public sealed class PersistenceOptions
{
    /// <summary>Chemin complet du fichier SQLite (le dossier parent est créé au besoin).</summary>
    public string DatabasePath { get; set; } = string.Empty;
}
