using System.Collections.Concurrent;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.TestUtilities;

/// <summary>Registre en mémoire. Les clés HKLM peuvent être déclarées non inscriptibles (simulation non-admin).</summary>
public sealed class InMemoryRegistryProvider : IRegistryProvider
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RegistryValueData>> _keys = new(StringComparer.OrdinalIgnoreCase);

    public bool LocalMachineWritable { get; set; } = true;

    public int WriteCount { get; private set; }

    private static string K(RegistryLocation l) => $"{l.Hive}|{l.View}|{l.KeyPath.Trim('\\')}";

    public void Set(RegistryHiveKind hive, string keyPath, string valueName, RegistryValueData data, RegistryViewKind view = RegistryViewKind.Default)
        => _keys.GetOrAdd(K(new RegistryLocation(hive, keyPath, view)), _ => new(StringComparer.OrdinalIgnoreCase))[valueName] = data;

    public void CreateKey(RegistryHiveKind hive, string keyPath, RegistryViewKind view = RegistryViewKind.Default)
        => _keys.GetOrAdd(K(new RegistryLocation(hive, keyPath, view)), _ => new(StringComparer.OrdinalIgnoreCase));

    public bool KeyExists(RegistryLocation location) => _keys.ContainsKey(K(location));

    public IReadOnlyList<string> GetSubKeyNames(RegistryLocation location)
    {
        var prefix = K(location) + "\\";
        return _keys.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> GetValueNames(RegistryLocation location)
        => _keys.TryGetValue(K(location), out var v) ? v.Keys.ToList() : [];

    public RegistryValueData? GetValue(RegistryLocation location, string valueName)
        => _keys.TryGetValue(K(location), out var v) && v.TryGetValue(valueName, out var d) ? d : null;

    public OperationResult SetValue(RegistryLocation location, string valueName, RegistryValueData data)
    {
        if (!CanWrite(location)) return OperationResult.Fail(OperationErrorKind.RequiresElevation);
        _keys.GetOrAdd(K(location), _ => new(StringComparer.OrdinalIgnoreCase))[valueName] = data;
        WriteCount++;
        return OperationResult.Ok();
    }

    public OperationResult DeleteValue(RegistryLocation location, string valueName)
    {
        if (!CanWrite(location)) return OperationResult.Fail(OperationErrorKind.RequiresElevation);
        if (_keys.TryGetValue(K(location), out var v)) v.TryRemove(valueName, out _);
        WriteCount++;
        return OperationResult.Ok();
    }

    public bool CanWrite(RegistryLocation location) => location.Hive == RegistryHiveKind.CurrentUser || LocalMachineWritable;
}
