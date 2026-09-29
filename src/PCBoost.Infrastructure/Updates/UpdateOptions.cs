namespace PCBoost.Infrastructure.Updates;

/// <summary>Configuration des mises à jour (§51). Aucun serveur n'est configuré par défaut : aucune connexion réseau.</summary>
public sealed class UpdateOptions
{
    /// <summary>
    /// Flux local : dossier contenant <c>update.json</c> (ou chemin direct d'un fichier .json), éventuellement un partage réseau
    /// d'entreprise. Vide : la source locale renvoie <c>NotConfigured</c>.
    /// </summary>
    public string? LocalFeedPath { get; set; }

    /// <summary>Dossier de téléchargement des paquets (défaut : &lt;DataDirectory&gt;\updates).</summary>
    public string? DownloadDirectory { get; set; }

    public string ResolveDownloadDirectory(string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(DownloadDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DownloadDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(Path.GetFullPath(dataDirectory), "updates");
    }
}
