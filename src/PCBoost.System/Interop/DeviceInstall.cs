using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

/// <summary>SetupAPI et NewDev : ouverture d'un périphérique par identifiant d'instance et retour au pilote précédent.</summary>
internal static partial class DeviceInstall
{
    /// <summary>ROLLBACK_FLAG_NO_UI : aucune fenêtre (le résultat est vérifié par PCBoost).</summary>
    public const uint ROLLBACK_FLAG_NO_UI = 0x00000001;

    public const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);
    public const int ERROR_NO_MORE_ITEMS = 259;

    /// <summary>SP_DEVINFO_DATA : 32 octets en 64 bits (alignement naturel du dernier champ pointeur).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nint Reserved;
    }

    [LibraryImport("setupapi.dll", SetLastError = true)]
    public static partial nint SetupDiCreateDeviceInfoList(nint classGuid, nint hwndParent);

    [LibraryImport("setupapi.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiOpenDeviceInfoW(nint deviceInfoSet, string deviceInstanceId, nint hwndParent, uint openFlags, ref SP_DEVINFO_DATA deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DiRollbackDriver(nint deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, nint hwndParent, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);
}
