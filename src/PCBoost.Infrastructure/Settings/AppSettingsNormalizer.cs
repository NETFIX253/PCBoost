using PCBoost.Core.Security;
using PCBoost.Core.Settings;
using PCBoost.Infrastructure.Localization;

namespace PCBoost.Infrastructure.Settings;

/// <summary>
/// Validation et normalisation des préférences, à la lecture comme à l'écriture : valeurs bornées, seuils cohérents
/// (un seuil critique est toujours plus sévère que le seuil d'avertissement), langue connue, télémétrie toujours désactivée.
/// </summary>
public static class AppSettingsNormalizer
{
    public const int MinHistoryRetentionDays = 7;
    public const int MaxHistoryRetentionDays = 365;

    /// <summary>Corrige <paramref name="settings"/> sur place ; renvoie le nom des champs corrigés.</summary>
    public static IReadOnlyList<string> Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var corrections = new List<string>();

        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
            corrections.Add(nameof(AppSettings.SchemaVersion));
        }

        var language = SupportedLanguages.Normalize(settings.Language);
        if (!string.Equals(language, settings.Language, StringComparison.Ordinal))
        {
            settings.Language = language;
            corrections.Add(nameof(AppSettings.Language));
        }

        if (!Enum.IsDefined(settings.Theme))
        {
            settings.Theme = ThemePreference.System;
            corrections.Add(nameof(AppSettings.Theme));
        }

        var retention = Math.Clamp(settings.HistoryRetentionDays, MinHistoryRetentionDays, MaxHistoryRetentionDays);
        if (retention != settings.HistoryRetentionDays)
        {
            settings.HistoryRetentionDays = retention;
            corrections.Add(nameof(AppSettings.HistoryRetentionDays));
        }

        // Aucune télémétrie n'existe (§33) : la valeur est forcée, quelle que soit la source.
        if (settings.TelemetryEnabled)
        {
            settings.TelemetryEnabled = false;
            corrections.Add(nameof(AppSettings.TelemetryEnabled));
        }

        var dismissed = NormalizeIdentifiers(settings.DismissedRecommendations, static s => s);
        if (settings.DismissedRecommendations is null || !dismissed.SequenceEqual(settings.DismissedRecommendations, StringComparer.Ordinal))
        {
            settings.DismissedRecommendations = dismissed;
            corrections.Add(nameof(AppSettings.DismissedRecommendations));
        }

        if (settings.Gaming is null)
        {
            settings.Gaming = new GamingSettings();
            corrections.Add(nameof(AppSettings.Gaming));
        }
        NormalizeGaming(settings.Gaming, corrections);

        if (settings.Thresholds is null)
        {
            settings.Thresholds = new HealthThresholds();
            corrections.Add(nameof(AppSettings.Thresholds));
        }
        NormalizeThresholds(settings.Thresholds, corrections);

        return corrections;
    }

    private static void NormalizeGaming(GamingSettings gaming, List<string> corrections)
    {
        const string prefix = nameof(AppSettings.Gaming) + ".";

        if (!Enum.IsDefined(gaming.AutoActivation))
        {
            gaming.AutoActivation = AutoGamingBehavior.Ask;
            corrections.Add(prefix + nameof(GamingSettings.AutoActivation));
        }

        var preferred = string.IsNullOrWhiteSpace(gaming.PreferredGameId) ? null : gaming.PreferredGameId.Trim();
        if (!string.Equals(preferred, gaming.PreferredGameId, StringComparison.Ordinal))
        {
            gaming.PreferredGameId = preferred;
            corrections.Add(prefix + nameof(GamingSettings.PreferredGameId));
        }

        var exclusions = NormalizeIdentifiers(gaming.BackgroundExclusions, CriticalProcessProtection.Normalize);
        if (gaming.BackgroundExclusions is null || !exclusions.SequenceEqual(gaming.BackgroundExclusions, StringComparer.Ordinal))
        {
            gaming.BackgroundExclusions = exclusions;
            corrections.Add(prefix + nameof(GamingSettings.BackgroundExclusions));
        }

        var games = NormalizeCustomGames(gaming.CustomGames);
        if (gaming.CustomGames is null
            || games.Count != gaming.CustomGames.Count
            || games.Where((g, i) => gaming.CustomGames[i] is not { } original
                                     || !string.Equals(original.Name, g.Name, StringComparison.Ordinal)
                                     || !string.Equals(original.ExecutablePath, g.ExecutablePath, StringComparison.Ordinal)).Any())
        {
            gaming.CustomGames = games;
            corrections.Add(prefix + nameof(GamingSettings.CustomGames));
        }
    }

    private static List<CustomGameEntry> NormalizeCustomGames(List<CustomGameEntry>? games)
    {
        var result = new List<CustomGameEntry>();
        if (games is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
        {
            var path = game?.ExecutablePath?.Trim();
            if (string.IsNullOrEmpty(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(path)) continue;
            var name = game!.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                var fileName = path.Replace('/', '\\');
                fileName = fileName[(fileName.LastIndexOf('\\') + 1)..];
                name = fileName[..^4];
            }
            result.Add(new CustomGameEntry { Name = name, ExecutablePath = path });
        }
        return result;
    }

    private static List<string> NormalizeIdentifiers(List<string>? values, Func<string, string> normalize)
    {
        var result = new List<string>();
        if (values is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            var normalized = normalize(value.Trim());
            if (normalized.Length > 0 && seen.Add(normalized)) result.Add(normalized);
        }
        return result;
    }

    private static void NormalizeThresholds(HealthThresholds t, List<string> corrections)
    {
        var d = new HealthThresholds();
        const string prefix = nameof(AppSettings.Thresholds) + ".";

        // Seuils croissants : l'avertissement doit être strictement inférieur au seuil critique.
        (t.RamWarningPercent, t.RamCriticalPercent) = Pair(t.RamWarningPercent, t.RamCriticalPercent,
            d.RamWarningPercent, d.RamCriticalPercent, 50, 99, increasing: true, prefix + "Ram", corrections);
        (t.CpuSustainedWarningPercent, t.CpuSustainedCriticalPercent) = Pair(t.CpuSustainedWarningPercent, t.CpuSustainedCriticalPercent,
            d.CpuSustainedWarningPercent, d.CpuSustainedCriticalPercent, 30, 100, increasing: true, prefix + "CpuSustained", corrections);
        (t.DiskActiveWarningPercent, t.DiskActiveCriticalPercent) = Pair(t.DiskActiveWarningPercent, t.DiskActiveCriticalPercent,
            d.DiskActiveWarningPercent, d.DiskActiveCriticalPercent, 30, 100, increasing: true, prefix + "DiskActive", corrections);

        var (startupWarning, startupCritical) = Pair(t.StartupWarningCount, t.StartupCriticalCount,
            d.StartupWarningCount, d.StartupCriticalCount, 1, 200, increasing: true, prefix + "StartupCount", corrections);
        t.StartupWarningCount = (int)startupWarning;
        t.StartupCriticalCount = (int)startupCritical;

        // Espace libre : plus la valeur est basse, plus c'est grave (avertissement > critique).
        (t.SystemDriveFreeWarningPercent, t.SystemDriveFreeCriticalPercent) = Pair(t.SystemDriveFreeWarningPercent, t.SystemDriveFreeCriticalPercent,
            d.SystemDriveFreeWarningPercent, d.SystemDriveFreeCriticalPercent, 1, 50, increasing: false, prefix + "SystemDriveFree", corrections);

        t.CpuTemperatureWarningC = Single(t.CpuTemperatureWarningC, d.CpuTemperatureWarningC, 50, 110, prefix + nameof(HealthThresholds.CpuTemperatureWarningC), corrections);
        t.GpuTemperatureWarningC = Single(t.GpuTemperatureWarningC, d.GpuTemperatureWarningC, 50, 110, prefix + nameof(HealthThresholds.GpuTemperatureWarningC), corrections);
        t.StorageTemperatureWarningC = Single(t.StorageTemperatureWarningC, d.StorageTemperatureWarningC, 40, 90, prefix + nameof(HealthThresholds.StorageTemperatureWarningC), corrections);
        t.UptimeWarningDays = (int)Single(t.UptimeWarningDays, d.UptimeWarningDays, 1, 365, prefix + nameof(HealthThresholds.UptimeWarningDays), corrections);
        t.BackgroundProcessWarningCount = (int)Single(t.BackgroundProcessWarningCount, d.BackgroundProcessWarningCount, 50, 2000, prefix + nameof(HealthThresholds.BackgroundProcessWarningCount), corrections);

        const long minCleanable = 100L * 1024 * 1024;
        const long maxCleanable = 1024L * 1024 * 1024 * 1024;
        var cleanable = Math.Clamp(t.CleanableWarningBytes, minCleanable, maxCleanable);
        if (cleanable != t.CleanableWarningBytes)
        {
            t.CleanableWarningBytes = cleanable;
            corrections.Add(prefix + nameof(HealthThresholds.CleanableWarningBytes));
        }
    }

    /// <summary>Borne les deux seuils ; s'ils restent incohérents, les valeurs par défaut (cohérentes) sont rétablies.</summary>
    private static (double Warning, double Critical) Pair(
        double warning, double critical, double defaultWarning, double defaultCritical,
        double min, double max, bool increasing, string name, List<string> corrections)
    {
        var w = double.IsFinite(warning) ? Math.Clamp(warning, min, max) : defaultWarning;
        var c = double.IsFinite(critical) ? Math.Clamp(critical, min, max) : defaultCritical;
        var coherent = increasing ? w < c : w > c;
        if (!coherent)
        {
            w = defaultWarning;
            c = defaultCritical;
        }
        if (w != warning || c != critical) corrections.Add(name);
        return (w, c);
    }

    private static double Single(double value, double defaultValue, double min, double max, string name, List<string> corrections)
    {
        var v = double.IsFinite(value) ? Math.Clamp(value, min, max) : defaultValue;
        if (v != value) corrections.Add(name);
        return v;
    }
}
