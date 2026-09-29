using System.Runtime.InteropServices;

namespace PCBoost.Platform.Interop;

internal static unsafe partial class User32
{
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint GW_OWNER = 4;
    public const uint MONITOR_DEFAULTTONULL = 0x00000000;
    public const nint HWND_BROADCAST = 0xFFFF;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    public const uint SPIF_UPDATEINIFILE = 0x01;
    public const uint SPIF_SENDCHANGE = 0x02;

    public const uint SPI_GETDRAGFULLWINDOWS = 0x0026;
    public const uint SPI_SETDRAGFULLWINDOWS = 0x0025;
    public const uint SPI_GETANIMATION = 0x0048;
    public const uint SPI_SETANIMATION = 0x0049;
    public const uint SPI_GETMENUANIMATION = 0x1002;
    public const uint SPI_SETMENUANIMATION = 0x1003;
    public const uint SPI_GETCOMBOBOXANIMATION = 0x1004;
    public const uint SPI_SETCOMBOBOXANIMATION = 0x1005;
    public const uint SPI_GETLISTBOXSMOOTHSCROLLING = 0x1006;
    public const uint SPI_SETLISTBOXSMOOTHSCROLLING = 0x1007;
    public const uint SPI_GETTOOLTIPANIMATION = 0x1016;
    public const uint SPI_SETTOOLTIPANIMATION = 0x1017;
    public const uint SPI_GETCURSORSHADOW = 0x101A;
    public const uint SPI_SETCURSORSHADOW = 0x101B;
    public const uint SPI_GETCLIENTAREAANIMATION = 0x1042;
    public const uint SPI_SETCLIENTAREAANIMATION = 0x1043;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ANIMATIONINFO
    {
        public uint cbSize;
        public int iMinAnimate;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> lpEnumFunc, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    public static partial nint GetWindow(nint hWnd, uint uCmd);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextLengthW")]
    public static partial int GetWindowTextLength(nint hWnd);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowTextW")]
    public static partial int GetWindowText(nint hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hWnd, out RECT lpRect);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint uiAction, uint uiParam, void* pvParam, uint fWinIni);

    [LibraryImport("user32.dll", SetLastError = true, EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint SendMessageTimeout(nint hWnd, uint msg, nuint wParam, string lParam, uint fuFlags, uint uTimeout, out nuint lpdwResult);
}

internal static unsafe partial class DwmApi
{
    public const uint DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, void* pvAttribute, uint cbAttribute);
}
