using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Security;

namespace PCBoost.Platform;

/// <summary>
/// Système de fichiers Windows. Ne suit jamais les points d'analyse, ne retire jamais l'attribut lecture seule et refuse
/// toute suppression ou écriture visant un chemin interdit par <see cref="ForbiddenTargetPolicy"/>.
/// </summary>
public sealed class FileSystemProvider : IFileSystemProvider
{
    private const long MaxReadBytes = 16 * ByteSize.MiB;
    private const int MaxDirectoryDepth = 128;

    private readonly ILogger<FileSystemProvider> _logger;

    public FileSystemProvider(ILogger<FileSystemProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<FileSystemProvider>.Instance;
    }

    public string? GetKnownFolder(KnownFolder folder) => KnownFolderResolver.Resolve(folder);

    public string? GetEnvironmentVariable(string name)
        => string.IsNullOrWhiteSpace(name) ? null : Environment.GetEnvironmentVariable(name);

    public bool FileExists(string path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    public bool DirectoryExists(string path) => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);

    public IEnumerable<FileEntry> EnumerateFiles(string directory, bool recursive, CancellationToken cancellationToken = default)
        => EnumerateFilesCore(directory, recursive, null, cancellationToken);

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        if (!IsTraversableDirectory(directory)) return [];
        try
        {
            var options = SafeFileEnumerator.CreateOptions(recursive: false);
            options.IgnoreInaccessible = true;
            return Directory.EnumerateDirectories(directory, "*", options).ToList();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return [];
        }
    }

    public DirectorySizeResult GetDirectorySize(string directory, CancellationToken cancellationToken = default)
    {
        long bytes = 0;
        var files = 0;
        var errors = new ErrorCounter();
        foreach (var entry in EnumerateFilesCore(directory, recursive: true, errors, cancellationToken))
        {
            bytes += entry.Size;
            files++;
        }
        return new DirectorySizeResult(bytes, files, errors.Count);
    }

    public OperationResult DeleteFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        var full = GetFullPath(path);
        if (ForbiddenTargetPolicy.IsForbiddenFilePath(full))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"));

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists) return OperationResult.Fail(OperationErrorKind.NotFound);
            var attributes = info.Attributes;
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"), "reparse point");
            if ((attributes & FileAttributes.ReadOnly) != 0)
                return OperationResult.Fail(OperationErrorKind.AccessDenied, TextRef.Of("Sys_FileReadOnly"));

            File.Delete(full);
            return OperationResult.Ok();
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            return OperationResult.Fail(OperationErrorKind.InUse, TextRef.Of("Sys_FileInUse"), ex.Message);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return OperationResult.FromException(ex);
        }
    }

    public int DeleteEmptySubdirectories(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return 0;
        var fullRoot = GetFullPath(root).TrimEnd('\\');
        if (!IsTraversableDirectory(fullRoot)) return 0;
        return DeleteEmptyChildren(fullRoot, depth: 0);
    }

    public string? ReadAllText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxReadBytes) return null;
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return null;
        }
    }

    /// <summary>Écriture atomique : fichier temporaire dans le même dossier puis remplacement.</summary>
    public OperationResult WriteAllText(string path, string content)
    {
        if (string.IsNullOrWhiteSpace(path)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        var full = GetFullPath(path);
        if (ForbiddenTargetPolicy.IsForbiddenFilePath(full))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"));

        var temp = full + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"), "reparse point");
            var directory = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(temp, content ?? string.Empty);
            File.Move(temp, full, overwrite: true);
            return OperationResult.Ok();
        }
        catch (IOException ex) when (IsInUse(ex))
        {
            TryDelete(temp);
            return OperationResult.Fail(OperationErrorKind.InUse, TextRef.Of("Sys_FileInUse"), ex.Message);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            TryDelete(temp);
            return OperationResult.FromException(ex);
        }
    }

    public OperationResult CreateDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        var full = GetFullPath(path);
        if (ForbiddenTargetPolicy.IsForbiddenFilePath(full.TrimEnd('\\') + "\\"))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ProtectedTarget"));
        try
        {
            Directory.CreateDirectory(full);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return OperationResult.FromException(ex);
        }
    }

    public IReadOnlyList<string> GetFixedDriveRoots()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && SafeIsReady(d))
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return [];
        }
    }

    public string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return path;
        }
    }

    private IEnumerable<FileEntry> EnumerateFilesCore(string directory, bool recursive, ErrorCounter? errors, CancellationToken cancellationToken)
    {
        if (!IsTraversableDirectory(directory)) yield break;

        SafeFileEnumerator enumerator;
        try
        {
            enumerator = new SafeFileEnumerator(directory, recursive);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            if (errors is not null) errors.Count++;
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool moved;
                try
                {
                    moved = enumerator.MoveNext();
                }
                catch (Exception ex) when (IsExpected(ex))
                {
                    _logger.LogDebug("Parcours interrompu ({Error})", ex.GetType().Name);
                    break;
                }
                if (!moved) break;
                yield return enumerator.Current;
            }
            if (errors is not null) errors.Count += enumerator.ErrorCount;
        }
    }

    private int DeleteEmptyChildren(string directory, int depth)
    {
        if (depth > MaxDirectoryDepth) return 0;
        var deleted = 0;
        foreach (var child in EnumerateDirectories(directory))
        {
            if (!IsTraversableDirectory(child)) continue;
            deleted += DeleteEmptyChildren(child, depth + 1);
            if (ForbiddenTargetPolicy.IsForbiddenFilePath(child.TrimEnd('\\') + "\\")) continue;
            try
            {
                var attributes = File.GetAttributes(child);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.System)) != 0) continue;
                if (Directory.EnumerateFileSystemEntries(child).Any()) continue;
                Directory.Delete(child, recursive: false);
                deleted++;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // Dossier verrouillé, recréé entre-temps ou inaccessible : laissé en place.
            }
        }
        return deleted;
    }

    /// <summary>Dossier existant qui n'est pas lui-même un point d'analyse (jonction, lien).</summary>
    private static bool IsTraversableDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
    }

    private static bool SafeIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // Fichier temporaire résiduel sans conséquence.
        }
    }

    /// <summary>Partage (32), verrou (33) ou section mappée (1224) : le fichier est utilisé par un autre programme.</summary>
    internal static bool IsInUse(IOException exception)
        => (exception.HResult & 0xFFFF) is 32 or 33 or 1224;

    private static bool IsExpected(Exception ex)
        => ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException;

    private sealed class ErrorCounter
    {
        public int Count { get; set; }
    }
}
