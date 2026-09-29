namespace PCBoost.Gaming.Common;

/// <summary>
/// Chaîne de paramètres DirectX au format « clé=valeur;clé=valeur; » (UserGpuPreferences). L'ordre et les paires inconnues
/// sont conservés : seule la clé demandée est modifiée.
/// </summary>
internal sealed class DirectXSettingsString
{
    private readonly List<Entry> _entries = [];

    private DirectXSettingsString()
    {
    }

    public static DirectXSettingsString Parse(string? value)
    {
        var result = new DirectXSettingsString();
        if (string.IsNullOrWhiteSpace(value)) return result;
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            var key = (eq < 0 ? part : part[..eq]).Trim();
            if (key.Length == 0) continue;
            result.SetCore(key, eq < 0 ? null : part[(eq + 1)..].Trim());
        }
        return result;
    }

    public int Count => _entries.Count;

    public string? Get(string key)
        => _entries.FirstOrDefault(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

    public void Set(string key, string value) => SetCore(key, value);

    private void SetCore(string key, string? value)
    {
        var index = _entries.FindIndex(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) _entries[index] = _entries[index] with { Value = value };
        else _entries.Add(new Entry(key, value));
    }

    public override string ToString() => string.Concat(_entries.Select(e => e.Value is null ? $"{e.Key};" : $"{e.Key}={e.Value};"));

    private sealed record Entry(string Key, string? Value);
}
