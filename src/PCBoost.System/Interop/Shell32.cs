using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

internal static unsafe partial class Shell32
{
    public static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid FOLDERID_Startup = new("B97D20BB-F46A-4C97-BA10-5E3608430854");
    public static readonly Guid FOLDERID_CommonStartup = new("82A5EA35-D9CD-47C5-9629-E15D2F714E6E");

    /// <summary>KF_FLAG_DONT_VERIFY : ne crée ni ne vérifie le dossier.</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    public const uint SHERB_NOCONFIRMATION = 0x00000001;
    public const uint SHERB_NOPROGRESSUI = 0x00000002;
    public const uint SHERB_NOSOUND = 0x00000004;

    public const uint SHOP_FILEPATH = 0x00000002;

    /// <summary>
    /// SHQUERYRBINFO : shellapi.h applique pshpack1 uniquement en 32 bits. En 64 bits (x64, ARM64) l'alignement naturel
    /// donne 24 octets ; en 32 bits la structure fait 20 octets (voir <see cref="SHQUERYRBINFO32"/>).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SHQUERYRBINFO
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct SHQUERYRBINFO32
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    public const uint FO_DELETE = 0x0003;
    public const ushort FOF_NOCONFIRMATION = 0x0010;
    public const ushort FOF_ALLOWUNDO = 0x0040;
    public const ushort FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>SHFILEOPSTRUCTW (alignement naturel en 64 bits ; pshpack1 ne s'applique qu'au 32 bits, non pris en charge).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SHFILEOPSTRUCTW
    {
        public nint hwnd;
        public uint wFunc;
        public char* pFrom;
        public char* pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public char* lpszProgressTitle;
    }

    [LibraryImport("shell32.dll", EntryPoint = "SHFileOperationW")]
    public static partial int SHFileOperation(ref SHFILEOPSTRUCTW lpFileOp);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out char* ppszPath);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHQueryRecycleBin32(string? pszRootPath, ref SHQUERYRBINFO32 pSHQueryRBInfo);

    [LibraryImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SHObjectProperties(nint hwnd, uint shopObjectType, string pszObjectName, string? pszPropertyPage);
}

internal static unsafe partial class PowrProf
{
    /// <summary>POWER_DATA_ACCESSOR.ACCESS_SCHEME</summary>
    public const uint ACCESS_SCHEME = 16;

    public const uint ERROR_SUCCESS = 0;
    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_NO_MORE_ITEMS = 259;

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerGetActiveScheme(nint userRootPowerKey, out nint activePolicyGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerSetActiveScheme(nint userRootPowerKey, Guid* schemeGuid);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerEnumerate(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupOfPowerSettingsGuid, uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    public static partial uint PowerReadFriendlyName(nint rootPowerKey, Guid* schemeGuid, Guid* subGroupOfPowerSettingsGuid, Guid* powerSettingGuid, byte* buffer, ref uint bufferSize);
}
