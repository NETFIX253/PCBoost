using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.Platform.Elevation;

/// <summary>Sérialisation JSON partagée entre l'application et PCBoost.Elevator.</summary>
internal static class ElevationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public static byte[] SerializeResponse(ElevatedResponse response)
        => JsonSerializer.SerializeToUtf8Bytes(Sanitize(response), Options);

    public static ElevatedResponse? DeserializeResponse(ReadOnlySpan<byte> json)
    {
        try
        {
            var response = JsonSerializer.Deserialize<ElevatedResponse>(json, Options);
            if (response?.Outcome is null) return null;
            return Sanitize(response with { Data = response.Data ?? new Dictionary<string, string>() });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Les arguments d'un <see cref="TextRef"/> désérialisés sont des JsonElement : ils sont ramenés à des types simples
    /// (chaîne, entier, réel, booléen) pour la mise en forme par le localiseur.
    /// </summary>
    private static ElevatedResponse Sanitize(ElevatedResponse response)
    {
        var outcome = response.Outcome;
        if (outcome.Message is null) return response;
        var args = outcome.Message.Args ?? [];
        var normalized = args.Select(NormalizeArgument).ToArray();
        return response with { Outcome = outcome with { Message = new TextRef(outcome.Message.Key, normalized) } };
    }

    private static object NormalizeArgument(object? value) => value switch
    {
        null => string.Empty,
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString() ?? string.Empty,
        JsonElement { ValueKind: JsonValueKind.Number } e when e.TryGetInt64(out var l) => l,
        JsonElement { ValueKind: JsonValueKind.Number } e => e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement e => e.GetRawText(),
        string or bool or int or long or double => value,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>Encodage de la requête sur la ligne de commande : JSON en base64url (alphabet [A-Za-z0-9_-], sans remplissage).</summary>
internal static class ElevatedRequestCodec
{
    public const int MaxEncodedLength = 16 * 1024;

    public static string Encode(ElevatedRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.SerializeToUtf8Bytes(request, ElevationJson.Options);
        return Base64Url.EncodeToString(json);
    }

    public static bool TryDecode(string? encoded, out ElevatedRequest? request)
    {
        request = null;
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxEncodedLength) return false;
        foreach (var c in encoded)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        }

        byte[] json;
        try
        {
            json = Base64Url.DecodeFromChars(encoded);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            var decoded = JsonSerializer.Deserialize<ElevatedRequest>(json, ElevationJson.Options);
            if (decoded?.Operation is null || decoded.Parameters is null) return false;
            if (decoded.Parameters.Any(p => p.Key is null || p.Value is null)) return false;
            request = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Arguments de PCBoost.Elevator : exactement « --request &lt;base64url&gt; --result &lt;chemin&gt; » (ordre libre).</summary>
internal static class ElevatorArguments
{
    public const string RequestSwitch = "--request";
    public const string ResultSwitch = "--result";

    public static bool TryParse(IReadOnlyList<string>? args, out string encodedRequest, out string resultPath)
    {
        encodedRequest = string.Empty;
        resultPath = string.Empty;
        if (args is null || args.Count != 4) return false;

        string? request = null, result = null;
        for (var i = 0; i < 4; i += 2)
        {
            var name = args[i];
            var value = args[i + 1];
            if (string.IsNullOrEmpty(value) || value.StartsWith("--", StringComparison.Ordinal)) return false;
            if (string.Equals(name, RequestSwitch, StringComparison.Ordinal) && request is null) request = value;
            else if (string.Equals(name, ResultSwitch, StringComparison.Ordinal) && result is null) result = value;
            else return false;
        }
        if (request is null || result is null) return false;
        encodedRequest = request;
        resultPath = result;
        return true;
    }

    /// <summary>Ligne de commande : base64url sans caractère spécial ; le chemin validé ne contient jamais de guillemet.</summary>
    public static string Build(string encodedRequest, string resultPath)
    {
        if (!ElevationPaths.IsValidResultPath(resultPath)) throw new ArgumentException("Chemin de résultat invalide.", nameof(resultPath));
        return $"{RequestSwitch} {encodedRequest} {ResultSwitch} \"{resultPath}\"";
    }
}
