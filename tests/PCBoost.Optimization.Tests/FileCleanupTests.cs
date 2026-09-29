using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Files;
using PCBoost.Optimization.Files;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class FileCleanupTests
{
    private const long MiB = 1024 * 1024;
    private readonly InMemoryFileSystemProvider _files = new();
    private readonly FakeActivityJournal _journal = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero));

    private FileCleanupService Create() => new(_files, _journal, _clock);

    private void Add(string path, long size, string content, int daysOld = 10)
        => _files.AddFile(path, size, _clock.UtcNow.AddDays(-daysOld), content: content);

    [Fact]
    public async Task Scan_finds_large_files_and_confirmed_duplicates_only_in_personal_folders()
    {
        Add(@"C:\Users\Test\Videos\film.mkv", 3000 * MiB, "film");
        Add(@"C:\Users\Test\Downloads\setup.iso", 700 * MiB, "iso");
        Add(@"C:\Users\Test\Downloads\photo.jpg", 4 * MiB, "même contenu", daysOld: 30);
        Add(@"C:\Users\Test\Pictures\photo (1).jpg", 4 * MiB, "même contenu", daysOld: 5);
        Add(@"C:\Users\Test\Documents\autre.jpg", 4 * MiB, "contenu différent");
        Add(@"C:\Users\Test\Documents\petit.txt", 1000, "même");
        Add(@"C:\Users\Test\Desktop\petit.txt", 1000, "même");
        Add(@"C:\Program Files\Jeu\data.pak", 5000 * MiB, "jeu");

        var scan = await Create().ScanAsync();

        Assert.Equal([@"C:\Users\Test\Videos\film.mkv", @"C:\Users\Test\Downloads\setup.iso"], scan.LargeFiles.Select(f => f.Path));
        var group = Assert.Single(scan.DuplicateGroups);
        Assert.Equal(4 * MiB, group.Size);
        Assert.Equal(4 * MiB, group.RecoverableBytes);
        Assert.Equal(@"C:\Users\Test\Downloads\photo.jpg", group.Files[0].Path); // le plus ancien en premier
        Assert.False(scan.ComparisonLimited);
        Assert.DoesNotContain(scan.Roots, r => r.StartsWith(@"C:\Program Files", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Selected_files_go_to_recycle_bin_and_are_logged()
    {
        Add(@"C:\Users\Test\Videos\film.mkv", 3000 * MiB, "film");
        Add(@"C:\Users\Test\Downloads\a.zip", 10 * MiB, "x");
        Add(@"C:\Users\Test\Documents\a.zip", 10 * MiB, "x");
        var service = Create();
        var scan = await service.ScanAsync();

        var report = await service.MoveToRecycleBinAsync(scan, [@"C:\Users\Test\Videos\film.mkv", @"C:\Users\Test\Documents\a.zip"]);

        Assert.Equal(2, report.Moved);
        Assert.Equal(3010 * MiB, report.MovedBytes);
        Assert.Empty(report.Failures);
        Assert.Contains(@"C:\Users\Test\Videos\film.mkv", _files.RecycledFiles);
        Assert.True(_files.FileExists(@"C:\Users\Test\Downloads\a.zip"));
        Assert.Empty(_files.DeletedFiles);
        var entry = Assert.Single(_journal.Entries);
        Assert.Equal(ActivityKind.Cleanup, entry.Kind);
        Assert.Equal("Files_Journal_Recycled", entry.Message.Key);
    }

    [Fact]
    public async Task One_copy_of_each_duplicate_group_is_always_kept()
    {
        Add(@"C:\Users\Test\Downloads\a.zip", 10 * MiB, "x", daysOld: 20);
        Add(@"C:\Users\Test\Documents\a.zip", 10 * MiB, "x", daysOld: 2);
        var service = Create();
        var scan = await service.ScanAsync();

        var report = await service.MoveToRecycleBinAsync(scan, [@"C:\Users\Test\Downloads\a.zip", @"C:\Users\Test\Documents\a.zip"]);

        Assert.Equal(1, report.Moved);
        Assert.True(_files.FileExists(@"C:\Users\Test\Downloads\a.zip"));
        Assert.Equal("Files_KeptCopy", Assert.Single(report.Failures).Error.Message!.Key);
    }

    [Fact]
    public async Task Duplicate_is_not_removed_if_the_kept_copy_disappeared()
    {
        Add(@"C:\Users\Test\Downloads\a.zip", 10 * MiB, "x");
        Add(@"C:\Users\Test\Documents\a.zip", 10 * MiB, "x");
        var service = Create();
        var scan = await service.ScanAsync();
        _files.DeleteFile(@"C:\Users\Test\Downloads\a.zip");

        var report = await service.MoveToRecycleBinAsync(scan, [@"C:\Users\Test\Documents\a.zip"]);

        Assert.Equal(0, report.Moved);
        Assert.Equal("Files_NoCopyLeft", Assert.Single(report.Failures).Error.Message!.Key);
        Assert.True(_files.FileExists(@"C:\Users\Test\Documents\a.zip"));
    }

    [Fact]
    public async Task Changed_or_unknown_files_are_not_touched()
    {
        Add(@"C:\Users\Test\Videos\film.mkv", 3000 * MiB, "film");
        Add(@"C:\Users\Test\Videos\autre.mkv", 3000 * MiB, "autre");
        var service = Create();
        var scan = await service.ScanAsync();
        Add(@"C:\Users\Test\Videos\film.mkv", 3100 * MiB, "film modifié", daysOld: 0);

        var report = await service.MoveToRecycleBinAsync(scan, [@"C:\Users\Test\Videos\film.mkv", @"C:\Windows\System32\kernel32.dll", @"C:\Users\Test\Videos\..\..\x"]);

        Assert.Equal(0, report.Moved);
        Assert.Equal(["Files_Changed", "Files_NotInScan", "Files_NotInScan"], report.Failures.Select(f => f.Error.Message!.Key));
        Assert.Empty(_files.RecycledFiles);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public async Task Locked_file_failure_is_reported_and_others_continue()
    {
        Add(@"C:\Users\Test\Videos\a.mkv", 3000 * MiB, "a");
        Add(@"C:\Users\Test\Videos\b.mkv", 2000 * MiB, "b");
        var service = Create();
        var scan = await service.ScanAsync();
        _files.AddFile(@"C:\Users\Test\Videos\a.mkv", 3000 * MiB, _clock.UtcNow.AddDays(-10), locked: true, content: "a");

        var report = await service.MoveToRecycleBinAsync(scan, [@"C:\Users\Test\Videos\a.mkv", @"C:\Users\Test\Videos\b.mkv"]);

        Assert.Equal(1, report.Moved);
        Assert.Equal(OperationErrorKind.InUse, Assert.Single(report.Failures).Error.Error);
    }

    [Fact]
    public async Task Progress_is_reported_until_done()
    {
        Add(@"C:\Users\Test\Downloads\a.zip", 10 * MiB, "x");
        Add(@"C:\Users\Test\Documents\a.zip", 10 * MiB, "x");
        var stages = new List<FileScanStage>();
        await Create().ScanAsync(new SyncProgress<FileScanProgress>(p => stages.Add(p.Stage)));
        Assert.Equal(FileScanStage.Done, stages[^1]);
        Assert.Contains(FileScanStage.Comparing, stages);
    }

    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
