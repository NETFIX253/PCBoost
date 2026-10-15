namespace PCBoost.Core.Branding;

/// <summary>
/// Identité produit chargée à l'exécution depuis Assets/Branding/branding.json.
/// Permet de renommer le produit sans toucher au code.
/// </summary>
public sealed class BrandingOptions
{
    public string ProductName { get; set; } = "PCBoost";
    public string Publisher { get; set; } = "Mohamed ABDOURAHMAN (DSI)";
    public Dictionary<string, string> Slogan { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fr"] = "Redonnez de la fluidité à votre PC.",
        ["en"] = "Bring the smoothness back to your PC.",
    };
    public string? SupportUrl { get; set; }
    public string? WebsiteUrl { get; set; }
    public string License { get; set; } = "Propriétaire — tous droits réservés";
    public string DataFolderName { get; set; } = "PCBoost";

    /// <summary>
    /// Flux de mises à jour local facultatif (dossier ou partage d'entreprise contenant le manifeste signé
    /// pcboost-update.json et le MSI), consulté avant GitHub. Vide par défaut.
    /// </summary>
    public string? UpdateFeedPath { get; set; }

    /// <summary>
    /// Dépôt GitHub « propriétaire/nom » dont les Releases publient les mises à jour (manifeste signé
    /// pcboost-update.json de la dernière Release). Vide : aucune source en ligne.
    /// </summary>
    public string? UpdateRepository { get; set; } = "NETFIX253/PCBoost";

    public string GetSlogan(string languageCode)
    {
        if (Slogan.TryGetValue(languageCode, out var s)) return s;
        return Slogan.Values.FirstOrDefault() ?? string.Empty;
    }
}
