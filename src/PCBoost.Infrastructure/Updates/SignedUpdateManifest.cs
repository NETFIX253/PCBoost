using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;

namespace PCBoost.Infrastructure.Updates;

/// <summary>Paquet MSI décrit par un manifeste vérifié.</summary>
internal sealed record UpdatePackage(string Architecture, string FileName, string Url, long Size, string Sha256);

/// <summary>Contenu d'un manifeste dont la signature est valide.</summary>
internal sealed record VerifiedUpdateManifest(
    Version Version,
    DateTimeOffset? PublishedAt,
    string? Notes,
    string? ReleaseUrl,
    IReadOnlyList<UpdatePackage> Packages,
    string KeyId,
    string Json)
{
    /// <summary>Paquet de l'architecture du processus (x64, arm64…), ou null.</summary>
    public UpdatePackage? PackageFor(string architecture)
        => Packages.FirstOrDefault(p => string.Equals(p.Architecture, architecture, StringComparison.OrdinalIgnoreCase));

    public UpdateInfo ToUpdateInfo(UpdatePackage package, string packageLocation)
        => new(Version, packageLocation, package.Sha256.ToUpperInvariant(), Notes, PublishedAt, package.Size, ReleaseUrl, Json);
}

/// <summary>Règles propres à une source : préfixes d'adresses acceptés (null : adresse ignorée, ex. flux local).</summary>
internal sealed record UpdateSourceRules(string? PackageUrlPrefix, string? ReleaseUrlPrefix)
{
    public static UpdateSourceRules Local { get; } = new(null, null);

    /// <summary>« NETFIX253/PCBoost » → paquets sous https://github.com/NETFIX253/PCBoost/releases/download/.</summary>
    public static UpdateSourceRules ForGitHub(string repository)
        => new($"https://github.com/{repository}/releases/download/", $"https://github.com/{repository}/releases/");
}

/// <summary>
/// Manifeste signé <c>pcboost-update.json</c> publié avec chaque version :
/// <code>{ "format": 1, "payload": "&lt;JSON en Base64&gt;", "signatures": [ { "keyId": "…", "sig": "&lt;ECDSA P-256/SHA-256, DER, Base64&gt;" } ] }</code>
/// La charge utile (signée telle quelle, octet pour octet) contient : produit « PCBoost », version, date, notes, page de
/// la version et, par architecture, le paquet MSI (nom, adresse HTTPS, taille, SHA-256). Une seule signature valide d'une
/// clé de <see cref="UpdateTrust"/> suffit ; toute anomalie rend le manifeste invalide (rien n'est téléchargé).
/// </summary>
internal static class SignedUpdateManifest
{
    public const string FileName = "pcboost-update.json";
    public const string Product = "PCBoost";
    public const int MaxBytes = 64 * 1024;
    private const int MaxPayloadBytes = 48 * 1024;
    private const int MaxSignatures = 4;
    private const int MaxPackages = 8;
    private const int MaxNotesLength = 8000;
    private const long MinPackageSize = 1024 * 1024;
    internal const long MaxPackageSize = 2L * 1024 * 1024 * 1024;

    /// <summary>Architecture du processus, au format du manifeste (« x64 », « arm64 », « x86 »).</summary>
    public static string CurrentArchitecture => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    public static OperationResult<VerifiedUpdateManifest> Verify(string? json, IReadOnlyDictionary<string, string> trustedKeys, UpdateSourceRules rules)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        ArgumentNullException.ThrowIfNull(rules);
        json = json?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxBytes) return Invalid("taille");

        byte[] payload;
        string keyId;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Number
                || !format.TryGetInt32(out var formatValue) || formatValue != 1)
            {
                return Invalid("format");
            }
            if (!root.TryGetProperty("payload", out var payloadElement) || payloadElement.ValueKind != JsonValueKind.String) return Invalid("charge utile");
            var encoded = payloadElement.GetString()!;
            if (encoded.Length > MaxPayloadBytes * 4 / 3 + 4) return Invalid("charge utile");
            payload = Convert.FromBase64String(encoded);
            if (!root.TryGetProperty("signatures", out var signatures) || signatures.ValueKind != JsonValueKind.Array) return Invalid("signatures");
            if (!TryFindValidSignature(payload, signatures, trustedKeys, out keyId)) return Fail("Infra_Update_SignatureInvalid", "signature");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            return Invalid("JSON");
        }

        return ParsePayload(payload, keyId, json, rules);
    }

    private static bool TryFindValidSignature(byte[] payload, JsonElement signatures, IReadOnlyDictionary<string, string> trustedKeys, out string keyId)
    {
        keyId = string.Empty;
        var count = 0;
        foreach (var entry in signatures.EnumerateArray())
        {
            if (++count > MaxSignatures) return false;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("keyId", out var id) || id.ValueKind != JsonValueKind.String) continue;
            if (!entry.TryGetProperty("sig", out var sig) || sig.ValueKind != JsonValueKind.String) continue;
            var name = id.GetString()!;
            if (!trustedKeys.TryGetValue(name, out var publicKey)) continue;
            byte[] signature;
            try
            {
                signature = Convert.FromBase64String(sig.GetString()!);
            }
            catch (FormatException)
            {
                continue;
            }
            if (signature.Length is 0 or > 80) continue;
            if (IsValidSignature(payload, signature, publicKey))
            {
                keyId = name;
                return true;
            }
        }
        return false;
    }

    private static bool IsValidSignature(byte[] payload, byte[] signature, string publicKeyBase64)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return false;
        }
    }

    private static OperationResult<VerifiedUpdateManifest> ParsePayload(byte[] payload, string keyId, string json, UpdateSourceRules rules)
    {
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("objet");
            if (!string.Equals(Text(root, "product"), Product, StringComparison.Ordinal)) return Invalid("produit");
            if (!TryParseVersion(Text(root, "version"), out var version)) return Invalid("version");

            DateTimeOffset? publishedAt = null;
            if (Text(root, "publishedAt") is { } date)
            {
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)) return Invalid("date");
                publishedAt = parsed;
            }

            var notes = Text(root, "notes");
            if (notes is not null)
            {
                if (notes.Length > MaxNotesLength) return Invalid("notes");
                notes = new string(notes.Where(c => c == '\n' || !char.IsControl(c)).ToArray()).Trim();
                if (notes.Length == 0) notes = null;
            }

            var releaseUrl = Text(root, "releaseUrl");
            if (releaseUrl is not null && !IsAllowedUrl(releaseUrl, rules.ReleaseUrlPrefix)) return Invalid("page de la version");

            if (!root.TryGetProperty("packages", out var packagesElement) || packagesElement.ValueKind != JsonValueKind.Array) return Invalid("paquets");
            var packages = new List<UpdatePackage>();
            foreach (var item in packagesElement.EnumerateArray())
            {
                if (packages.Count >= MaxPackages) return Invalid("paquets");
                if (!TryParsePackage(item, rules, out var package)) return Invalid("paquet");
                packages.Add(package);
            }
            if (packages.Count == 0) return Invalid("paquets");

            return OperationResult<VerifiedUpdateManifest>.Ok(new VerifiedUpdateManifest(version, publishedAt, notes, releaseUrl, packages, keyId, json));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Invalid("charge utile");
        }
    }

    private static bool TryParsePackage(JsonElement item, UpdateSourceRules rules, out UpdatePackage package)
    {
        package = null!;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var architecture = Text(item, "arch");
        if (architecture is not ("x64" or "arm64" or "x86")) return false;
        if (!string.Equals(Text(item, "kind"), "msi", StringComparison.Ordinal)) return false;
        var fileName = Text(item, "fileName");
        if (!PathContainment.IsPlainFileName(fileName) || fileName!.Length > 120 || !fileName.EndsWith(".msi", StringComparison.OrdinalIgnoreCase)) return false;
        var url = Text(item, "url");
        if (url is null || !IsAllowedUrl(url, rules.PackageUrlPrefix) || !url.EndsWith("/" + Uri.EscapeDataString(fileName), StringComparison.Ordinal)) return false;
        if (!item.TryGetProperty("size", out var sizeElement) || sizeElement.ValueKind != JsonValueKind.Number
            || !sizeElement.TryGetInt64(out var size) || size is < MinPackageSize or > MaxPackageSize)
        {
            return false;
        }
        var sha = Text(item, "sha256");
        if (!Sha256Hash.IsValid(sha)) return false;
        package = new UpdatePackage(architecture, fileName, url, size, sha!.ToUpperInvariant());
        return true;
    }

    /// <summary>Adresse HTTPS absolue, sans identifiants ni fragment, commençant par le préfixe de la source s'il existe.</summary>
    private static bool IsAllowedUrl(string url, string? prefix)
    {
        if (url.Length > 512 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || !uri.IsDefaultPort) return false;
        return prefix is null || url.StartsWith(prefix, StringComparison.Ordinal);
    }

    /// <summary>Version « a.b », « a.b.c » ou « a.b.c.d » (nombres de 0 à 65535).</summary>
    internal static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 23) return false;
        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4) return false;
        var numbers = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length is 0 or > 5 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) || numbers[i] > 65535)
            {
                return false;
            }
        }
        version = parts.Length switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static OperationResult<VerifiedUpdateManifest> Invalid(string detail) => Fail("Infra_Update_FeedInvalid", detail);

    private static OperationResult<VerifiedUpdateManifest> Fail(string key, string detail)
        => OperationResult<VerifiedUpdateManifest>.Fail(OperationErrorKind.Blocked, TextRef.Of(key), detail);

    /// <summary>Encodage UTF-8 strict (pour la lecture des flux).</summary>
    internal static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
