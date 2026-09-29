using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.Startup;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class StartupBootTimeTests
{
    private readonly TestUi _ui = new();
    private readonly FakeStartupService _startup = new();
    private readonly FakeBootTimeService _boot = new();

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    private StartupViewModel Create() => new(_ui.Context, _startup, _boot);

    private BootRecord Boot(int daysAgo, double seconds, int? apps = 14)
        => new(Now.AddDays(-daysAgo), TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(seconds * 0.7), TimeSpan.FromSeconds(seconds * 0.3), apps);

    private static string Plain(string text) => text.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    [Fact]
    public async Task Latest_measured_boot_is_shown_with_breakdown()
    {
        _boot.Report = new BootTimeReport([], new BootPerformanceData(Now, [Boot(1, 42)], []), null, null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.True(vm.HasBootSection);
        Assert.True(vm.HasBootMeasurement);
        Assert.Equal("42 s", Plain(vm.BootValueText));
        Assert.StartsWith("Dernier démarrage complet mesuré par Windows, le ", vm.BootCaptionText);
        Assert.Equal("Jusqu'au bureau : 29 s · Finalisation : 13 s · 14 programmes lancés", Plain(vm.BootBreakdownText));
        Assert.False(vm.HasBootComparison);
        Assert.Equal(string.Empty, vm.BootInfoText);
    }

    [Fact]
    public async Task Without_measurement_the_last_boot_is_named_and_nothing_is_estimated()
    {
        _boot.Report = new BootTimeReport(
            [new BootSession(Now.AddHours(-2), BootKind.FastStartup, null), new BootSession(Now.AddDays(-1), BootKind.FastStartup, null), new BootSession(Now.AddDays(-2), BootKind.Cold, null)],
            null, null, null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.False(vm.HasBootMeasurement);
        Assert.Equal(string.Empty, vm.BootValueText);
        Assert.Contains("(démarrage rapide)", vm.BootInfoText);
        Assert.True(vm.ShowFastStartupNote);
    }

    [Fact]
    public async Task Comparison_reports_measured_difference_honestly()
    {
        var comparison = new BootComparison(Now.AddDays(-5), TimeSpan.FromSeconds(58), 5, TimeSpan.FromSeconds(41), 2);
        _boot.Report = new BootTimeReport([], new BootPerformanceData(Now, [Boot(1, 41)], []), Now.AddDays(-5), comparison);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.True(vm.HasBootComparison);
        Assert.Contains("58 s en moyenne (5 démarrages)", Plain(vm.BootComparisonText));
        Assert.Contains("41 s en moyenne (2 démarrages)", Plain(vm.BootComparisonText));
        Assert.Equal("17 s de moins", Plain(vm.BootDifferenceText));
        Assert.True(vm.BootImproved);
    }

    [Fact]
    public async Task Slower_boot_after_change_is_not_hidden()
    {
        var comparison = new BootComparison(Now.AddDays(-5), TimeSpan.FromSeconds(40), 3, TimeSpan.FromSeconds(45), 1);
        _boot.Report = new BootTimeReport([], new BootPerformanceData(Now, [Boot(1, 45)], []), Now.AddDays(-5), comparison);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("5,0 s de plus", Plain(vm.BootDifferenceText));
        Assert.False(vm.BootImproved);
    }

    [Fact]
    public async Task Pending_comparison_explains_what_is_missing()
    {
        _boot.Report = new BootTimeReport([], null, Now.AddDays(-1), null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.True(vm.HasBootComparison);
        Assert.StartsWith("Vous avez modifié les programmes au démarrage le ", vm.BootComparisonText);
        Assert.Equal(string.Empty, vm.BootDifferenceText);
    }

    [Fact]
    public async Task Degradations_are_listed_and_matching_startup_programs_get_a_badge()
    {
        _startup.Entries.Add(new StartupEntry
        {
            Id = "od", Name = "Microsoft OneDrive", Location = StartupLocation.RegistryRunUser, SourcePath = @"HKCU\Run", ItemName = "OneDrive",
            ExecutablePath = @"C:\Users\x\AppData\Local\Microsoft\OneDrive\OneDrive.exe", IsEnabled = true,
        });
        _startup.Entries.Add(new StartupEntry { Id = "other", Name = "Other", Location = StartupLocation.RegistryRunUser, SourcePath = @"HKCU\Run", ItemName = "Other", ExecutablePath = @"C:\x\other.exe", IsEnabled = true });
        var degradations = new[]
        {
            new BootDegradation(Now.AddDays(-2), BootDegradationKind.Application, "Microsoft OneDrive", "OneDrive.exe", TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4)),
            new BootDegradation(Now.AddDays(-3), BootDegradationKind.Service, "Spouleur d'impression", null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1.5)),
            new BootDegradation(Now.AddDays(-60), BootDegradationKind.Driver, "Ancien pilote", null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(12)),
        };
        _boot.Report = new BootTimeReport([], new BootPerformanceData(Now, [Boot(1, 50)], degradations), null, null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(["Microsoft OneDrive", "Spouleur d'impression"], vm.BootDegradations.Select(d => d.Name));
        Assert.Equal("+4,0 s", Plain(vm.BootDegradations[0].DelayText));
        Assert.Equal("Service", vm.BootDegradations[1].KindText);
        var onedrive = vm.Items.Single(i => i.Id == "od");
        Assert.True(onedrive.HasBootDelay);
        Assert.Equal("A ralenti le démarrage de 4,0 s", Plain(onedrive.BootDelayText!));
        Assert.False(vm.Items.Single(i => i.Id == "other").HasBootDelay);
    }

    [Fact]
    public async Task Reading_measurements_refreshes_the_section()
    {
        _boot.ReportAfterRead = new BootTimeReport([], new BootPerformanceData(Now, [Boot(0, 35)], []), null, null);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.False(vm.HasBootMeasurement);

        await vm.ReadBootMeasurementsCommand.ExecuteAsync(null);

        Assert.Equal(1, _boot.ReadCount);
        Assert.True(vm.HasBootMeasurement);
        Assert.Equal("Mesures de démarrage lues.", vm.StatusMessage);
    }

    [Fact]
    public async Task Declined_permission_keeps_section_unchanged()
    {
        _boot.ReadResult = OperationResult.Fail(OperationErrorKind.ElevationCancelled);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ReadBootMeasurementsCommand.ExecuteAsync(null);
        Assert.Equal("Autorisation refusée.", vm.ErrorText);
        Assert.False(vm.HasBootMeasurement);
    }

    [Theory]
    [InlineData(0.84, "0,8 s")]
    [InlineData(42.4, "42 s")]
    [InlineData(95, "1 min 35 s")]
    [InlineData(600, "10 min 00 s")]
    public void Boot_durations_are_readable(double seconds, string expected)
        => Assert.Equal(expected, Plain(StartupViewModel.BootDuration(_ui.Formatter, _ui.Localizer, TimeSpan.FromSeconds(seconds))));

    [Fact]
    public async Task Without_boot_service_the_section_is_hidden()
    {
        var vm = new StartupViewModel(_ui.Context, _startup);
        await vm.OnNavigatedToAsync(null);
        Assert.False(vm.HasBootSection);
        Assert.False(vm.IsBootLoaded);
    }
}
