using PCBoost.Core.Common;
using PCBoost.Core.Models.Files;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Tests;

public sealed class FilesViewModelTests
{
    private const long MiB = 1024 * 1024;
    private readonly TestUi _ui = new();
    private readonly FakeCleanup _cleanup = new();
    private readonly FakeShellService _shell = new();

    private sealed class FakeCleanup : IFileCleanupService
    {
        public FileScanResult Result { get; set; } = null!;
        public List<IReadOnlyCollection<string>> Requests { get; } = [];
        public Func<IReadOnlyCollection<string>, RecycleReport>? Handler { get; set; }
        public int Scans { get; private set; }

        public Task<FileScanResult> ScanAsync(IProgress<FileScanProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            Scans++;
            progress?.Report(new FileScanProgress(FileScanStage.Listing, 100, 0, 0));
            return Task.FromResult(Result);
        }

        public Task<RecycleReport> MoveToRecycleBinAsync(FileScanResult scan, IReadOnlyCollection<string> paths, CancellationToken cancellationToken = default)
        {
            Requests.Add(paths);
            return Task.FromResult(Handler?.Invoke(paths) ?? new RecycleReport(paths.Count, paths.Count * 10 * MiB, [], false));
        }
    }

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    private async Task<FilesViewModel> LoadAsync()
    {
        _cleanup.Result = new FileScanResult(Now, [@"C:\Users\Test\Videos"],
            [new LargeFile(@"C:\Users\Test\Videos\film.mkv", 3000 * MiB, Now.AddDays(-40))],
            [new DuplicateGroup("H", 10 * MiB, [new DuplicateFile(@"C:\Users\Test\Downloads\a.zip", Now.AddDays(-20)), new DuplicateFile(@"C:\Users\Test\Documents\a.zip", Now.AddDays(-2)), new DuplicateFile(@"C:\Users\Test\Desktop\a.zip", Now.AddDays(-1))])],
            1234, 0, false);
        var vm = new FilesViewModel(_ui.Context, _cleanup, _shell);
        await vm.OnNavigatedToAsync(null);
        return vm;
    }

    [Fact]
    public async Task Scan_shows_large_files_and_duplicate_groups_with_nothing_selected()
    {
        var vm = await LoadAsync();

        Assert.True(vm.HasResult);
        Assert.Equal("film.mkv", Assert.Single(vm.LargeFiles).Name);
        var group = Assert.Single(vm.DuplicateGroups);
        Assert.Equal("a.zip", group.Title);
        Assert.Contains("3 copies identiques", group.DetailText);
        Assert.StartsWith("1 groupe de fichiers identiques", vm.DuplicatesSummary);
        Assert.Equal("Aucun fichier sélectionné", vm.SelectionText);
        Assert.False(vm.RecycleCommand.CanExecute(null));
        Assert.Contains("fichiers analysés", vm.SummaryText);
    }

    [Fact]
    public async Task All_copies_of_a_group_can_never_be_selected()
    {
        var vm = await LoadAsync();
        var group = vm.DuplicateGroups[0];

        group.Files[0].IsSelected = true;
        group.Files[1].IsSelected = true;
        group.Files[2].IsSelected = true;

        Assert.False(group.Files[2].IsSelected);
        Assert.True(group.ShowKeepHint);
        Assert.Equal(2, vm.SelectedCount);

        group.SelectCopiesCommand.Execute(null);
        Assert.Equal([false, true, true], group.Files.Select(f => f.IsSelected));
        Assert.False(group.ShowKeepHint);
    }

    [Fact]
    public async Task Recycle_confirms_sends_selection_and_removes_moved_files()
    {
        var vm = await LoadAsync();
        vm.LargeFiles[0].IsSelected = true;
        vm.DuplicateGroups[0].SelectCopiesCommand.Execute(null);
        Assert.Equal(3, vm.SelectedCount);

        await vm.RecycleCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_ui.Dialogs.Confirmations);
        Assert.Equal("Files_Confirm_Title", confirm.Title.Key);
        Assert.Contains(confirm.Details!, d => d.Key == "Files_Confirm_Restorable");
        Assert.Equal(3, Assert.Single(_cleanup.Requests).Count);
        Assert.StartsWith("3 fichiers envoyés à la Corbeille", vm.StatusMessage);
        Assert.Empty(vm.LargeFiles);
        Assert.Empty(vm.DuplicateGroups);
        Assert.False(vm.HasDuplicates);
        Assert.Equal(0, vm.SelectedCount);
    }

    [Fact]
    public async Task Declined_confirmation_sends_nothing()
    {
        var vm = await LoadAsync();
        vm.LargeFiles[0].IsSelected = true;
        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await vm.RecycleCommand.ExecuteAsync(null);
        Assert.Empty(_cleanup.Requests);
        Assert.Single(vm.LargeFiles);
    }

    [Fact]
    public async Task Failures_are_reported_and_failed_files_stay_listed()
    {
        var vm = await LoadAsync();
        vm.LargeFiles[0].IsSelected = true;
        vm.DuplicateGroups[0].Files[1].IsSelected = true;
        _cleanup.Handler = paths => new RecycleReport(1, 10 * MiB,
            [(@"C:\Users\Test\Videos\film.mkv", OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Files_Changed")))], false);

        await vm.RecycleCommand.ExecuteAsync(null);

        Assert.Single(vm.LargeFiles);
        Assert.Equal(2, vm.DuplicateGroups.Single().Files.Count);
        Assert.StartsWith("1 fichier n'a pas été envoyé", vm.ErrorText);
    }

    [Fact]
    public async Task Reveal_opens_explorer_on_the_file()
    {
        var vm = await LoadAsync();
        vm.LargeFiles[0].RevealCommand.Execute(null);
        Assert.Equal(@"reveal:C:\Users\Test\Videos\film.mkv", Assert.Single(_shell.Calls));
        Assert.Equal(1, _cleanup.Scans);
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(1, _cleanup.Scans);
    }
}
