using System.Globalization;

namespace PCBoost.Core.Models.Health;

/// <summary>
/// Format des données échangées avec l'assistant administrateur pour les diagnostics (dictionnaire texte → texte de
/// <c>ElevatedResponse.Data</c>). Écrit par PCBoost.Elevator, relu par l'application ; toute valeur invalide est ignorée.
/// </summary>
public static class HealthElevatedData
{
    public const int MaxDisks = 16;
    public const int MaxBoots = 40;
    public const int MaxDegradations = 60;
    public const int MaxPrefetchEntries = 1024;
    private const int MaxTextLength = 200;

    // ---- Compteurs de fiabilité des disques ----

    public static Dictionary<string, string> EncodeDisks(IReadOnlyList<DiskReliability> disks)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = disks.Take(MaxDisks).ToList();
        data["count"] = Int(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var d = list[i];
            data[$"{i}.id"] = Text(d.DeviceId);
            Put(data, $"{i}.wear", d.WearPercent);
            Put(data, $"{i}.temp", d.TemperatureCelsius);
            Put(data, $"{i}.tempmax", d.TemperatureMaxCelsius);
            Put(data, $"{i}.hours", d.PowerOnHours);
            Put(data, $"{i}.rerr", d.ReadErrorsTotal);
            Put(data, $"{i}.runc", d.ReadErrorsUncorrected);
            Put(data, $"{i}.werr", d.WriteErrorsTotal);
        }
        return data;
    }

    public static IReadOnlyList<DiskReliability> DecodeDisks(IReadOnlyDictionary<string, string> data, DateTimeOffset measuredAt)
    {
        var result = new List<DiskReliability>();
        var count = Math.Min(GetLong(data, "count") ?? 0, MaxDisks);
        for (var i = 0; i < count; i++)
        {
            if (!data.TryGetValue($"{i}.id", out var id) || string.IsNullOrWhiteSpace(id) || id.Length > MaxTextLength) continue;
            result.Add(new DiskReliability(
                id,
                Percent(GetLong(data, $"{i}.wear")),
                Temperature(GetLong(data, $"{i}.temp")),
                Temperature(GetLong(data, $"{i}.tempmax")),
                NonNegative(GetLong(data, $"{i}.hours")),
                NonNegative(GetLong(data, $"{i}.rerr")),
                NonNegative(GetLong(data, $"{i}.runc")),
                NonNegative(GetLong(data, $"{i}.werr")),
                measuredAt));
        }
        return result;
    }

    // ---- Mesures de démarrage ----

    public static Dictionary<string, string> EncodeBoot(IReadOnlyList<BootRecord> boots, IReadOnlyList<BootDegradation> degradations)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var b = boots.Take(MaxBoots).ToList();
        data["boots"] = Int(b.Count);
        for (var i = 0; i < b.Count; i++)
        {
            data[$"b{i}.time"] = Time(b[i].Timestamp);
            data[$"b{i}.total"] = Ms(b[i].BootTime);
            data[$"b{i}.main"] = Ms(b[i].MainPathBootTime);
            data[$"b{i}.post"] = Ms(b[i].PostBootTime);
            Put(data, $"b{i}.apps", b[i].StartupAppCount);
        }

        var d = degradations.Take(MaxDegradations).ToList();
        data["degs"] = Int(d.Count);
        for (var i = 0; i < d.Count; i++)
        {
            data[$"d{i}.time"] = Time(d[i].Timestamp);
            data[$"d{i}.kind"] = Int((int)d[i].Kind);
            data[$"d{i}.name"] = Text(d[i].Name);
            if (d[i].FileName is { } file) data[$"d{i}.file"] = Text(file);
            data[$"d{i}.total"] = Ms(d[i].TotalTime);
            data[$"d{i}.deg"] = Ms(d[i].DegradationTime);
        }
        return data;
    }

    public static BootPerformanceData DecodeBoot(IReadOnlyDictionary<string, string> data, DateTimeOffset readAt)
    {
        var boots = new List<BootRecord>();
        var bootCount = Math.Min(GetLong(data, "boots") ?? 0, MaxBoots);
        for (var i = 0; i < bootCount; i++)
        {
            if (GetTime(data, $"b{i}.time") is not { } time || GetLong(data, $"b{i}.total") is not { } total || total <= 0 || total > TimeSpan.FromHours(2).TotalMilliseconds)
                continue;
            var apps = GetLong(data, $"b{i}.apps");
            boots.Add(new BootRecord(time,
                TimeSpan.FromMilliseconds(total),
                TimeSpan.FromMilliseconds(Math.Clamp(GetLong(data, $"b{i}.main") ?? 0, 0, total)),
                TimeSpan.FromMilliseconds(Math.Clamp(GetLong(data, $"b{i}.post") ?? 0, 0, total)),
                apps is >= 0 and < 10_000 ? (int)apps.Value : null));
        }

        var degradations = new List<BootDegradation>();
        var degCount = Math.Min(GetLong(data, "degs") ?? 0, MaxDegradations);
        for (var i = 0; i < degCount; i++)
        {
            if (GetTime(data, $"d{i}.time") is not { } time || !data.TryGetValue($"d{i}.name", out var name) || string.IsNullOrWhiteSpace(name) || name.Length > MaxTextLength)
                continue;
            if (GetLong(data, $"d{i}.deg") is not { } deg || deg <= 0) continue;
            var kind = GetLong(data, $"d{i}.kind") is { } k && Enum.IsDefined(typeof(BootDegradationKind), (int)k) ? (BootDegradationKind)k : BootDegradationKind.Other;
            data.TryGetValue($"d{i}.file", out var file);
            degradations.Add(new BootDegradation(time, kind, name, string.IsNullOrWhiteSpace(file) || file.Length > MaxTextLength ? null : file,
                TimeSpan.FromMilliseconds(Math.Max(0, GetLong(data, $"d{i}.total") ?? 0)), TimeSpan.FromMilliseconds(deg)));
        }

        return new BootPerformanceData(readAt,
            boots.OrderByDescending(b => b.Timestamp).ToList(),
            degradations.OrderByDescending(d => d.Timestamp).ToList());
    }

    // ---- Point de restauration ----

    public const string RestoreStatusKey = "status";
    public const string RestoreTimeKey = "time";

    public static Dictionary<string, string> EncodeRestorePoint(RestorePointStatus status, DateTimeOffset? createdAt)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal) { [RestoreStatusKey] = status.ToString() };
        if (createdAt is { } t) data[RestoreTimeKey] = Time(t);
        return data;
    }

    public static (RestorePointStatus Status, DateTimeOffset? CreatedAt) DecodeRestorePoint(IReadOnlyDictionary<string, string> data)
    {
        var status = data.TryGetValue(RestoreStatusKey, out var s) && Enum.TryParse<RestorePointStatus>(s, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : RestorePointStatus.Failed;
        return (status, GetTime(data, RestoreTimeKey));
    }

    // ---- Dernière exécution des programmes (Prefetch) ----

    public static Dictionary<string, string> EncodeLastRun(IReadOnlyDictionary<string, DateTimeOffset> lastRun)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = lastRun.Take(MaxPrefetchEntries).ToList();
        data["count"] = Int(entries.Count);
        for (var i = 0; i < entries.Count; i++)
        {
            data[$"{i}.exe"] = Text(entries[i].Key);
            data[$"{i}.time"] = Time(entries[i].Value);
        }
        return data;
    }

    /// <summary>Nom d'exécutable en minuscules (ex. « vlc.exe ») → dernière exécution.</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> DecodeLastRun(IReadOnlyDictionary<string, string> data)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var count = Math.Min(GetLong(data, "count") ?? 0, MaxPrefetchEntries);
        for (var i = 0; i < count; i++)
        {
            if (!data.TryGetValue($"{i}.exe", out var exe) || !IsExecutableName(exe) || GetTime(data, $"{i}.time") is not { } time) continue;
            var key = exe.ToLowerInvariant();
            if (!result.TryGetValue(key, out var existing) || time > existing) result[key] = time;
        }
        return result;
    }

    public static bool IsExecutableName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name.Length <= 120 && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0;

    // ---- Outils ----

    private static void Put(Dictionary<string, string> data, string key, long? value)
    {
        if (value is { } v) data[key] = v.ToString(CultureInfo.InvariantCulture);
    }

    private static string Int(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Ms(TimeSpan value) => ((long)Math.Round(value.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Text(string value)
    {
        var text = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= MaxTextLength ? text : text[..MaxTextLength];
    }

    private static long? GetLong(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static DateTimeOffset? GetTime(IReadOnlyDictionary<string, string> data, string key)
        => data.TryGetValue(key, out var s) && DateTimeOffset.TryParseExact(s, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t) ? t : null;

    private static int? Percent(long? value) => value is >= 0 and <= 100 ? (int)value.Value : null;

    private static int? Temperature(long? value) => value is > 0 and < 150 ? (int)value.Value : null;

    private static long? NonNegative(long? value) => value is >= 0 ? value : null;
}
