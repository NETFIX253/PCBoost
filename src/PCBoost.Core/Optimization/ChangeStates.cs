using System.Text.Json;
using System.Text.Json.Serialization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.Core.Optimization;

/// <summary>
/// États « avant » sérialisés dans ChangeRecord.BeforeState. Partagés entre les modules qui créent
/// les modifications (Optimization, Gaming) et les gestionnaires d'annulation (IChangeHandler).
/// </summary>
public static class ChangeStateSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize<T>(T state) => JsonSerializer.Serialize(state, Options);

    public static T? Deserialize<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }
}

/// <summary>État d'une valeur de registre avant modification (Existed=false : la valeur sera supprimée à l'annulation).</summary>
public sealed record RegistryValueState(
    RegistryHiveKind Hive,
    RegistryViewKind View,
    string KeyPath,
    string ValueName,
    bool Existed,
    RegistryValueType Type = RegistryValueType.Unknown,
    string? StringValue = null,
    long? NumericValue = null,
    string? BinaryBase64 = null,
    string[]? MultiStringValue = null)
{
    public RegistryLocation Location => new(Hive, KeyPath, View);

    public static RegistryValueState Capture(RegistryLocation location, string valueName, RegistryValueData? data)
    {
        if (data is null)
            return new RegistryValueState(location.Hive, location.View, location.KeyPath, valueName, false);

        return data.Type switch
        {
            RegistryValueType.String or RegistryValueType.ExpandString
                => new(location.Hive, location.View, location.KeyPath, valueName, true, data.Type, StringValue: Convert.ToString(data.Value, System.Globalization.CultureInfo.InvariantCulture)),
            RegistryValueType.DWord or RegistryValueType.QWord
                => new(location.Hive, location.View, location.KeyPath, valueName, true, data.Type, NumericValue: Convert.ToInt64(data.Value, System.Globalization.CultureInfo.InvariantCulture)),
            RegistryValueType.Binary
                => new(location.Hive, location.View, location.KeyPath, valueName, true, data.Type, BinaryBase64: Convert.ToBase64String((byte[])data.Value)),
            RegistryValueType.MultiString
                => new(location.Hive, location.View, location.KeyPath, valueName, true, data.Type, MultiStringValue: (string[])data.Value),
            _ => new(location.Hive, location.View, location.KeyPath, valueName, true, data.Type, StringValue: data.Value?.ToString()),
        };
    }

    /// <summary>Reconstruit la donnée d'origine (null si la valeur n'existait pas).</summary>
    public RegistryValueData? ToData() => !Existed ? null : Type switch
    {
        RegistryValueType.String or RegistryValueType.ExpandString => new RegistryValueData(Type, StringValue ?? string.Empty),
        RegistryValueType.DWord => new RegistryValueData(Type, unchecked((int)(NumericValue ?? 0))),
        RegistryValueType.QWord => new RegistryValueData(Type, NumericValue ?? 0L),
        RegistryValueType.Binary => new RegistryValueData(Type, Convert.FromBase64String(BinaryBase64 ?? string.Empty)),
        RegistryValueType.MultiString => new RegistryValueData(Type, MultiStringValue ?? []),
        _ => new RegistryValueData(RegistryValueType.String, StringValue ?? string.Empty),
    };
}

public sealed record PowerSchemeState(Guid SchemeId, string? SchemeName);

public sealed record ProcessPriorityState(int ProcessId, string ProcessName, DateTimeOffset? StartTime, ProcessPriority Priority);

public sealed record ProcessEfficiencyState(int ProcessId, string ProcessName, DateTimeOffset? StartTime, bool Enabled);

public sealed record ScheduledTaskState(string TaskPath, bool Enabled);

public sealed record FileDeletionRecord(string CategoryId, int FileCount, long Bytes);
