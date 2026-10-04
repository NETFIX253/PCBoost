using PCBoost.Core.Models.Drivers;

namespace PCBoost.Core.Drivers;

/// <summary>Nature d'une source officielle de pilotes.</summary>
public enum DriverSourceKind
{
    /// <summary>Fabricant du PC : pilotes et logiciels validés pour le modèle.</summary>
    PcManufacturer = 0,
    /// <summary>Fabricant de la carte graphique.</summary>
    Graphics = 1,
}

/// <summary>Fabricant et modèle du PC (Win32_ComputerSystem), carte mère en repli pour les PC assemblés.</summary>
public sealed record ComputerIdentity(string? Manufacturer, string? Model, string? BoardManufacturer);

/// <summary>
/// Source officielle complémentaire à Windows Update : outil et site du fabricant. PCBoost ne télécharge et n'installe
/// rien depuis ces sources ; il ouvre seulement le site officiel (adresse HTTPS de la liste fermée) à la demande.
/// </summary>
public sealed record OfficialDriverSource(string Id, DriverSourceKind Kind, string Vendor, string ToolName, Uri Uri, string? DetectedFor);

/// <summary>
/// Liste fermée des fabricants reconnus et de leurs pages officielles de pilotes (vérifiées le 04/10/2026). Un fabricant
/// inconnu n'a pas de source : Windows Update reste alors la seule source proposée. Les PC Microsoft (Surface) reçoivent
/// leurs pilotes et microprogrammes par Windows Update.
/// </summary>
public static class OfficialDriverSources
{
    private sealed record Entry(string Id, DriverSourceKind Kind, string Vendor, string ToolName, string Uri, string[] Names);

    private static readonly Entry[] Entries =
    [
        new("hp", DriverSourceKind.PcManufacturer, "HP", "HP Support Assistant", "https://support.hp.com/drivers", ["HP", "HP Inc.", "Hewlett-Packard", "Hewlett Packard"]),
        new("dell", DriverSourceKind.PcManufacturer, "Dell", "Dell Command | Update / SupportAssist", "https://www.dell.com/support/home", ["Dell", "Dell Inc."]),
        new("lenovo", DriverSourceKind.PcManufacturer, "Lenovo", "Lenovo Vantage / Lenovo System Update", "https://pcsupport.lenovo.com", ["Lenovo"]),
        new("asus", DriverSourceKind.PcManufacturer, "ASUS", "MyASUS", "https://www.asus.com/support/download-center/", ["ASUSTeK", "ASUS"]),
        new("acer", DriverSourceKind.PcManufacturer, "Acer", "Acer Care Center", "https://www.acer.com/us-en/support/drivers-and-manuals", ["Acer"]),
        new("msi", DriverSourceKind.PcManufacturer, "MSI", "MSI Center", "https://www.msi.com/support/download", ["Micro-Star", "MSI"]),
        new("dynabook", DriverSourceKind.PcManufacturer, "Dynabook (Toshiba)", "Dynabook Service Station", "https://support.dynabook.com/drivers", ["Dynabook", "Toshiba"]),
        new("fujitsu", DriverSourceKind.PcManufacturer, "Fujitsu", "DeskUpdate", "https://support.ts.fujitsu.com/", ["Fujitsu", "FUJITSU CLIENT COMPUTING"]),
        new("nvidia", DriverSourceKind.Graphics, "NVIDIA", "NVIDIA App", "https://www.nvidia.com/en-us/drivers/", ["NVIDIA"]),
        new("amd", DriverSourceKind.Graphics, "AMD", "AMD Software: Adrenalin Edition", "https://www.amd.com/en/support/download/drivers.html", ["Advanced Micro Devices", "AMD", "ATI Technologies", "ATI"]),
        new("intel", DriverSourceKind.Graphics, "Intel", "Intel Driver & Support Assistant", "https://www.intel.com/content/www/us/en/support/detect.html", ["Intel"]),
    ];

    /// <summary>Adresses de la liste fermée (toutes en HTTPS).</summary>
    public static IEnumerable<Uri> AllUris => Entries.Select(e => new Uri(e.Uri));

    /// <summary>
    /// Sources pour ce PC : fabricant du PC (ou de la carte mère d'un PC assemblé), puis fabricants des cartes graphiques
    /// présentes (une source par fabricant, avec le nom de la carte).
    /// </summary>
    public static IReadOnlyList<OfficialDriverSource> For(ComputerIdentity? computer, IReadOnlyList<InstalledDriver> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var result = new List<OfficialDriverSource>();
        var pc = Find(computer?.Manufacturer, DriverSourceKind.PcManufacturer) ?? Find(computer?.BoardManufacturer, DriverSourceKind.PcManufacturer);
        if (pc is not null) result.Add(Source(pc, computer?.Model));

        foreach (var gpu in devices.Where(d => string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = Find(gpu.Provider, DriverSourceKind.Graphics) ?? Find(gpu.DeviceName, DriverSourceKind.Graphics);
            if (entry is null || result.Any(r => r.Id == entry.Id)) continue;
            result.Add(Source(entry, gpu.DeviceName));
        }
        return result;
    }

    /// <summary>
    /// Fabricant reconnu : nom identique, ou commençant par un nom de la liste suivi d'une espace, d'un point ou d'une
    /// virgule, d'une parenthèse ou d'un tiret (« HP Inc. », « Dell Inc. », « LENOVO », « ASUSTeK COMPUTER INC. »,
    /// « Intel(R) UHD Graphics ») — « HPE » ou « Intelligent » ne correspondent pas.
    /// </summary>
    public static string? MatchId(string? name, DriverSourceKind kind) => Find(name, kind)?.Id;

    private static Entry? Find(string? name, DriverSourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var value = name.Trim();
        return Entries.FirstOrDefault(e => e.Kind == kind && e.Names.Any(n => Matches(value, n)));
    }

    private static bool Matches(string value, string name)
    {
        if (value.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
        if (!value.StartsWith(name, StringComparison.OrdinalIgnoreCase) || value.Length == name.Length) return false;
        return value[name.Length] is ' ' or '.' or ',' or '(' or '-';
    }

    private static OfficialDriverSource Source(Entry entry, string? detectedFor)
        => new(entry.Id, entry.Kind, entry.Vendor, entry.ToolName, new Uri(entry.Uri), string.IsNullOrWhiteSpace(detectedFor) ? null : detectedFor.Trim());
}
