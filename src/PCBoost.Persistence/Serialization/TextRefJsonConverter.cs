using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCBoost.Core.Common;

namespace PCBoost.Persistence.Serialization;

/// <summary>
/// Sérialise <see cref="TextRef"/> en conservant le type des arguments : <c>{"key":"…","args":[…]}</c>.
/// Les chaînes, booléens, entiers 32 bits et null sont écrits tels quels ; les autres types sont étiquetés
/// (<c>{"$type":"double","value":2}</c>) pour qu'un <c>double</c> entier ne revienne pas en <c>int</c>, ni un <c>long</c> en <c>int</c>.
/// À la lecture, un nombre non étiqueté (données écrites par un autre outil) devient int, long ou double selon sa valeur.
/// Aucun type n'est chargé dynamiquement depuis les données (pas de nom de type .NET stocké).
/// </summary>
public sealed class TextRefJsonConverter : JsonConverter<TextRef>
{
    private const string KeyProperty = "key";
    private const string ArgsProperty = "args";
    private const string TypeProperty = "$type";
    private const string ValueProperty = "value";
    private const int MaxNestingDepth = 8;

    public override TextRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        using var document = JsonDocument.ParseValue(ref reader);
        return ReadTextRef(document.RootElement, 0);
    }

    public override void Write(Utf8JsonWriter writer, TextRef value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        WriteTextRef(writer, value, 0);
    }

    private static void WriteTextRef(Utf8JsonWriter writer, TextRef value, int depth)
    {
        writer.WriteStartObject();
        writer.WriteString(KeyProperty, value.Key);
        writer.WriteStartArray(ArgsProperty);
        foreach (var arg in value.Args ?? [])
            WriteArgument(writer, arg, depth);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteArgument(Utf8JsonWriter writer, object? arg, int depth)
    {
        switch (arg)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case int i: writer.WriteNumberValue(i); break;
            case short s16: writer.WriteNumberValue(s16); break;
            case ushort u16: writer.WriteNumberValue(u16); break;
            case byte u8: writer.WriteNumberValue(u8); break;
            case sbyte s8: writer.WriteNumberValue(s8); break;
            case char c: writer.WriteStringValue(c.ToString()); break;
            case long l: WriteTagged(writer, "long", w => w.WriteNumberValue(l)); break;
            case uint u32: WriteTagged(writer, "long", w => w.WriteNumberValue((long)u32)); break;
            case ulong u64 when u64 <= long.MaxValue: WriteTagged(writer, "long", w => w.WriteNumberValue((long)u64)); break;
            case ulong u64: WriteTagged(writer, "ulong", w => w.WriteNumberValue(u64)); break;
            case double d when double.IsFinite(d): WriteTagged(writer, "double", w => w.WriteNumberValue(d)); break;
            case double d: WriteTagged(writer, "double", w => w.WriteStringValue(d.ToString("R", CultureInfo.InvariantCulture))); break;
            case float f when float.IsFinite(f): WriteTagged(writer, "float", w => w.WriteNumberValue(f)); break;
            case float f: WriteTagged(writer, "float", w => w.WriteStringValue(f.ToString("R", CultureInfo.InvariantCulture))); break;
            case decimal m: WriteTagged(writer, "decimal", w => w.WriteNumberValue(m)); break;
            case DateTimeOffset dto: WriteTagged(writer, "datetimeoffset", w => w.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture))); break;
            case DateTime dt: WriteTagged(writer, "datetime", w => w.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture))); break;
            case TimeSpan ts: WriteTagged(writer, "timespan", w => w.WriteStringValue(ts.ToString("c", CultureInfo.InvariantCulture))); break;
            case Guid g: WriteTagged(writer, "guid", w => w.WriteStringValue(g.ToString("D"))); break;
            case TextRef nested when depth < MaxNestingDepth: WriteTagged(writer, "text", w => WriteTextRef(w, nested, depth + 1)); break;
            case TextRef nested: writer.WriteStringValue(nested.ToString()); break;
            case JsonElement element: element.WriteTo(writer); break;
            // Énumérations et autres types : représentation textuelle (identique à l'affichage par string.Format).
            case IFormattable formattable: writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture)); break;
            default: writer.WriteStringValue(arg.ToString()); break;
        }
    }

    private static void WriteTagged(Utf8JsonWriter writer, string type, Action<Utf8JsonWriter> writeValue)
    {
        writer.WriteStartObject();
        writer.WriteString(TypeProperty, type);
        writer.WritePropertyName(ValueProperty);
        writeValue(writer);
        writer.WriteEndObject();
    }

    private static TextRef ReadTextRef(JsonElement element, int depth)
    {
        if (element.ValueKind == JsonValueKind.String)
            return TextRef.Literal(element.GetString() ?? string.Empty);
        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("TextRef attendu (objet JSON).");

        string? key = null;
        object[] args = [];
        foreach (var property in element.EnumerateObject())
        {
            if (property.NameEquals(KeyProperty) || string.Equals(property.Name, "Key", StringComparison.OrdinalIgnoreCase))
            {
                key = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            }
            else if (property.NameEquals(ArgsProperty) || string.Equals(property.Name, "Args", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                    args = property.Value.EnumerateArray().Select(a => ReadArgument(a, depth)).ToArray()!;
            }
        }

        if (string.IsNullOrEmpty(key)) throw new JsonException("TextRef sans clé.");
        return new TextRef(key, args);
    }

    private static object? ReadArgument(JsonElement element, int depth)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                return ReadUntaggedNumber(element);
            case JsonValueKind.Object when element.TryGetProperty(TypeProperty, out var typeElement)
                                        && element.TryGetProperty(ValueProperty, out var valueElement):
                return ReadTagged(typeElement.GetString(), valueElement, depth);
            default:
                return element.GetRawText();
        }
    }

    private static object ReadUntaggedNumber(JsonElement element)
    {
        if (element.TryGetInt32(out var i)) return i;
        if (element.TryGetInt64(out var l)) return l;
        return element.GetDouble();
    }

    private static object? ReadTagged(string? type, JsonElement value, int depth)
    {
        try
        {
            return type switch
            {
                "long" => value.GetInt64(),
                "ulong" => value.GetUInt64(),
                "double" => value.ValueKind == JsonValueKind.String
                    ? double.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : value.GetDouble(),
                "float" => value.ValueKind == JsonValueKind.String
                    ? float.Parse(value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)
                    : value.GetSingle(),
                "decimal" => value.GetDecimal(),
                "datetimeoffset" => DateTimeOffset.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "datetime" => DateTime.Parse(value.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                "timespan" => TimeSpan.ParseExact(value.GetString()!, "c", CultureInfo.InvariantCulture),
                "guid" => Guid.Parse(value.GetString()!),
                "text" when depth < MaxNestingDepth => ReadTextRef(value, depth + 1),
                _ => ReadArgument(value, depth),
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new JsonException($"Argument TextRef de type « {type} » invalide.", ex);
        }
    }
}
