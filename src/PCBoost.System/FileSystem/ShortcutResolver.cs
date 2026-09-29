using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;

namespace PCBoost.Platform;

/// <summary>Lecture d'un raccourci .lnk (IShellLinkW + IPersistFile). Le raccourci n'est jamais « résolu » (pas de recherche ni d'interface).</summary>
public sealed class ShortcutResolver : IShortcutResolver
{
    private static readonly Guid ClsidShellLink = new("00021401-0000-0000-C000-000000000046");

    private const int MaxPath = 32768;
    private const uint SLGP_RAWPATH = 0x4;
    private const int STGM_READ = 0x0;

    private readonly ILogger<ShortcutResolver> _logger;

    public ShortcutResolver(ILogger<ShortcutResolver>? logger = null)
    {
        _logger = logger ?? NullLogger<ShortcutResolver>.Instance;
    }

    public ShortcutTarget? Resolve(string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath)) return null;
        if (!shortcutPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return null;

        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(ClsidShellLink, throwOnError: false);
            if (type is null) return null;
            instance = Activator.CreateInstance(type);
            if (instance is not IShellLinkW link || instance is not IPersistFile persist) return null;

            persist.Load(shortcutPath, STGM_READ);

            var target = new StringBuilder(MaxPath);
            link.GetPath(target, target.Capacity, 0, SLGP_RAWPATH);
            var arguments = new StringBuilder(MaxPath);
            link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(MaxPath);
            link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);

            var targetPath = Expand(target.ToString());
            if (string.IsNullOrWhiteSpace(targetPath)) return null;
            return new ShortcutTarget(targetPath, NullIfEmpty(arguments.ToString()), NullIfEmpty(Expand(workingDirectory.ToString())));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or InvalidCastException or ArgumentException)
        {
            _logger.LogDebug("Raccourci illisible ({Error})", ex.GetType().Name);
            return null;
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
        }
    }

    private static string Expand(string value)
        => string.IsNullOrEmpty(value) ? value : Environment.ExpandEnvironmentVariables(value).Trim();

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>IShellLinkW, méthodes dans l'ordre exact de la table virtuelle (shobjidl_core.h).</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);

        void GetIDList(out nint ppidl);

        void SetIDList(nint pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(nint hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
