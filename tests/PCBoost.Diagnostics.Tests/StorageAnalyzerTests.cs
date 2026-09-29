using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Analysis;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Diagnostics.Storage;
using PCBoost.Diagnostics.Tests.TestSupport;
using PCBoost.TestUtilities;

namespace PCBoost.Diagnostics.Tests;

public sealed class StorageAnalyzerTests
{
    private readonly FakeClock _clock = new(Reports.Now);
    private readonly FakeSystemInfoProvider _systemInfo = new();
    private readonly InMemoryFileSystemProvider _fs = new();

    public StorageAnalyzerTests()
    {
        // Disque C: de 100 Gio, 40 Gio libres → 60 Gio utilisés.
        _systemInfo.Drives = [Reports.Drive(100L * ByteSize.GiB, 40L * ByteSize.GiB, StorageMediaType.Ssd)];
        _fs.AddFile(@"C:\Users\Test\Documents\rapport.docx", 100 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\Downloads\setup.exe", 200 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\Downloads\video.mp4", 300 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\Desktop\note.txt", 1 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\Pictures\photo.jpg", 50 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\AppData\Local\Temp\tmp1.tmp", 70 * ByteSize.MiB);
        _fs.AddFile(@"C:\Users\Test\AppData\Local\Cache\big.bin", 2L * ByteSize.GiB);
        _fs.AddFile(@"C:\Program Files\App\app.exe", 1L * ByteSize.GiB);
        _fs.AddFile(@"C:\Program Files (x86)\Old\old.exe", 500 * ByteSize.MiB);
        _fs.AddFile(@"C:\Windows\System32\kernel32.dll", 3L * ByteSize.GiB);
    }

    private StorageAnalyzer Analyzer(IFileSystemProvider? fs = null, StorageAnalyzerOptions? options = null)
        => new(_systemInfo, fs ?? _fs, _clock, NullLogger<StorageAnalyzer>.Instance, options);

    private static StorageCategoryUsage Category(StorageBreakdown b, StorageCategoryKind kind) => b.Categories.Single(c => c.Kind == kind);

    [Fact]
    public async Task Categories_are_measured_on_the_system_drive()
    {
        var breakdown = await Analyzer().AnalyzeSystemDriveAsync();
        Assert.NotNull(breakdown);

        Assert.Equal(100 * ByteSize.MiB, Category(breakdown, StorageCategoryKind.Documents).Bytes);
        Assert.Equal(500 * ByteSize.MiB, Category(breakdown, StorageCategoryKind.Downloads).Bytes);
        Assert.Equal(1 * ByteSize.MiB, Category(breakdown, StorageCategoryKind.Desktop).Bytes);
        Assert.Equal(50 * ByteSize.MiB, Category(breakdown, StorageCategoryKind.Pictures).Bytes);
        Assert.Equal(0, Category(breakdown, StorageCategoryKind.Videos).Bytes);
        Assert.True(Category(breakdown, StorageCategoryKind.Videos).Measured);
        Assert.Equal(70 * ByteSize.MiB, Category(breakdown, StorageCategoryKind.Temporary).Bytes);

        var apps = Category(breakdown, StorageCategoryKind.Applications);
        Assert.True(apps.Measured);
        Assert.Equal(1L * ByteSize.GiB + 500 * ByteSize.MiB, apps.Bytes);

        var system = Category(breakdown, StorageCategoryKind.System);
        Assert.False(system.Measured);
        Assert.Equal(0, system.Bytes);

        var measured = breakdown.Categories.Where(c => c.Measured && c.Kind != StorageCategoryKind.Other).Sum(c => c.Bytes);
        var other = Category(breakdown, StorageCategoryKind.Other);
        Assert.Equal(60L * ByteSize.GiB - measured, other.Bytes);
        Assert.Equal(_clock.UtcNow, breakdown.Timestamp);
        Assert.Equal(@"C:\", breakdown.Drive.RootPath);
    }

    [Fact]
    public async Task Largest_profile_folders_are_sorted_and_limited()
    {
        for (var i = 0; i < 10; i++) _fs.AddFile($@"C:\Users\Test\Folder{i}\data.bin", (i + 1) * ByteSize.MiB);
        var breakdown = await Analyzer().AnalyzeSystemDriveAsync();

        Assert.Equal(8, breakdown!.LargestFolders.Count);
        Assert.Equal(@"C:\Users\Test\AppData", breakdown.LargestFolders[0].Path);
        Assert.Equal(2L * ByteSize.GiB + 70 * ByteSize.MiB, breakdown.LargestFolders[0].Bytes);
        Assert.Equal(@"C:\Users\Test\Downloads", breakdown.LargestFolders[1].Path);
        for (var i = 1; i < breakdown.LargestFolders.Count; i++)
            Assert.True(breakdown.LargestFolders[i - 1].Bytes >= breakdown.LargestFolders[i].Bytes);
    }

    [Fact]
    public async Task Folders_on_another_drive_are_not_counted()
    {
        _fs.SetKnownFolder(KnownFolder.Videos, @"D:\Videos");
        _fs.AddFile(@"D:\Videos\film.mkv", 20L * ByteSize.GiB);
        var breakdown = await Analyzer().AnalyzeSystemDriveAsync();
        Assert.DoesNotContain(breakdown!.Categories, c => c.Kind == StorageCategoryKind.Videos);
        Assert.True(Category(breakdown, StorageCategoryKind.Other).Bytes > 0);
    }

    [Fact]
    public async Task Applications_over_time_budget_are_not_measured()
    {
        var slow = new ScriptedFileSystem(_fs) { BlockUnder = @"C:\Program Files" };
        var breakdown = await Analyzer(slow, new StorageAnalyzerOptions { ApplicationsTimeBudget = TimeSpan.FromMilliseconds(50) }).AnalyzeSystemDriveAsync();

        var apps = Category(breakdown!, StorageCategoryKind.Applications);
        Assert.False(apps.Measured);
        Assert.Equal(0, apps.Bytes);
        Assert.True(Category(breakdown!, StorageCategoryKind.Documents).Measured);
    }

    [Fact]
    public async Task Access_denied_marks_category_as_not_measured()
    {
        var denied = new ScriptedFileSystem(_fs) { DenyUnder = @"C:\Program Files (x86)" };
        var breakdown = await Analyzer(denied).AnalyzeSystemDriveAsync();
        Assert.False(Category(breakdown!, StorageCategoryKind.Applications).Measured);
    }

    [Fact]
    public async Task Analysis_never_deletes_anything()
    {
        var before = _fs.AllFiles.Count;
        await Analyzer().AnalyzeSystemDriveAsync();
        Assert.Empty(_fs.DeletedFiles);
        Assert.Equal(before, _fs.AllFiles.Count);
    }

    [Fact]
    public async Task No_system_drive_returns_null()
    {
        _systemInfo.Drives = [];
        Assert.Null(await Analyzer().AnalyzeSystemDriveAsync());
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Analyzer().AnalyzeSystemDriveAsync(cts.Token));
    }

    [Theory]
    [InlineData(@"C:\Users\Test", @"C:\", true)]
    [InlineData(@"c:\users", @"C:\", true)]
    [InlineData(@"D:\Data", @"C:\", false)]
    [InlineData(@"C:", @"C:\", true)]
    public void Drive_membership(string path, string root, bool expected)
        => Assert.Equal(expected, StorageAnalyzer.IsOnDrive(path, root));

    /// <summary>Système de fichiers qui bloque (jusqu'à l'annulation) ou refuse l'accès sous un préfixe.</summary>
    private sealed class ScriptedFileSystem(InMemoryFileSystemProvider inner) : IFileSystemProvider
    {
        public string? BlockUnder { get; init; }
        public string? DenyUnder { get; init; }

        public DirectorySizeResult GetDirectorySize(string directory, CancellationToken cancellationToken = default)
        {
            if (DenyUnder is not null && directory.StartsWith(DenyUnder, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException(directory);
            if (BlockUnder is not null && directory.StartsWith(BlockUnder, StringComparison.OrdinalIgnoreCase))
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                cancellationToken.ThrowIfCancellationRequested();
            }
            return inner.GetDirectorySize(directory, cancellationToken);
        }

        public string? GetKnownFolder(KnownFolder folder) => inner.GetKnownFolder(folder);
        public string? GetEnvironmentVariable(string name) => inner.GetEnvironmentVariable(name);
        public bool FileExists(string path) => inner.FileExists(path);
        public bool DirectoryExists(string path) => inner.DirectoryExists(path);
        public IEnumerable<FileEntry> EnumerateFiles(string directory, bool recursive, CancellationToken cancellationToken = default) => inner.EnumerateFiles(directory, recursive, cancellationToken);
        public IEnumerable<string> EnumerateDirectories(string directory) => inner.EnumerateDirectories(directory);
        public OperationResult DeleteFile(string path) => throw new InvalidOperationException("Aucune suppression attendue.");
        public int DeleteEmptySubdirectories(string root) => throw new InvalidOperationException("Aucune suppression attendue.");
        public string? ReadAllText(string path) => inner.ReadAllText(path);
        public OperationResult WriteAllText(string path, string content) => throw new InvalidOperationException("Aucune écriture attendue.");
        public OperationResult CreateDirectory(string path) => throw new InvalidOperationException("Aucune écriture attendue.");
        public IReadOnlyList<string> GetFixedDriveRoots() => inner.GetFixedDriveRoots();
        public string GetFullPath(string path) => inner.GetFullPath(path);
    }
}
