using System.Globalization;
using System.Resources;

namespace PCBoost.Core.Localization;

/// <summary>Adapte un <see cref="ResourceManager"/> (.resx embarqués d'un module) en source de chaînes.</summary>
public sealed class ResourceManagerStringSource : IStringResourceSource
{
    private readonly ResourceManager _resourceManager;

    public ResourceManagerStringSource(ResourceManager resourceManager)
        => _resourceManager = resourceManager ?? throw new ArgumentNullException(nameof(resourceManager));

    /// <summary>Crée la source pour les ressources "{assembly}.Resources.Strings" (convention PCBoost).</summary>
    public static ResourceManagerStringSource ForAssembly(System.Reflection.Assembly assembly, string baseName)
        => new(new ResourceManager(baseName, assembly));

    public string? GetString(string key, CultureInfo culture)
    {
        try
        {
            return _resourceManager.GetString(key, culture);
        }
        catch (MissingManifestResourceException)
        {
            return null;
        }
    }
}
