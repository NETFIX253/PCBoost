namespace PCBoost.Diagnostics;

/// <summary>Retire les données personnelles des textes journalisés (chemins sous le profil utilisateur).</summary>
internal static class PrivacyRedactor
{
    private static readonly string? ProfilePath = SafeProfilePath();

    /// <summary>Remplace le préfixe du profil utilisateur par « %USERPROFILE% ».</summary>
    public static string Redact(string? text, string? profilePath = null)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var result = text;
        foreach (var prefix in new[] { profilePath, ProfilePath })
        {
            if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 3) continue;
            result = result.Replace(prefix.TrimEnd('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }
        return result;
    }

    private static string? SafeProfilePath()
    {
        try
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }
}
