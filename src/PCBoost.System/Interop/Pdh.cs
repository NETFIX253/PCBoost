using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

/// <summary>
/// Performance Data Helper. Les chemins sont ajoutés avec PdhAddEnglishCounterW : les noms anglais fonctionnent
/// sur un Windows localisé (français, etc.).
/// </summary>
internal static unsafe partial class Pdh
{
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;

    public const uint PDH_FMT_LONG = 0x00000100;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_LARGE = 0x00000400;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;

    /// <summary>PDH_FMT_COUNTERVALUE : DWORD CStatus puis une union alignée sur 8 octets (16 octets en x86 comme en x64).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    public struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double doubleValue;

        [FieldOffset(8)]
        public long largeValue;

        [FieldOffset(8)]
        public int longValue;
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W : LPWSTR szName puis la valeur (24 octets en x86 comme en x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PDH_FMT_COUNTERVALUE_ITEM_W
    {
        public char* szName;
        public PDH_FMT_COUNTERVALUE FmtValue;
    }

    [LibraryImport("pdh.dll", EntryPoint = "PdhOpenQueryW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhOpenQuery(string? szDataSource, nuint dwUserData, out SafePdhQueryHandle phQuery);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCloseQuery(nint hQuery);

    [LibraryImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint PdhAddEnglishCounter(SafePdhQueryHandle hQuery, string szFullCounterPath, nuint dwUserData, out nint phCounter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhRemoveCounter(nint hCounter);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhCollectQueryData(SafePdhQueryHandle hQuery);

    [LibraryImport("pdh.dll")]
    public static partial uint PdhGetFormattedCounterValue(nint hCounter, uint dwFormat, out uint lpdwType, out PDH_FMT_COUNTERVALUE pValue);

    [LibraryImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")]
    public static partial uint PdhGetFormattedCounterArray(nint hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, byte* itemBuffer);
}
