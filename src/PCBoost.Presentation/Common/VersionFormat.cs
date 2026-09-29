namespace PCBoost.Presentation.Common;

internal static class VersionFormat
{
    /// <summary>« 1.2.3 » (ou « 1.2 » si la version n'a que deux composants).</summary>
    public static string Short(Version? version)
    {
        if (version is null) return string.Empty;
        return version.Build >= 0 ? version.ToString(3) : version.ToString(2);
    }
}
