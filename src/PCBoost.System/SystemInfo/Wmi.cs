using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace PCBoost.Platform;

/// <summary>Requêtes WMI synchrones, sans exception propagée (liste vide en cas d'échec).</summary>
internal static class Wmi
{
    public const string CimV2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string RootWmi = @"root\WMI";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);

    public static List<T> Query<T>(string scope, string wql, Func<ManagementBaseObject, T?> map, ILogger logger)
        where T : class
    {
        TryQuery(scope, wql, map, logger, out var results);
        return results;
    }

    /// <summary>
    /// Comme <see cref="Query{T}"/>, mais indique si la requête a abouti : false si elle a échoué ou s'est interrompue en
    /// cours d'énumération (délai dépassé…) — les lignes déjà lues sont alors incomplètes et ne doivent pas servir de
    /// référence pour une décision (état « avant » d'un pilote, revérification).
    /// </summary>
    public static bool TryQuery<T>(string scope, string wql, Func<ManagementBaseObject, T?> map, ILogger logger, out List<T> results)
        where T : class
    {
        results = new List<T>();
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(wql), options);
            using var collection = searcher.Get();
            foreach (var item in collection)
            {
                using (item)
                {
                    var mapped = map(item);
                    if (mapped is not null) results.Add(mapped);
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            logger.LogDebug(ex, "Requête WMI indisponible ({Scope} : {Query})", scope, wql);
            return false;
        }
        return true;
    }

    public static string? String(ManagementBaseObject item, string property)
    {
        var value = Get(item, property)?.ToString()?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static long? Int64(ManagementBaseObject item, string property)
    {
        var value = Get(item, property);
        if (value is null) return null;
        try
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static int? Int32(ManagementBaseObject item, string property)
    {
        var value = Int64(item, property);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value.Value : null;
    }

    /// <summary>Propriété tableau de chaînes (ex. Win32_PnPEntity.HardwareID) ; liste vide si absente.</summary>
    public static IReadOnlyList<string> Strings(ManagementBaseObject item, string property)
        => Get(item, property) is string[] values
            ? values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray()
            : [];

    public static bool? Bool(ManagementBaseObject item, string property)
        => Get(item, property) is bool value ? value : null;

    /// <summary>Propriété CIM char16 (ex. MSFT_Partition.DriveLetter), renvoyée par System.Management comme char ou UInt16.</summary>
    public static char? Char(ManagementBaseObject item, string property)
    {
        var value = Get(item, property);
        var c = value switch
        {
            char ch => ch,
            ushort u => (char)u,
            string s when s.Length > 0 => s[0],
            _ => '\0',
        };
        return c == '\0' ? null : c;
    }

    private static object? Get(ManagementBaseObject item, string property)
    {
        try
        {
            return item[property];
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
