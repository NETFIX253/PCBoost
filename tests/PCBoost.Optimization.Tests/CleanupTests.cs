using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Tests;

public sealed class CleanupTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private DateTimeOffset Old => _h.Clock.UtcNow.AddDays(-30);

    private const string UserTemp = @"C:\Users\Test\AppData\Local\Temp";

    [Fact]
    public void Categories_ComeFromCatalog_AndOnlySafeOnesAreSelectedByDefault()
    {
        var categories = _h.Get<ICleanupService>().GetCategories();

        Assert.Equal(CleanupCatalog.All.Select(c => c.Id), categories.Select(c => c.Id));
        Assert.All(categories.Where(c => c.SelectedByDefault), c => Assert.Equal(SafetyCategory.Safe, c.Safety));
        Assert.All(categories, c => Assert.Equal($"Cleanup_{c.Id}_Name", c.Name.Key));
    }

    [Fact]
    public async Task Scan_BrowserRunning_MakesItsCacheUnavailableWithReason()
    {
        _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache\Cache_Data\f_0001", 5_000_000, Old);
        _h.Processes.Add(300, "chrome.exe", hasWindow: true);

        var results = await _h.Get<ICleanupService>().ScanAsync([CleanupCatalog.ChromeCache]);

        var chrome = Assert.Single(results);
        Assert.False(chrome.Available);
        Assert.Equal("Opt_Cleanup_CloseApp", chrome.UnavailableReason!.Key);
        Assert.Equal("Google Chrome", chrome.UnavailableReason.Args[0]);
        Assert.Equal(5_000_000, chrome.Bytes);
    }

    [Fact]
    public async Task Clean_BrowserRunning_LeavesCacheUntouched()
    {
        var file = _h.FileSystem.AddFile(@"C:\Users\Test\AppData\Local\Microsoft\Edge\User Data\Default\Cache\Cache_Data\data_1", 1000, Old);
        _h.Processes.Add(301, "msedge.exe");

        var summary = await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.EdgeCache]);

        Assert.Equal(OperationErrorKind.InUse, summary.Results.Single().Outcome.Error);
        Assert.True(_h.FileSystem.FileExists(file.Path));
    }

    [Fact]
    public async Task Clean_KeepsRecentAndLockedFiles_DeletesOldOnes()
    {
        _h.FileSystem.AddFile(UserTemp + @"\old.tmp", 1000, Old);
        _h.FileSystem.AddFile(UserTemp + @"\sub\old2.tmp", 2000, Old);
        _h.FileSystem.AddFile(UserTemp + @"\recent.tmp", 3000, _h.Clock.UtcNow.AddHours(-2));
        _h.FileSystem.AddFile(UserTemp + @"\locked.tmp", 4000, Old, locked: true);

        var summary = await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.UserTemp]);

        var result = summary.Results.Single();
        Assert.True(result.Outcome.Success);
        Assert.Equal(3000, result.BytesFreed);
        Assert.Equal(2, result.FilesDeleted);
        Assert.Equal(1, result.FilesSkipped);
        Assert.True(_h.FileSystem.FileExists(UserTemp + @"\recent.tmp"));
        Assert.True(_h.FileSystem.FileExists(UserTemp + @"\locked.tmp"));
        Assert.False(_h.FileSystem.FileExists(UserTemp + @"\old.tmp"));
    }

    [Fact]
    public async Task Clean_ElevatedCategories_UseASingleGroupedElevatedRequest()
    {
        _h.Elevation.Handler = request => new ElevatedResponse(OperationResult.Ok(), new Dictionary<string, string>
        {
            ["windows-temp.bytes"] = "1000", ["windows-temp.deleted"] = "3", ["windows-temp.skipped"] = "1",
            ["system-error-reports.bytes"] = "500", ["system-error-reports.deleted"] = "2", ["system-error-reports.skipped"] = "0",
        });

        var summary = await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.WindowsTemp, CleanupCatalog.SystemErrorReports, CleanupCatalog.UserTemp]);

        var request = Assert.Single(_h.Elevation.Requests);
        Assert.Equal(ElevatedOperations.CleanupCategory, request.Operation);
        Assert.Equal("windows-temp,system-error-reports", request.Parameters["categories"]);
        Assert.Single(request.Parameters);
        var windowsTemp = summary.Results.Single(r => r.CategoryId == CleanupCatalog.WindowsTemp);
        Assert.Equal(1000, windowsTemp.BytesFreed);
        Assert.Equal(3, windowsTemp.FilesDeleted);
        Assert.Equal(1, windowsTemp.FilesSkipped);
        Assert.Equal(1500, summary.TotalBytesFreed);
    }

    [Fact]
    public async Task Clean_UacRefused_ElevatedCategoriesCancelled_OthersContinue()
    {
        _h.Elevation.UserCancels = true;
        _h.FileSystem.AddFile(UserTemp + @"\old.tmp", 1000, Old);
        _h.FileSystem.AddFile(@"C:\Windows\Temp\sys.tmp", 5000, Old);

        var summary = await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.UserTemp, CleanupCatalog.WindowsTemp]);

        Assert.Equal(OperationErrorKind.ElevationCancelled, summary.Results.Single(r => r.CategoryId == CleanupCatalog.WindowsTemp).Outcome.Error);
        Assert.True(summary.Results.Single(r => r.CategoryId == CleanupCatalog.UserTemp).Outcome.Success);
        Assert.False(_h.FileSystem.FileExists(UserTemp + @"\old.tmp"));
        Assert.True(_h.FileSystem.FileExists(@"C:\Windows\Temp\sys.tmp"));
    }

    [Fact]
    public async Task Clean_AllCategories_NeverTouchesDocumentsDownloadsOrPictures()
    {
        _h.Elevation.IsElevated = true;
        var personal = new[]
        {
            @"C:\Users\Test\Documents\thesis.docx",
            @"C:\Users\Test\Downloads\setup.exe",
            @"C:\Users\Test\Pictures\holiday.jpg",
            @"C:\Users\Test\Desktop\notes.txt",
            @"C:\Users\Test\Videos\clip.mp4",
        };
        foreach (var p in personal) _h.FileSystem.AddFile(p, 10_000, Old);
        _h.FileSystem.AddFile(UserTemp + @"\x.tmp", 10, Old);

        await _h.Get<ICleanupService>().CleanAsync(CleanupCatalog.All.Select(c => c.Id).ToList());

        Assert.All(personal, p => Assert.True(_h.FileSystem.FileExists(p), p));
        Assert.False(_h.FileSystem.FileExists(UserTemp + @"\x.tmp"));
        Assert.True(_h.RecycleBin.Emptied);
    }

    [Fact]
    public async Task Clean_RecordsEachCategoryAsIrreversibleInCleanupSession_AndJournals()
    {
        _h.FileSystem.AddFile(UserTemp + @"\old.tmp", 1000, Old);

        await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.UserTemp, CleanupCatalog.RecycleBin]);

        var session = (await _h.Get<IRollbackManager>().GetHistoryAsync()).Single();
        Assert.Equal(SessionType.Cleanup, session.Type);
        Assert.Equal(SessionStatus.Completed, session.Status);
        Assert.Equal(1000 + 500 * ByteSize.MiB, session.BytesFreed);
        Assert.Equal(2, session.Changes.Count);
        Assert.All(session.Changes, c =>
        {
            Assert.Equal(ChangeStatus.Irreversible, c.Status);
            Assert.False(c.Reversible);
        });
        Assert.Contains(session.Changes, c => c.Kind == ChangeKinds.RecycleBin);
        Assert.Contains(session.Changes, c => c.Kind == ChangeKinds.FileDeletion);
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Opt_Journal_CleanupDone");
    }

    [Fact]
    public async Task Scan_RecycleBin_UsesProvider_AndElevatedCategoriesAreFlagged()
    {
        _h.FileSystem.AddFile(@"C:\Windows\Temp\readable.tmp", 700, Old);

        var results = await _h.Get<ICleanupService>().ScanAsync([CleanupCatalog.RecycleBin, CleanupCatalog.WindowsTemp]);

        var bin = results.Single(r => r.CategoryId == CleanupCatalog.RecycleBin);
        Assert.Equal(500 * ByteSize.MiB, bin.Bytes);
        Assert.Equal(42, bin.FileCount);
        var windows = results.Single(r => r.CategoryId == CleanupCatalog.WindowsTemp);
        Assert.True(windows.RequiresElevation);
        Assert.Equal(700, windows.Bytes);
        Assert.Empty(_h.Elevation.Requests);
    }

    [Fact]
    public async Task Clean_Cancelled_ReturnsCancelledResultsWithoutThrowing_AndRecordsThem()
    {
        _h.FileSystem.AddFile(UserTemp + @"\old.tmp", 1000, Old);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var summary = await _h.Get<ICleanupService>().CleanAsync([CleanupCatalog.UserTemp, CleanupCatalog.WindowsTemp], null, cts.Token);

        Assert.All(summary.Results, r => Assert.Equal(OperationErrorKind.Cancelled, r.Outcome.Error));
        Assert.True(_h.FileSystem.FileExists(UserTemp + @"\old.tmp"));
        Assert.Empty(_h.Elevation.Requests);
    }
}
