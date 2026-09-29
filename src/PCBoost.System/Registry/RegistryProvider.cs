using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Security;

namespace PCBoost.Platform;

/// <summary>
/// Accès au registre (Microsoft.Win32) avec vues 32/64 bits. Aucune exception n'est propagée.
/// Défense en profondeur : toute écriture ou suppression visant une clé interdite par <see cref="ForbiddenTargetPolicy"/> est refusée.
/// </summary>
public sealed class RegistryProvider : IRegistryProvider
{
    private readonly ILogger<RegistryProvider> _logger;

    public RegistryProvider(ILogger<RegistryProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<RegistryProvider>.Instance;
    }

    public bool KeyExists(RegistryLocation location)
    {
        try
        {
            using var key = Open(location, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    public IReadOnlyList<string> GetSubKeyNames(RegistryLocation location)
    {
        try
        {
            using var key = Open(location, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogDebug(ex, "Sous-clés illisibles : {Location}", location);
            return [];
        }
    }

    public IReadOnlyList<string> GetValueNames(RegistryLocation location)
    {
        try
        {
            using var key = Open(location, writable: false);
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogDebug(ex, "Valeurs illisibles : {Location}", location);
            return [];
        }
    }

    public RegistryValueData? GetValue(RegistryLocation location, string valueName)
    {
        try
        {
            using var key = Open(location, writable: false);
            if (key is null) return null;
            var raw = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return null;
            return RegistryValueConverter.FromRegistry(key.GetValueKind(valueName), raw);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogDebug(ex, "Valeur illisible : {Location}", location);
            return null;
        }
    }

    public OperationResult SetValue(RegistryLocation location, string valueName, RegistryValueData data)
    {
        if (ForbiddenTargetPolicy.IsForbiddenRegistryLocation(location))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"), location.ToString());
        if (!RegistryValueConverter.TryToRegistry(data, out var kind, out var value))
            return OperationResult.Fail(OperationErrorKind.InvalidInput, null, $"Type {data.Type} / {data.Value?.GetType().Name}");

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToHive(location.Hive), ToView(location.View));
            using var key = baseKey.CreateSubKey(location.KeyPath.Trim('\\'), writable: true);
            key.SetValue(valueName, value, kind);
            _logger.LogInformation("Registre modifié : {Location} [{Name}]", location, valueName);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogWarning("Écriture refusée : {Location} [{Name}] ({Error})", location, valueName, ex.GetType().Name);
            return OperationResult.FromException(ex);
        }
    }

    /// <summary>Idempotent : une valeur (ou une clé) déjà absente est un succès.</summary>
    public OperationResult DeleteValue(RegistryLocation location, string valueName)
    {
        if (ForbiddenTargetPolicy.IsForbiddenRegistryLocation(location))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"), location.ToString());

        try
        {
            using var key = Open(location, writable: true);
            if (key is null) return OperationResult.Ok();
            key.DeleteValue(valueName, throwOnMissingValue: false);
            _logger.LogInformation("Valeur de registre supprimée : {Location} [{Name}]", location, valueName);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            _logger.LogWarning("Suppression refusée : {Location} [{Name}] ({Error})", location, valueName, ex.GetType().Name);
            return OperationResult.FromException(ex);
        }
    }

    public bool CanWrite(RegistryLocation location)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(ToHive(location.Hive), ToView(location.View));
            // Clé absente : on teste le parent existant le plus proche (la clé y sera créée).
            var path = location.KeyPath.Trim('\\');
            while (path.Length > 0)
            {
                using (var existing = baseKey.OpenSubKey(path, writable: false))
                {
                    if (existing is not null) return TryOpenWritable(baseKey, path);
                }
                var separator = path.LastIndexOf('\\');
                path = separator < 0 ? string.Empty : path[..separator];
            }
            // Racine de ruche : HKCU appartient à l'utilisateur ; HKLM exige l'élévation.
            return location.Hive == RegistryHiveKind.CurrentUser || Interop.TokenHelper.IsCurrentProcessElevated;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    private static bool TryOpenWritable(RegistryKey baseKey, string path)
    {
        try
        {
            using var key = baseKey.OpenSubKey(path, writable: true);
            return key is not null;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    private static RegistryKey? Open(RegistryLocation location, bool writable)
    {
        using var baseKey = RegistryKey.OpenBaseKey(ToHive(location.Hive), ToView(location.View));
        return baseKey.OpenSubKey(location.KeyPath.Trim('\\'), writable);
    }

    internal static RegistryHive ToHive(RegistryHiveKind hive) => hive switch
    {
        RegistryHiveKind.LocalMachine => RegistryHive.LocalMachine,
        _ => RegistryHive.CurrentUser,
    };

    internal static RegistryView ToView(RegistryViewKind view) => view switch
    {
        RegistryViewKind.Registry32 => RegistryView.Registry32,
        RegistryViewKind.Registry64 => RegistryView.Registry64,
        _ => RegistryView.Default,
    };

    private static bool IsExpected(Exception ex)
        => ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or ObjectDisposedException;
}

/// <summary>Conversion entre les types du registre et <see cref="RegistryValueData"/>.</summary>
internal static class RegistryValueConverter
{
    public static RegistryValueData FromRegistry(RegistryValueKind kind, object raw) => kind switch
    {
        RegistryValueKind.String => new RegistryValueData(RegistryValueType.String, raw as string ?? raw.ToString() ?? string.Empty),
        RegistryValueKind.ExpandString => new RegistryValueData(RegistryValueType.ExpandString, raw as string ?? raw.ToString() ?? string.Empty),
        RegistryValueKind.DWord => new RegistryValueData(RegistryValueType.DWord, Convert.ToInt32(raw, CultureInfo.InvariantCulture)),
        RegistryValueKind.QWord => new RegistryValueData(RegistryValueType.QWord, Convert.ToInt64(raw, CultureInfo.InvariantCulture)),
        RegistryValueKind.Binary => new RegistryValueData(RegistryValueType.Binary, raw as byte[] ?? []),
        RegistryValueKind.MultiString => new RegistryValueData(RegistryValueType.MultiString, raw as string[] ?? []),
        _ => new RegistryValueData(RegistryValueType.Unknown, raw),
    };

    public static bool TryToRegistry(RegistryValueData data, out RegistryValueKind kind, out object value)
    {
        kind = RegistryValueKind.Unknown;
        value = string.Empty;
        try
        {
            switch (data.Type)
            {
                case RegistryValueType.String:
                    kind = RegistryValueKind.String;
                    value = Convert.ToString(data.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                case RegistryValueType.ExpandString:
                    kind = RegistryValueKind.ExpandString;
                    value = Convert.ToString(data.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                    return true;
                case RegistryValueType.DWord:
                    kind = RegistryValueKind.DWord;
                    value = data.Value switch
                    {
                        uint u => unchecked((int)u),
                        long l => unchecked((int)l),
                        ulong ul => unchecked((int)ul),
                        _ => Convert.ToInt32(data.Value, CultureInfo.InvariantCulture),
                    };
                    return true;
                case RegistryValueType.QWord:
                    kind = RegistryValueKind.QWord;
                    value = data.Value is ulong q ? unchecked((long)q) : Convert.ToInt64(data.Value, CultureInfo.InvariantCulture);
                    return true;
                case RegistryValueType.Binary when data.Value is byte[] bytes:
                    kind = RegistryValueKind.Binary;
                    value = bytes;
                    return true;
                case RegistryValueType.MultiString when data.Value is string[] strings:
                    kind = RegistryValueKind.MultiString;
                    value = strings;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return false;
        }
    }
}
