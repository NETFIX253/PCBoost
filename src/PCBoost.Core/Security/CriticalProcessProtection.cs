using System.Reflection;
using System.Text.Json;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Services;

namespace PCBoost.Core.Security;

/// <summary>
/// Liste de protection des processus. La liste de base est embarquée et ne peut pas être réduite ;
/// un fichier utilisateur peut uniquement ajouter des entrées.
/// </summary>
public sealed class CriticalProcessProtection : ICriticalProcessProtection
{
    private readonly HashSet<string> _critical;
    private readonly HashSet<string> _sensitive;

    public CriticalProcessProtection(IEnumerable<string>? additionalCritical = null, IEnumerable<string>? additionalSensitive = null)
    {
        var defaults = LoadDefaults();
        _critical = new HashSet<string>(defaults.Critical.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        _sensitive = new HashSet<string>(defaults.Sensitive.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        if (additionalCritical is not null) foreach (var n in additionalCritical) _critical.Add(Normalize(n));
        if (additionalSensitive is not null) foreach (var n in additionalSensitive) _sensitive.Add(Normalize(n));
        _sensitive.ExceptWith(_critical);
    }

    /// <summary>Charge la liste de base + un fichier d'extension utilisateur (ajouts uniquement).</summary>
    public static CriticalProcessProtection CreateWithUserExtensions(string? userFilePath)
    {
        if (string.IsNullOrWhiteSpace(userFilePath) || !File.Exists(userFilePath))
            return new CriticalProcessProtection();
        try
        {
            var list = JsonSerializer.Deserialize<ProtectionList>(File.ReadAllText(userFilePath), JsonOptions);
            return new CriticalProcessProtection(list?.Critical, list?.Sensitive);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new CriticalProcessProtection();
        }
    }

    public IReadOnlyCollection<string> CriticalProcessNames => _critical;

    public bool IsCritical(string processName) => _critical.Contains(Normalize(processName));

    public ProtectionInfo GetProtection(string processName, string? executablePath)
    {
        var name = Normalize(processName);
        if (_critical.Contains(name))
            return new ProtectionInfo(ProtectionLevel.Critical, TextRef.Of("Protection_Critical"));

        if (_sensitive.Contains(name))
            return new ProtectionInfo(ProtectionLevel.Sensitive, TextRef.Of("Protection_Sensitive"));

        // Les exécutables situés dans System32 sont traités comme sensibles par défaut.
        if (executablePath is not null && IsInSystemDirectory(executablePath))
            return new ProtectionInfo(ProtectionLevel.Sensitive, TextRef.Of("Protection_SystemDirectory"));

        return ProtectionInfo.None;
    }

    private static bool IsInSystemDirectory(string path)
    {
        var p = path.Replace('/', '\\');
        return p.Contains(@"\Windows\System32\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\Windows\SysWOW64\", StringComparison.OrdinalIgnoreCase)
            || p.Contains(@"\Windows\SystemApps\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Normalise un nom : minuscules, ajoute ".exe" sauf pour les pseudo-processus.</summary>
    public static string Normalize(string name)
    {
        var n = (name ?? string.Empty).Trim().ToLowerInvariant();
        if (n.Length == 0) return n;
        var fileName = n.Contains('\\') || n.Contains('/') ? System.IO.Path.GetFileName(n.Replace('\\', '/')) : n;
        if (fileName.EndsWith(".exe", StringComparison.Ordinal) || PseudoProcesses.Contains(fileName)) return fileName;
        return fileName + ".exe";
    }

    private static readonly HashSet<string> PseudoProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "secure system", "memory compression", "vmmem", "vmmemws",
    };

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };

    private static ProtectionList LoadDefaults()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PCBoost.Core.Security.protected-processes.json")
            ?? throw new InvalidOperationException("Liste de protection embarquée introuvable.");
        return JsonSerializer.Deserialize<ProtectionList>(stream, JsonOptions)
            ?? throw new InvalidOperationException("Liste de protection embarquée invalide.");
    }

    private sealed class ProtectionList
    {
        public List<string> Critical { get; set; } = [];
        public List<string> Sensitive { get; set; } = [];
    }
}
