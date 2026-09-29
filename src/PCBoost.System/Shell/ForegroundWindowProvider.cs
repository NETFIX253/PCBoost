using PCBoost.Core.Abstractions.Platform;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Fenêtre au premier plan et détection du plein écran (rectangle de la fenêtre couvrant son moniteur).</summary>
public sealed class ForegroundWindowProvider : IForegroundWindowProvider
{
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

    public int? GetForegroundProcessId()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == 0) return null;
        User32.GetWindowThreadProcessId(hwnd, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    public bool IsForegroundFullscreen()
    {
        var hwnd = User32.GetForegroundWindow();
        if (hwnd == 0 || IsDesktopOrShell(hwnd)) return false;
        if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)) return false;
        if (!User32.GetWindowRect(hwnd, out var window)) return false;

        var monitor = User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTONULL);
        if (monitor == 0) return false;
        var info = new User32.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<User32.MONITORINFO>() };
        if (!User32.GetMonitorInfo(monitor, ref info)) return false;
        return Covers(window, info.rcMonitor);
    }

    internal static bool Covers(User32.RECT window, User32.RECT monitor)
        => window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom
           && monitor.Right > monitor.Left && monitor.Bottom > monitor.Top;

    private static bool IsDesktopOrShell(nint hwnd)
        => hwnd == User32.GetDesktopWindow() || hwnd == User32.GetShellWindow() || DesktopClasses.Contains(WindowEnumerator.GetClassName(hwnd));
}
