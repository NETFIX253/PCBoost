using System.Text.Json;
using System.Xml.Linq;
using PCBoost.Core.Branding;

namespace PCBoost.Core.Tests.Domain;

/// <summary>
/// L'identité produit est déclarée à deux endroits : build/Branding.props (binaires, installateur, « Applications installées »)
/// et Assets/Branding/branding.json (page À propos, lu à l'exécution). Ces tests garantissent qu'ils ne divergent pas.
/// </summary>
public sealed class BrandingConsistencyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    [Fact]
    public void BrandingJson_MatchesBrandingProps()
    {
        var props = LoadBrandingProps();
        var json = LoadBrandingJson();

        Assert.Equal(props["BrandProductName"], json.ProductName);
        Assert.Equal(props["BrandPublisher"], json.Publisher);
    }

    [Fact]
    public void Copyright_NamesThePublisher()
    {
        var props = LoadBrandingProps();

        Assert.Contains(props["BrandPublisher"], props["BrandCopyright"], StringComparison.Ordinal);
    }

    [Fact]
    public void BrandingOptionsDefaults_MatchBrandingJson()
    {
        // Valeurs de repli si branding.json est absent ou illisible : même identité que le fichier livré.
        var json = LoadBrandingJson();
        var defaults = new BrandingOptions();

        Assert.Equal(json.ProductName, defaults.ProductName);
        Assert.Equal(json.Publisher, defaults.Publisher);
        Assert.Equal(json.DataFolderName, defaults.DataFolderName);
        Assert.Equal(json.License, defaults.License);
    }

    private static Dictionary<string, string> LoadBrandingProps()
    {
        var document = XDocument.Load(Path.Combine(RepositoryRoot(), "build", "Branding.props"));
        return document.Descendants()
            .Where(e => e.Name.LocalName.StartsWith("Brand", StringComparison.Ordinal) && !e.HasElements)
            .ToDictionary(e => e.Name.LocalName, e => e.Value.Trim(), StringComparer.Ordinal);
    }

    private static BrandingOptions LoadBrandingJson()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "PCBoost.App", "Assets", "Branding", "branding.json");
        var options = JsonSerializer.Deserialize<BrandingOptions>(File.ReadAllText(path), JsonOptions);
        Assert.NotNull(options);
        return options;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "build", "Branding.props"))) return directory.FullName;
        }

        throw new InvalidOperationException("Racine du dépôt introuvable (build/Branding.props).");
    }
}
