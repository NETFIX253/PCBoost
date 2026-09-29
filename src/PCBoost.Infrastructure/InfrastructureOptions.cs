using PCBoost.Core.Branding;
using PCBoost.Infrastructure.Updates;

namespace PCBoost.Infrastructure;

/// <summary>Options des services transverses, fournies par l'hôte (PCBoost.App).</summary>
public sealed class InfrastructureOptions
{
    public const string DatabaseFileName = "pcboost.db";

    public BrandingOptions Branding { get; set; } = new();

    /// <summary>Dossier des données (défaut : %LOCALAPPDATA%\&lt;DataFolderName&gt;).</summary>
    public string? DataDirectory { get; set; }

    /// <summary>Dossier des journaux (défaut : &lt;DataDirectory&gt;\logs).</summary>
    public string? LogDirectory { get; set; }

    /// <summary>Version du Windows App SDK, fournie par l'hôte (vide si inconnue).</summary>
    public string? WindowsAppSdkVersion { get; set; }

    public UpdateOptions Updates { get; set; } = new();

    public string ResolveDataDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DataDirectory))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(DataDirectory));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrEmpty(localAppData)) localAppData = Path.GetTempPath();
        return Path.Combine(localAppData, SafeFolderName(Branding?.DataFolderName));
    }

    public string ResolveLogDirectory()
        => !string.IsNullOrWhiteSpace(LogDirectory)
            ? Path.GetFullPath(Environment.ExpandEnvironmentVariables(LogDirectory))
            : Path.Combine(ResolveDataDirectory(), "logs");

    public string ResolveUpdatesDirectory() => (Updates ?? new UpdateOptions()).ResolveDownloadDirectory(ResolveDataDirectory());

    /// <summary>Chemin conseillé du fichier SQLite, à passer à <c>AddPCBoostPersistence</c>.</summary>
    public string ResolveDatabasePath() => Path.Combine(ResolveDataDirectory(), DatabaseFileName);

    /// <summary>Un nom de dossier de marque invalide (vide, séparateurs, « .. ») retombe sur la valeur par défaut.</summary>
    private static string SafeFolderName(string? name)
    {
        var fallback = new BrandingOptions().DataFolderName;
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var trimmed = name.Trim();
        if (trimmed is "." or ".." || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return fallback;
        return trimmed;
    }
}
