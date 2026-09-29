using System.Text.Json;
using System.Text.Json.Serialization;

namespace PCBoost.Persistence.Serialization;

/// <summary>Options JSON du stockage local : énumérations en chaînes, <see cref="Core.Common.TextRef"/> fidèle.</summary>
public static class PersistenceJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    /// <summary>Lecture tolérante : null si le JSON est absent ou illisible (une ligne abîmée ne bloque pas l'historique).</summary>
    internal static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General)
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new TextRefJsonConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
