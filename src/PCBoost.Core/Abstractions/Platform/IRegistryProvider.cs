using PCBoost.Core.Common;

namespace PCBoost.Core.Abstractions.Platform;

public enum RegistryHiveKind { CurrentUser = 0, LocalMachine = 1 }

public enum RegistryViewKind { Default = 0, Registry32 = 1, Registry64 = 2 }

public enum RegistryValueType { Unknown = 0, String, ExpandString, DWord, QWord, Binary, MultiString }

/// <summary>Valeur de registre typée, sérialisable pour la restauration.</summary>
public sealed record RegistryValueData(RegistryValueType Type, object Value)
{
    public static RegistryValueData String(string value) => new(RegistryValueType.String, value);
    public static RegistryValueData DWord(int value) => new(RegistryValueType.DWord, value);
    public static RegistryValueData Binary(byte[] value) => new(RegistryValueType.Binary, value);
}

public sealed record RegistryLocation(RegistryHiveKind Hive, string KeyPath, RegistryViewKind View = RegistryViewKind.Default)
{
    public override string ToString() => $"{(Hive == RegistryHiveKind.CurrentUser ? "HKCU" : "HKLM")}\\{KeyPath}";
}

/// <summary>Accès au registre. Les implémentations ne lèvent pas d'exception : elles renvoient des résultats.</summary>
public interface IRegistryProvider
{
    bool KeyExists(RegistryLocation location);

    IReadOnlyList<string> GetSubKeyNames(RegistryLocation location);

    IReadOnlyList<string> GetValueNames(RegistryLocation location);

    RegistryValueData? GetValue(RegistryLocation location, string valueName);

    OperationResult SetValue(RegistryLocation location, string valueName, RegistryValueData data);

    OperationResult DeleteValue(RegistryLocation location, string valueName);

    /// <summary>Le processus courant peut-il écrire dans cette clé sans élévation ?</summary>
    bool CanWrite(RegistryLocation location);
}
