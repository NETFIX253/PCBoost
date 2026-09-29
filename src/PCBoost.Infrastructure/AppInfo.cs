using System.Reflection;
using System.Runtime.InteropServices;
using PCBoost.Core.Services;

namespace PCBoost.Infrastructure;

/// <summary>Informations « À propos » : produit (identité remplaçable), versions, dossiers locaux.</summary>
public sealed class AppInfo : IAppInfo
{
    public AppInfo(InfrastructureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var entryAssembly = Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly;

        var brandName = options.Branding?.ProductName;
        ProductName = !string.IsNullOrWhiteSpace(brandName)
            ? brandName.Trim()
            : entryAssembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? entryAssembly.GetName().Name ?? string.Empty;
        Version = entryAssembly.GetName().Version ?? new Version(0, 0, 0, 0);
        DotNetVersion = RuntimeInformation.FrameworkDescription;
        WindowsAppSdkVersion = options.WindowsAppSdkVersion?.Trim() ?? string.Empty;
        DataDirectory = options.ResolveDataDirectory();
        LogDirectory = options.ResolveLogDirectory();
    }

    public string ProductName { get; }

    public Version Version { get; }

    public string DotNetVersion { get; }

    /// <summary>Vide si l'hôte ne l'a pas fournie (l'interface affiche alors « Non disponible »).</summary>
    public string WindowsAppSdkVersion { get; }

    public string DataDirectory { get; }

    public string LogDirectory { get; }
}
