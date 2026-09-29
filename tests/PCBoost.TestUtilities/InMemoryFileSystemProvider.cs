using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.TestUtilities;

/// <summary>Système de fichiers en mémoire (chemins Windows, insensibles à la casse).</summary>
public sealed class InMemoryFileSystemProvider : IFileSystemProvider
{
    private readonly Dictionary<string, FakeFile> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<KnownFolder, string> _knownFolders = new();
    private readonly Dictionary<string, string> _environment = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryFileSystemProvider()
    {
        SetKnownFolder(KnownFolder.UserProfile, @"C:\Users\Test");
        SetKnownFolder(KnownFolder.LocalAppData, @"C:\Users\Test\AppData\Local");
        SetKnownFolder(KnownFolder.RoamingAppData, @"C:\Users\Test\AppData\Roaming");
        SetKnownFolder(KnownFolder.UserTemp, @"C:\Users\Test\AppData\Local\Temp");
        SetKnownFolder(KnownFolder.ProgramData, @"C:\ProgramData");
        SetKnownFolder(KnownFolder.WindowsDirectory, @"C:\Windows");
        SetKnownFolder(KnownFolder.WindowsTemp, @"C:\Windows\Temp");
        SetKnownFolder(KnownFolder.ProgramFiles, @"C:\Program Files");
        SetKnownFolder(KnownFolder.ProgramFilesX86, @"C:\Program Files (x86)");
        SetKnownFolder(KnownFolder.Documents, @"C:\Users\Test\Documents");
        SetKnownFolder(KnownFolder.Downloads, @"C:\Users\Test\Downloads");
        SetKnownFolder(KnownFolder.Desktop, @"C:\Users\Test\Desktop");
        SetKnownFolder(KnownFolder.Pictures, @"C:\Users\Test\Pictures");
        SetKnownFolder(KnownFolder.Videos, @"C:\Users\Test\Videos");
        SetKnownFolder(KnownFolder.Music, @"C:\Users\Test\Music");
        SetKnownFolder(KnownFolder.StartupUser, @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup");
        SetKnownFolder(KnownFolder.StartupCommon, @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\StartUp");
        SetKnownFolder(KnownFolder.StartMenuPrograms, @"C:\Users\Test\AppData\Roaming\Microsoft\Windows\Start Menu\Programs");
    }

    public sealed class FakeFile
    {
        public required string Path { get; init; }
        public long Size { get; set; }
        public DateTimeOffset LastWriteUtc { get; set; }
        public bool Locked { get; set; }
        public bool ReadOnly { get; set; }
        public string? Content { get; set; }
    }

    public IReadOnlyCollection<string> AllFiles => _files.Keys.ToList();

    public HashSet<string> DeletedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetKnownFolder(KnownFolder folder, string path)
    {
        _knownFolders[folder] = path;
        AddDirectory(path);
    }

    public void SetEnvironmentVariable(string name, string value) => _environment[name] = value;

    public void AddDirectory(string path)
    {
        var p = Norm(path);
        while (!string.IsNullOrEmpty(p))
        {
            _directories.Add(p);
            var parent = System.IO.Path.GetDirectoryName(p.Replace('\\', '/'))?.Replace('/', '\\');
            if (parent is null || parent.Length < 3 || parent == p) break;
            p = parent;
        }
    }

    public FakeFile AddFile(string path, long size, DateTimeOffset? lastWrite = null, bool locked = false, string? content = null)
    {
        var p = Norm(path);
        var f = new FakeFile { Path = p, Size = size, LastWriteUtc = lastWrite ?? DateTimeOffset.UtcNow.AddDays(-30), Locked = locked, Content = content };
        _files[p] = f;
        AddDirectory(Parent(p));
        return f;
    }

    private static string Norm(string path) => path.Replace('/', '\\').TrimEnd('\\');

    private static string Parent(string path)
    {
        var i = path.LastIndexOf('\\');
        return i <= 2 ? path[..Math.Max(i, 0)] : path[..i];
    }

    public string? GetKnownFolder(KnownFolder folder) => _knownFolders.TryGetValue(folder, out var p) ? p : null;

    public string? GetEnvironmentVariable(string name) => _environment.TryGetValue(name, out var v) ? v : null;

    public bool FileExists(string path) => _files.ContainsKey(Norm(path));

    public bool DirectoryExists(string path) => _directories.Contains(Norm(path));

    public IEnumerable<FileEntry> EnumerateFiles(string directory, bool recursive, CancellationToken cancellationToken = default)
    {
        var d = Norm(directory) + "\\";
        foreach (var f in _files.Values.ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!f.Path.StartsWith(d, StringComparison.OrdinalIgnoreCase)) continue;
            if (!recursive && f.Path[d.Length..].Contains('\\')) continue;
            yield return new FileEntry(f.Path, f.Size, f.LastWriteUtc, f.ReadOnly, false, false);
        }
    }

    public IEnumerable<string> EnumerateDirectories(string directory)
    {
        var d = Norm(directory) + "\\";
        return _directories.Where(x => x.StartsWith(d, StringComparison.OrdinalIgnoreCase) && !x[d.Length..].Contains('\\')).ToList();
    }

    public DirectorySizeResult GetDirectorySize(string directory, CancellationToken cancellationToken = default)
    {
        var files = EnumerateFiles(directory, true, cancellationToken).ToList();
        return new DirectorySizeResult(files.Sum(f => f.Size), files.Count, 0);
    }

    public OperationResult DeleteFile(string path)
    {
        var p = Norm(path);
        if (!_files.TryGetValue(p, out var f)) return OperationResult.Fail(OperationErrorKind.NotFound);
        if (f.Locked) return OperationResult.Fail(OperationErrorKind.InUse);
        if (f.ReadOnly) return OperationResult.Fail(OperationErrorKind.AccessDenied);
        _files.Remove(p);
        DeletedFiles.Add(p);
        return OperationResult.Ok();
    }

    public int DeleteEmptySubdirectories(string root)
    {
        var r = Norm(root) + "\\";
        var empty = _directories.Where(d => d.StartsWith(r, StringComparison.OrdinalIgnoreCase)
            && !_files.Keys.Any(f => f.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
            && !_directories.Any(o => o.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))).ToList();
        foreach (var d in empty) _directories.Remove(d);
        return empty.Count;
    }

    public string? ReadAllText(string path) => _files.TryGetValue(Norm(path), out var f) ? f.Content : null;

    public OperationResult WriteAllText(string path, string content)
    {
        AddFile(path, content.Length, DateTimeOffset.UtcNow, content: content);
        return OperationResult.Ok();
    }

    public OperationResult CreateDirectory(string path)
    {
        AddDirectory(path);
        return OperationResult.Ok();
    }

    public IReadOnlyList<string> GetFixedDriveRoots() => [@"C:\"];

    public string GetFullPath(string path) => Norm(path);
}
