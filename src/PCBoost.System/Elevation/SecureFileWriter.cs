using System.Text;
using Microsoft.Win32.SafeHandles;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Elevation;

/// <summary>
/// Écriture de fichiers par le processus élevé dans des dossiers modifiables par l'utilisateur (résultat, journal).
/// Parades aux redirections (jonctions, liens symboliques, liens physiques) : aucun point d'analyse sur les derniers
/// niveaux du chemin avant l'ouverture, création exclusive (jamais d'écrasement), vérification du chemin final résolu
/// et du nombre de liens après l'ouverture ; en cas d'écart, rien n'est écrit et le fichier créé est retiré.
/// </summary>
internal static unsafe class SecureFileWriter
{
    /// <summary>Crée un nouveau fichier (échoue s'il existe) et y écrit <paramref name="content"/>.</summary>
    public static bool TryWriteNewFile(string path, ReadOnlySpan<byte> content, Func<string, bool> isAcceptableResolvedPath, int checkedParentLevels, out string? error)
    {
        error = null;
        if (HasReparsePoint(path, checkedParentLevels, out error)) return false;

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.GetType().Name;
            return false;
        }

        string? resolved;
        using (stream)
        {
            resolved = GetResolvedPath(stream.SafeFileHandle);
            if (resolved is not null && isAcceptableResolvedPath(resolved) && GetLinkCount(stream.SafeFileHandle) == 1)
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
                return true;
            }
        }

        // Redirection détectée : le fichier que nous venons de créer (vide) est retiré à son emplacement réel.
        error = "redirection";
        if (resolved is not null)
        {
            try
            {
                File.Delete(resolved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = "redirection (suppression impossible)";
            }
        }
        return false;
    }

    /// <summary>Ajoute une ligne à un journal existant ou le crée ; n'écrit jamais dans un fichier redirigé ou multi-lié.</summary>
    public static bool TryAppendLine(string path, string line, Func<string, bool> isAcceptableResolvedPath, int checkedParentLevels)
    {
        if (HasReparsePoint(path, checkedParentLevels, out _)) return false;
        var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);

        FileStream stream;
        try
        {
            stream = File.Exists(path)
                ? new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read)
                : new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        using (stream)
        {
            var resolved = GetResolvedPath(stream.SafeFileHandle);
            if (resolved is null || !isAcceptableResolvedPath(resolved) || GetLinkCount(stream.SafeFileHandle) != 1) return false;
            if (stream.Length > 1024 * 1024) stream.SetLength(0); // Journal borné.
            stream.Seek(0, SeekOrigin.End);
            stream.Write(bytes);
            return true;
        }
    }

    /// <summary>Le fichier ou l'un de ses <paramref name="levels"/> dossiers parents est-il un point d'analyse ?</summary>
    internal static bool HasReparsePoint(string path, int levels, out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                error = "reparse point (fichier)";
                return true;
            }
            var directory = Path.GetDirectoryName(path);
            for (var i = 0; i < levels && !string.IsNullOrEmpty(directory); i++)
            {
                var info = new DirectoryInfo(directory);
                if (!info.Exists)
                {
                    error = "dossier absent";
                    return true;
                }
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "reparse point (dossier)";
                    return true;
                }
                directory = Path.GetDirectoryName(directory);
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            error = ex.GetType().Name;
            return true;
        }
    }

    /// <summary>Chemin final résolu par Windows (GetFinalPathNameByHandleW), sans préfixe \\?\ ; null pour un chemin UNC.</summary>
    private static string? GetResolvedPath(SafeFileHandle handle)
    {
        const int Capacity = 1024;
        var buffer = stackalloc char[Capacity];
        var length = Kernel32.GetFinalPathNameByHandle(handle, buffer, Capacity, Kernel32.FILE_NAME_NORMALIZED | Kernel32.VOLUME_NAME_DOS);
        if (length == 0 || length >= Capacity) return null;
        var path = new string(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return null;
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static uint GetLinkCount(SafeFileHandle handle)
        => Kernel32.GetFileInformationByHandle(handle, out var info) ? info.nNumberOfLinks : 0;
}
