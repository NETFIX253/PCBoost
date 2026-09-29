using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace PCBoost.Infrastructure.Updates;

/// <summary>
/// Contenu de <c>update.json</c> :
/// <c>{ "version": "1.1.0", "package": "PCBoost-1.1.0-x64.msi", "sha256": "…", "notes": "…", "publishedAt": "2026-10-01T00:00:00Z" }</c>.
/// </summary>
internal sealed class UpdateFeedManifest
{
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("package")] public string? Package { get; set; }
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("publishedAt")] public string? PublishedAt { get; set; }

    /// <summary>Date facultative : absente → null (valide) ; illisible → false.</summary>
    public static bool TryParseDate(string? text, out DateTimeOffset? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return false;
        value = parsed;
        return true;
    }
}

internal static class UpdateVersions
{
    /// <summary>Compare sans tenir compte des composants absents (« 1.0.0 » == « 1.0.0.0 »).</summary>
    public static bool IsNewer(Version candidate, Version current) => Normalize(candidate) > Normalize(current);

    public static Version Normalize(Version v)
        => new(Math.Max(v.Major, 0), Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    /// <summary>Affichage court : « 1.1.0 » (révision affichée seulement si non nulle).</summary>
    public static string Display(Version v)
    {
        var n = Normalize(v);
        return n.Revision > 0 ? n.ToString(4) : n.ToString(3);
    }
}

internal static class Sha256Hash
{
    public static bool IsValid(string? hex)
        => hex is { Length: 64 } && hex.All(Uri.IsHexDigit);

    public static bool Matches(string actualHex, string expectedHex)
    {
        if (!IsValid(actualHex) || !IsValid(expectedHex)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actualHex), Convert.FromHexString(expectedHex));
    }

    public static async Task<string> ComputeAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public static string Compute(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
}

internal static class PathContainment
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Le chemin (résolu) est-il situé sous <paramref name="directory"/> ? Les segments « .. » sont résolus avant comparaison.</summary>
    public static bool IsInside(string path, string directory)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(root, Comparison) && full.Length > root.Length;
    }

    public static bool PathEquals(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), Comparison);

    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Nom de fichier simple : ni séparateur, ni « .. », ni caractère interdit.</summary>
    public static bool IsPlainFileName(string? name)
        => !string.IsNullOrWhiteSpace(name)
           && name.IndexOfAny(['\\', '/', ':']) < 0
           && !name.Contains("..", StringComparison.Ordinal)
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && string.Equals(name, name.Trim(), StringComparison.Ordinal);
}
