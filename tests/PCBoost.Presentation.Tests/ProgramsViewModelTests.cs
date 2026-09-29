using PCBoost.Core.Common;
using PCBoost.Core.Models.Programs;
using PCBoost.Core.Services;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Tests;

public sealed class ProgramsViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeInventory _inventory = new();
    private readonly FakeShellService _shell = new();

    private sealed class FakeInventory : IProgramInventoryService
    {
        public ProgramInventory Inventory { get; set; } = new(DateTimeOffset.UnixEpoch, [], 0, null);
        public UninstallResult Result { get; set; } = new(UninstallOutcome.Removed);
        public OperationResult LastRunResult { get; set; } = OperationResult.Ok();
        public List<InstalledProgram> Uninstalled { get; } = [];
        public int ReadCount { get; private set; }
        public int LoadCount { get; private set; }

        public Task<ProgramInventory> GetInventoryAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(Inventory);
        }

        public Task<OperationResult> ReadLastRunAsync(CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return Task.FromResult(LastRunResult);
        }

        public Task<UninstallResult> UninstallAsync(InstalledProgram program, CancellationToken cancellationToken = default)
        {
            Uninstalled.Add(program);
            if (Result.Outcome == UninstallOutcome.Removed) Inventory = Inventory with { Programs = Inventory.Programs.Where(p => p.Id != program.Id).ToList() };
            return Task.FromResult(Result);
        }
    }

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    private InstalledProgram P(string name, long? size, int? daysSinceUse, LastUseSource source = LastUseSource.FileAccess, bool canUninstall = true)
        => new($"LocalMachine|Registry64|{name}", name, "Éditeur", "1.0", new DateOnly(2024, 1, 1), size, true, null, ProgramScope.Machine, null,
            daysSinceUse is { } d ? Now.AddDays(-d) : null, daysSinceUse is null ? LastUseSource.Unknown : source,
            canUninstall ? new UninstallCommand($@"C:\Apps\{name}\uninstall.exe", "", false, null) : null);

    private async Task<ProgramsViewModel> LoadAsync(params InstalledProgram[] programs)
    {
        _inventory.Inventory = new ProgramInventory(Now, programs, 7, null);
        var vm = new ProgramsViewModel(_ui.Context, _inventory, _shell);
        await vm.OnNavigatedToAsync(null);
        return vm;
    }

    [Fact]
    public async Task Programs_are_sorted_by_size_with_summary_and_hidden_count()
    {
        var vm = await LoadAsync(P("Petit", 10 * 1024 * 1024, 5), P("Gros", 5L * 1024 * 1024 * 1024, 200), P("Inconnu", null, null));

        Assert.Equal(["Gros", "Petit", "Inconnu"], vm.Items.Select(i => i.Name));
        Assert.StartsWith("3 programmes · ", vm.SummaryText);
        Assert.StartsWith("7 composants protégés non affichés", vm.HiddenText);
        Assert.StartsWith("Dates d'utilisation approximatives", vm.LastRunText);
        Assert.Equal(1, vm.RarelyUsedCount);
        Assert.True(vm.Items[0].IsRarelyUsed);
        Assert.Equal("Taille non indiquée", vm.Items[2].SizeText);
        Assert.Equal("Dernière utilisation inconnue", vm.Items[2].LastUsedText);
        Assert.Equal("(approximatif)", vm.Items[0].LastUsedHint);
        Assert.StartsWith("Dernier accès aux fichiers : ", vm.Items[0].LastUsedText);
    }

    [Fact]
    public async Task Filters_and_sorts_apply()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300), P("Beta", 200, 10), P("Gamma", 50, 120, LastUseSource.WindowsPrefetch));

        vm.RarelyUsedOnly = true;
        Assert.Equal(["Alpha", "Gamma"], vm.Items.Select(i => i.Name));
        Assert.Equal("(d'après Windows)", vm.Items.Single(i => i.Name == "Gamma").LastUsedHint);
        Assert.StartsWith("Dernière utilisation : ", vm.Items.Single(i => i.Name == "Gamma").LastUsedText);

        vm.RarelyUsedOnly = false;
        vm.SelectedSortIndex = (int)ProgramSort.LastUsed;
        Assert.Equal(["Alpha", "Gamma", "Beta"], vm.Items.Select(i => i.Name));

        vm.SearchText = "bet";
        Assert.Equal(["Beta"], vm.Items.Select(i => i.Name));
        vm.SearchText = "zzz";
        Assert.True(vm.ShowEmpty);
        Assert.Equal("Aucun programme ne correspond à ces critères.", vm.EmptyText);
    }

    [Fact]
    public async Task Uninstall_asks_confirmation_then_refreshes()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300), P("Beta", 200, 10));
        var alpha = vm.Items.Single(i => i.Name == "Alpha");

        await alpha.UninstallCommand.ExecuteAsync(null);

        var confirm = Assert.Single(_ui.Dialogs.Confirmations);
        Assert.True(confirm.IsDestructive);
        Assert.Equal("Programs_Confirm_Title", confirm.Title.Key);
        Assert.Contains(confirm.Details!, d => d.Key == "Programs_Confirm_Irreversible");
        Assert.Equal("Alpha", Assert.Single(_inventory.Uninstalled).Name);
        Assert.Equal("« Alpha » a été désinstallé.", vm.StatusMessage);
        Assert.Equal(["Beta"], vm.Items.Select(i => i.Name));
        Assert.False(vm.IsUninstalling);
    }

    [Fact]
    public async Task Declining_confirmation_runs_nothing()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300));
        _ui.Dialogs.NextResult = DialogResultKind.Cancel;
        await vm.Items[0].UninstallCommand.ExecuteAsync(null);
        Assert.Empty(_inventory.Uninstalled);
        Assert.Null(vm.StatusMessage);
    }

    [Fact]
    public async Task Still_installed_and_failures_are_explained()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300));
        _inventory.Result = new UninstallResult(UninstallOutcome.StillInstalled);
        await vm.Items[0].UninstallCommand.ExecuteAsync(null);
        Assert.StartsWith("« Alpha » est toujours installé.", vm.StatusMessage);

        _inventory.Result = new UninstallResult(UninstallOutcome.Failed, OperationResult.Fail(OperationErrorKind.ElevationCancelled));
        await vm.Items[0].UninstallCommand.ExecuteAsync(null);
        Assert.Equal("Autorisation refusée.", vm.ErrorText);
    }

    [Fact]
    public async Task Programs_without_official_command_cannot_be_uninstalled_here()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300, canUninstall: false));
        Assert.False(vm.Items[0].UninstallCommand.CanExecute(null));
        Assert.Equal("Désinstallation depuis les Paramètres de Windows uniquement.", vm.Items[0].UninstallUnavailableText);
        vm.OpenWindowsSettingsCommand.Execute(null);
        Assert.Equal("uri:ms-settings:appsfeatures", Assert.Single(_shell.Calls));
    }

    [Fact]
    public async Task Reading_windows_records_reloads_the_list()
    {
        var vm = await LoadAsync(P("Alpha", 100, 300));
        await vm.ReadLastRunCommand.ExecuteAsync(null);
        Assert.Equal(1, _inventory.ReadCount);
        Assert.Equal(2, _inventory.LoadCount);
        Assert.Equal("Dates d'utilisation mises à jour d'après Windows.", vm.StatusMessage);
    }
}
