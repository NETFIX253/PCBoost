using System.Runtime.InteropServices;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Résolution des dossiers connus : SHGetKnownFolderPath pour Téléchargements et Démarrage, Environment ailleurs.</summary>
internal static unsafe class KnownFolderResolver
{
    public static string? Resolve(KnownFolder folder)
    {
        var path = folder switch
        {
            KnownFolder.UserProfile => Special(Environment.SpecialFolder.UserProfile),
            KnownFolder.LocalAppData => Special(Environment.SpecialFolder.LocalApplicationData),
            KnownFolder.RoamingAppData => Special(Environment.SpecialFolder.ApplicationData),
            KnownFolder.ProgramData => Special(Environment.SpecialFolder.CommonApplicationData),
            KnownFolder.UserTemp => Path.GetTempPath(),
            KnownFolder.WindowsDirectory => Special(Environment.SpecialFolder.Windows),
            KnownFolder.WindowsTemp => WindowsTemp(),
            KnownFolder.ProgramFiles => Special(Environment.SpecialFolder.ProgramFiles),
            KnownFolder.ProgramFilesX86 => Special(Environment.SpecialFolder.ProgramFilesX86),
            KnownFolder.Documents => Special(Environment.SpecialFolder.MyDocuments),
            KnownFolder.Downloads => Shell(Shell32.FOLDERID_Downloads),
            KnownFolder.Desktop => Special(Environment.SpecialFolder.DesktopDirectory),
            KnownFolder.Pictures => Special(Environment.SpecialFolder.MyPictures),
            KnownFolder.Videos => Special(Environment.SpecialFolder.MyVideos),
            KnownFolder.Music => Special(Environment.SpecialFolder.MyMusic),
            KnownFolder.StartupUser => Shell(Shell32.FOLDERID_Startup) ?? Special(Environment.SpecialFolder.Startup),
            KnownFolder.StartupCommon => Shell(Shell32.FOLDERID_CommonStartup) ?? Special(Environment.SpecialFolder.CommonStartup),
            KnownFolder.StartMenuPrograms => Special(Environment.SpecialFolder.Programs),
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        // Conserve la barre finale d'une racine de lecteur (« C:\ »).
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    private static string? Special(Environment.SpecialFolder folder)
    {
        var path = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static string? WindowsTemp()
    {
        var windows = Environment.GetEnvironmentVariable("WINDIR") ?? Special(Environment.SpecialFolder.Windows);
        return string.IsNullOrEmpty(windows) ? null : Path.Combine(windows, "Temp");
    }

    private static string? Shell(Guid folderId)
    {
        if (!OperatingSystem.IsWindows()) return null;
        char* path = null;
        try
        {
            var hr = Shell32.SHGetKnownFolderPath(folderId, Shell32.KF_FLAG_DONT_VERIFY, 0, out path);
            return hr >= 0 ? NativeStrings.FromNullTerminated(path) : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            // Le tampon doit être libéré par CoTaskMemFree même en cas d'échec.
            if (path != null) Marshal.FreeCoTaskMem((nint)path);
        }
    }
}
