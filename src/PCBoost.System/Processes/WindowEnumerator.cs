using System.Runtime.InteropServices;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Fenêtre de premier niveau candidate au titre de « fenêtre principale » d'un processus.</summary>
internal readonly record struct TopLevelWindow(nint Handle, int ProcessId, string Title, bool IsVisible, bool HasOwner, bool IsCloaked);

/// <summary>Une seule passe EnumWindows pour tous les processus (MainWindowHandle ferait une passe par processus).</summary>
internal static unsafe class WindowEnumerator
{
    public static List<TopLevelWindow> EnumerateTopLevelWindows(bool visibleOnly)
    {
        var state = new EnumState(visibleOnly);
        var handle = GCHandle.Alloc(state);
        try
        {
            User32.EnumWindows(&Callback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        return state.Windows;
    }

    /// <summary>
    /// Fenêtre principale par processus, selon la règle de .NET (visible, sans propriétaire), en ignorant les fenêtres
    /// masquées par DWM (applications UWP suspendues) et en préférant une fenêtre titrée.
    /// </summary>
    public static Dictionary<int, TopLevelWindow> GetMainWindows()
    {
        var result = new Dictionary<int, TopLevelWindow>();
        foreach (var window in EnumerateTopLevelWindows(visibleOnly: true))
        {
            if (window.HasOwner || window.IsCloaked) continue;
            if (!result.TryGetValue(window.ProcessId, out var existing) || (existing.Title.Length == 0 && window.Title.Length > 0))
                result[window.ProcessId] = window;
        }
        return result;
    }

    public static string GetTitle(nint hwnd)
    {
        var length = User32.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        length = Math.Min(length, 1024);
        var buffer = stackalloc char[length + 1];
        var copied = User32.GetWindowText(hwnd, buffer, length + 1);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    public static string GetClassName(nint hwnd)
    {
        var buffer = stackalloc char[257];
        var copied = User32.GetClassName(hwnd, buffer, 257);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    public static bool IsCloaked(nint hwnd)
    {
        int cloaked = 0;
        return DwmApi.DwmGetWindowAttribute(hwnd, DwmApi.DWMWA_CLOAKED, &cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [UnmanagedCallersOnly]
    private static int Callback(nint hwnd, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is not EnumState state) return 0;
            var visible = User32.IsWindowVisible(hwnd);
            if (state.VisibleOnly && !visible) return 1;
            User32.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return 1;
            var hasOwner = User32.GetWindow(hwnd, User32.GW_OWNER) != 0;
            state.Windows.Add(new TopLevelWindow(hwnd, (int)pid, visible && !hasOwner ? GetTitle(hwnd) : string.Empty, visible, hasOwner, visible && IsCloaked(hwnd)));
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Une exception ne doit jamais traverser la frontière native : on arrête l'énumération proprement.
            return 0;
        }
    }

    private sealed class EnumState(bool visibleOnly)
    {
        public bool VisibleOnly { get; } = visibleOnly;
        public List<TopLevelWindow> Windows { get; } = new(256);
    }
}
