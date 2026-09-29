using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.Domain;

public sealed class CleanupExecutorTests
{
    private const string Temp = @"C:\Users\Test\AppData\Local\Temp";
    private const string Local = @"C:\Users\Test\AppData\Local";

    private readonly InMemoryFileSystemProvider _fs = new();
    private readonly FakeClock _clock = new();
    private readonly CleanupExecutor _executor;

    public CleanupExecutorTests() => _executor = new CleanupExecutor(_fs, _clock);

    private DateTimeOffset DaysAgo(double days) => _clock.UtcNow - TimeSpan.FromDays(days);

    private static CleanupCategoryDefinition Category(string id) => CleanupCatalog.Find(id)!;

    [Fact]
    public void Minimum_age_protects_recent_files()
    {
        _fs.AddFile(Temp + @"\old.tmp", 100, DaysAgo(3));
        _fs.AddFile(Temp + @"\recent.tmp", 50, _clock.UtcNow - TimeSpan.FromHours(2));

        var scan = _executor.Scan(Category(CleanupCatalog.UserTemp));
        var result = _executor.Delete(Category(CleanupCatalog.UserTemp));

        Assert.Equal(new CleanupScanTotals(100, 1, 1), scan);
        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(100, result.BytesFreed);
        Assert.Contains(Temp + @"\old.tmp", _fs.DeletedFiles);
        Assert.True(_fs.FileExists(Temp + @"\recent.tmp"));
    }

    [Fact]
    public void Crash_dump_pattern_only_matches_dmp_files_at_top_level()
    {
        var dumps = Local + @"\CrashDumps";
        _fs.AddFile(dumps + @"\app.exe.1234.dmp", 1000, DaysAgo(30));
        _fs.AddFile(dumps + @"\notes.txt", 10, DaysAgo(30));
        _fs.AddFile(dumps + @"\sub\nested.dmp", 10, DaysAgo(30));

        var result = _executor.Delete(Category(CleanupCatalog.UserCrashDumps));

        Assert.Equal(1, result.FilesDeleted);
        Assert.False(_fs.FileExists(dumps + @"\app.exe.1234.dmp"));
        Assert.True(_fs.FileExists(dumps + @"\notes.txt"));
        Assert.True(_fs.FileExists(dumps + @"\sub\nested.dmp"));
    }

    [Fact]
    public void Thumbnail_cache_pattern_matches_thumbcache_files_only()
    {
        var explorer = Local + @"\Microsoft\Windows\Explorer";
        _fs.AddFile(explorer + @"\thumbcache_256.db", 500, DaysAgo(1));
        _fs.AddFile(explorer + @"\THUMBCACHE_idx.db", 5, DaysAgo(1));
        _fs.AddFile(explorer + @"\iconcache_16.db", 500, DaysAgo(1));

        var result = _executor.Delete(Category(CleanupCatalog.ThumbnailCache));

        Assert.Equal(2, result.FilesDeleted);
        Assert.True(_fs.FileExists(explorer + @"\iconcache_16.db"));
    }

    [Theory]
    [InlineData("thumbcache_256.db", "thumbcache_*.db", true)]
    [InlineData("thumbcache_256.dbx", "thumbcache_*.db", false)]
    [InlineData("xthumbcache_256.db", "thumbcache_*.db", false)]
    [InlineData("crash.DMP", "*.dmp", true)]
    [InlineData("MEMORY.DMP", "MEMORY.DMP", true)]
    [InlineData("memory.dmp.bak", "MEMORY.DMP", false)]
    [InlineData("anything", "*", true)]
    public void Pattern_matching_is_case_insensitive_and_anchored(string name, string pattern, bool expected)
        => Assert.Equal(expected, CleanupExecutor.MatchesPattern(name, pattern));

    [Fact]
    public void Browser_profile_wildcard_is_expanded_for_every_profile()
    {
        var userData = Local + @"\Google\Chrome\User Data";
        _fs.AddFile(userData + @"\Default\Cache\Cache_Data\f_000001", 100);
        _fs.AddFile(userData + @"\Profile 1\Cache\Cache_Data\f_000002", 200);
        _fs.AddFile(userData + @"\Default\Code Cache\js\index", 300);
        _fs.AddFile(userData + @"\Default\GPUCache\data_0", 400);
        _fs.AddFile(userData + @"\Default\Bookmarks", 50);
        _fs.AddFile(userData + @"\Default\History", 60);

        var roots = _executor.ResolveRoots(Category(CleanupCatalog.ChromeCache)).Select(r => r.Directory).ToList();
        var result = _executor.Delete(Category(CleanupCatalog.ChromeCache));

        Assert.Contains(userData + @"\Default\Cache\Cache_Data", roots);
        Assert.Contains(userData + @"\Profile 1\Cache\Cache_Data", roots);
        Assert.Equal(4, result.FilesDeleted);
        Assert.Equal(1000, result.BytesFreed);
        Assert.True(_fs.FileExists(userData + @"\Default\Bookmarks"));
        Assert.True(_fs.FileExists(userData + @"\Default\History"));
    }

    [Fact]
    public void Missing_browser_folder_resolves_to_no_roots()
        => Assert.Empty(_executor.ResolveRoots(Category(CleanupCatalog.FirefoxCache)));

    [Theory]
    [InlineData(@"..\Documents")]
    [InlineData(@"Temp\..\..\Documents")]
    [InlineData(@"..")]
    public void Relative_paths_escaping_the_known_folder_are_rejected(string relativePath)
    {
        _fs.AddFile(@"C:\Users\Test\Documents\thesis.docx", 1000, DaysAgo(365));
        var hostile = new CleanupCategoryDefinition("hostile", SafetyCategory.Safe, false, true, TimeSpan.Zero,
            [new CleanupTargetSpec(KnownFolder.LocalAppData, relativePath)], []);

        Assert.Empty(_executor.ResolveRoots(hostile));
        var result = _executor.Delete(hostile);

        Assert.Equal(0, result.FilesDeleted);
        Assert.True(_fs.FileExists(@"C:\Users\Test\Documents\thesis.docx"));
    }

    [Theory]
    [InlineData(@"C:\Temp\file.tmp", @"C:\Temp", true)]
    [InlineData(@"C:\Temp\sub\file.tmp", @"C:\Temp\", true)]
    [InlineData(@"c:\temp\FILE.tmp", @"C:\Temp", true)]
    [InlineData(@"C:\Temp2\file.tmp", @"C:\Temp", false)]
    [InlineData(@"C:\Tem", @"C:\Temp", false)]
    [InlineData(@"C:\Temp", @"C:\Temp", true)]
    public void Containment_check_requires_a_path_separator_boundary(string path, string root, bool expected)
        => Assert.Equal(expected, CleanupExecutor.IsContained(path, root));

    [Fact]
    public void Locked_and_read_only_files_are_skipped_not_deleted()
    {
        _fs.AddFile(Temp + @"\ok.tmp", 10, DaysAgo(5));
        _fs.AddFile(Temp + @"\locked.tmp", 20, DaysAgo(5), locked: true);
        _fs.AddFile(Temp + @"\readonly.tmp", 30, DaysAgo(5)).ReadOnly = true;

        var result = _executor.Delete(Category(CleanupCatalog.UserTemp));

        Assert.Equal(1, result.FilesDeleted);
        Assert.Equal(2, result.FilesSkipped);
        Assert.Equal(0, result.FilesFailed);
        Assert.Equal(10, result.BytesFreed);
        Assert.True(_fs.FileExists(Temp + @"\locked.tmp"));
        Assert.True(_fs.FileExists(Temp + @"\readonly.tmp"));
    }

    [Fact]
    public void Forbidden_system_paths_are_never_deleted_even_if_a_known_folder_points_there()
    {
        var hijacked = @"C:\Windows\System32\config\systemprofile\AppData\Local\Temp";
        _fs.SetKnownFolder(KnownFolder.UserTemp, hijacked);
        _fs.AddFile(hijacked + @"\evil.tmp", 10, DaysAgo(10));

        var scan = _executor.Scan(Category(CleanupCatalog.UserTemp));
        var result = _executor.Delete(Category(CleanupCatalog.UserTemp));

        Assert.Equal(0, scan.RootsFound);
        Assert.Equal(0, result.FilesDeleted);
        Assert.True(_fs.FileExists(hijacked + @"\evil.tmp"));
    }

    [Fact]
    public void Empty_subdirectories_are_removed_but_never_the_root()
    {
        _fs.AddFile(Temp + @"\setup\deep\a.tmp", 10, DaysAgo(5));

        _executor.Delete(Category(CleanupCatalog.UserTemp));

        Assert.False(_fs.FileExists(Temp + @"\setup\deep\a.tmp"));
        Assert.False(_fs.DirectoryExists(Temp + @"\setup\deep"));
        Assert.True(_fs.DirectoryExists(Temp));
    }

    [Fact]
    public void Non_recursive_targets_do_not_remove_subdirectories()
    {
        var dumps = Local + @"\CrashDumps";
        _fs.AddDirectory(dumps + @"\empty");
        _fs.AddFile(dumps + @"\a.dmp", 1, DaysAgo(30));

        _executor.Delete(Category(CleanupCatalog.UserCrashDumps));

        Assert.True(_fs.DirectoryExists(dumps + @"\empty"));
    }

    [Fact]
    public void Personal_folders_are_never_touched_by_any_catalog_category()
    {
        var personal = new[] { KnownFolder.Documents, KnownFolder.Downloads, KnownFolder.Desktop, KnownFolder.Pictures, KnownFolder.Videos, KnownFolder.Music };
        var planted = new List<string>();
        foreach (var folder in personal)
        {
            var root = _fs.GetKnownFolder(folder)!;
            foreach (var name in new[] { "report.docx", "setup.tmp", "crash.dmp", "thumbcache_1.db", @"Cache\Cache_Data\f_0001", @"sub\MEMORY.DMP" })
            {
                var path = root + "\\" + name;
                _fs.AddFile(path, 1234, DaysAgo(400));
                planted.Add(path);
            }
        }
        // Des fichiers réellement nettoyables coexistent : le nettoyage fonctionne, mais ne déborde jamais.
        _fs.AddFile(Temp + @"\junk.tmp", 10, DaysAgo(5));

        foreach (var category in CleanupCatalog.All)
        {
            foreach (var (directory, _) in _executor.ResolveRoots(category))
            {
                foreach (var folder in personal)
                    Assert.False(CleanupExecutor.IsContained(directory, _fs.GetKnownFolder(folder)!), $"{category.Id} → {directory}");
            }
            _executor.Delete(category);
        }

        Assert.False(_fs.FileExists(Temp + @"\junk.tmp"));
        Assert.All(planted, path => Assert.True(_fs.FileExists(path), path));
    }

    [Fact]
    public void Deletion_honours_cancellation()
    {
        _fs.AddFile(Temp + @"\a.tmp", 1, DaysAgo(5));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => _executor.Delete(Category(CleanupCatalog.UserTemp), null, cts.Token));
        Assert.True(_fs.FileExists(Temp + @"\a.tmp"));
    }

    [Fact]
    public void Deletion_reports_progress_per_root()
    {
        _fs.AddFile(Local + @"\Microsoft\Windows\WER\ReportArchive\r1\report.wer", 10, DaysAgo(30));
        _fs.AddFile(Local + @"\Microsoft\Windows\WER\ReportQueue\r2\report.wer", 10, DaysAgo(30));
        var reports = new List<double>();

        var result = _executor.Delete(Category(CleanupCatalog.UserErrorReports), new SynchronousProgress(reports.Add));

        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(new[] { 50d, 100d }, reports);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
