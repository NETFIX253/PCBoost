using System.Text.RegularExpressions;

namespace PCBoost.Infrastructure.Updates;

/// <summary>Configuration des mises à jour (§51).</summary>
public sealed partial class UpdateOptions
{
    /// <summary>
    /// Flux local facultatif : dossier contenant <c>pcboost-update.json</c> (manifeste signé) et le paquet MSI, ou chemin
    /// direct du manifeste — par exemple un partage d'entreprise. Consulté avant GitHub. Vide : non utilisé.
    /// </summary>
    public string? LocalFeedPath { get; set; }

    /// <summary>
    /// Dépôt GitHub « propriétaire/nom » : manifeste lu à
    /// <c>https://github.com/&lt;dépôt&gt;/releases/latest/download/pcboost-update.json</c>. Vide : aucune source en ligne.
    /// </summary>
    public string? Repository { get; set; }

    /// <summary>Clés publiques acceptées pour la signature du manifeste (défaut : <see cref="UpdateTrust.ProductionKeys"/>).</summary>
    public IReadOnlyDictionary<string, string> TrustedKeys { get; set; } = UpdateTrust.ProductionKeys;

    /// <summary>Dossier de téléchargement des paquets (défaut : &lt;DataDirectory&gt;\updates).</summary>
    public string? DownloadDirectory { get; set; }

    public string ResolveDownloadDirectory(string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(DownloadDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DownloadDirectory));
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        return Path.Combine(Path.GetFullPath(dataDirectory), "updates");
    }

    /// <summary>Dépôt valide (« propriétaire/nom », caractères autorisés par GitHub), ou null.</summary>
    public string? ResolveRepository()
    {
        var value = Repository?.Trim();
        return value is not null && RepositoryRegex().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal) ? value : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryRegex();
}
