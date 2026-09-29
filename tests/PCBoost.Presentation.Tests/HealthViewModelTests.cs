using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class HealthViewModelTests
{
    private readonly TestUi _ui = new();
    private readonly FakeHardwareHealthService _health = new();
    private readonly FakeThermalThrottlingDetector _thermal = new();

    private HealthViewModel Create() => new(_ui.Context, _health, _thermal);

    private DateTimeOffset Now => _ui.Clock.UtcNow;

    private static DiskHealthInfo Disk(DiskHealthStatus status, int? wear = null, long? uncorrected = null, bool withCounters = true, StorageMediaType media = StorageMediaType.Ssd)
        => new("0", "Samsung SSD 870", media, StorageBusType.Sata, 500_107_862_016, status, true,
            withCounters ? new DiskReliability("0", wear, 38, 55, 4200, 0, uncorrected, 0, DateTimeOffset.UnixEpoch) : null);

    private HardwareHealthReport Report(DiskHealthInfo[]? disks = null, BatteryInfo[]? batteries = null, DeviceProblem[]? devices = null,
        ThermalLimitInfo? thermal = null, DateTimeOffset? countersAt = null)
        => new(Now, disks ?? [Disk(DiskHealthStatus.Healthy, wear: 5)], Availability.Available, countersAt ?? Now,
            batteries ?? [], Availability.Available, devices ?? [], Availability.Available, thermal ?? new ThermalLimitInfo(0, null, 0, null));

    [Fact]
    public async Task Healthy_pc_shows_good_overall_status_and_no_battery_on_desktop()
    {
        _health.Report = Report();
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(1, _health.RefreshCount);
        Assert.True(vm.HasReport);
        Assert.Equal(HealthLevel.Good, vm.OverallLevel);
        Assert.Equal("Aucun problème matériel détecté", vm.OverallTitle);
        var disk = Assert.Single(vm.Disks);
        Assert.Equal("Bon état", disk.StatusText);
        Assert.Equal("SSD · SATA · 465,8 Go", disk.DetailsText.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Contains(disk.Rows, r => r.Label == "Usure (SSD)" && r.Value == "5 %");
        Assert.False(vm.HasBatteries);
        Assert.Equal("Aucune batterie détectée : ce PC fonctionne sur secteur.", vm.NoBatteryText);
        Assert.Equal("Aucune limitation observée", vm.ThermalStatusText);
        Assert.False(vm.ShowThermalAdvice);
        Assert.Equal("Aucun périphérique en erreur.", vm.DevicesStatusText);
    }

    [Fact]
    public async Task Recent_report_is_reused_without_new_reading()
    {
        _health.Latest = Report();
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(0, _health.RefreshCount);
        Assert.True(vm.HasReport);
    }

    [Fact]
    public async Task Failing_disk_is_critical_with_backup_advice()
    {
        _health.Report = Report([Disk(DiskHealthStatus.Healthy, uncorrected: 3)]);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal(HealthLevel.Critical, vm.OverallLevel);
        Assert.Equal("Un disque est signalé en mauvais état. Sauvegardez vos fichiers importants dès maintenant.", vm.OverallDetail);
        var disk = vm.Disks.Single();
        Assert.Equal(HealthLevel.Critical, disk.Level);
        Assert.StartsWith("Sauvegardez vos fichiers importants dès maintenant", disk.AdviceText);
        Assert.Contains(disk.Rows, r => r.Label == "Erreurs de lecture non corrigées" && r.Value == "3" && r.HasDetail);
    }

    [Fact]
    public async Task Counters_not_read_are_shown_as_not_read_and_hdd_hides_wear()
    {
        _health.Report = Report([Disk(DiskHealthStatus.Healthy, withCounters: false, media: StorageMediaType.Hdd)]) with { ReliabilityMeasuredAt = null };
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var disk = vm.Disks.Single();
        Assert.DoesNotContain(disk.Rows, r => r.Label == "Usure (SSD)");
        Assert.All(disk.Rows, r => Assert.Equal("Non lu", r.Value));
        Assert.StartsWith("Les compteurs détaillés", vm.CountersText);
        Assert.Equal("Aucun problème signalé. Lisez les compteurs détaillés pour connaître l'usure des disques.", vm.OverallDetail);
    }

    [Fact]
    public async Task Reading_counters_updates_disks_and_reports_status()
    {
        _health.Report = Report([Disk(DiskHealthStatus.Healthy, withCounters: false)]) with { ReliabilityMeasuredAt = null };
        _health.ReportAfterRead = Report([Disk(DiskHealthStatus.Healthy, wear: 75)]);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        await vm.ReadCountersCommand.ExecuteAsync(null);

        Assert.Equal(1, _health.ReadCount);
        Assert.Equal("Compteurs des disques lus.", vm.StatusMessage);
        Assert.Equal(HealthLevel.Warning, vm.Disks.Single().Level);
        Assert.Equal(HealthLevel.Warning, vm.OverallLevel);
        Assert.Equal("1 point à examiner ci-dessous.", vm.OverallDetail);
    }

    [Fact]
    public async Task Declined_permission_shows_error_and_keeps_previous_state()
    {
        _health.Report = Report();
        _health.ReadResult = OperationResult.Fail(OperationErrorKind.ElevationCancelled);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        await vm.ReadCountersCommand.ExecuteAsync(null);
        Assert.Equal("Autorisation refusée.", vm.ErrorText);
        Assert.Null(vm.StatusMessage);
        Assert.Equal(HealthLevel.Good, vm.Disks.Single().Level);
    }

    [Theory]
    [InlineData(50_000L, 45_000L, HealthLevel.Good, "Bonne capacité")]
    [InlineData(50_000L, 35_000L, HealthLevel.Warning, "Batterie vieillissante")]
    [InlineData(50_000L, 25_000L, HealthLevel.Critical, "Batterie usée")]
    public async Task Battery_level_follows_remaining_capacity(long design, long full, HealthLevel level, string status)
    {
        _health.Report = Report(batteries: [new BatteryInfo("DELL 7FHHV", "SMP", "Li-ion", design, full, 312)]);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        var battery = Assert.Single(vm.Batteries);
        Assert.Equal(level, battery.Level);
        Assert.Equal(status, battery.StatusText);
        Assert.Equal("SMP DELL 7FHHV", battery.Name);
        Assert.Contains(battery.Rows, r => r.Label == "Capacité d'origine" && r.Value == "50 Wh");
        Assert.Contains(battery.Rows, r => r.Label == "Cycles de charge" && r.Value == "312");
    }

    [Fact]
    public async Task Throttling_episode_shows_cooling_advice_and_live_update()
    {
        _health.Report = Report();
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.False(vm.ShowThermalAdvice);

        _thermal.Raise(new ThrottlingEpisode(Now, TimeSpan.FromSeconds(45), 92, 48, 97));

        Assert.True(vm.ShowThermalAdvice);
        Assert.Equal(HealthLevel.Warning, vm.ThermalLevel);
        Assert.Contains("45 s", Plain(vm.ThermalDetailText));
        Assert.Contains("48 %", Plain(vm.ThermalDetailText));
        Assert.Contains("97", vm.ThermalDetailText);
        Assert.Equal(4, vm.ThermalAdvice.Count);

        vm.OnNavigatedFrom();
        _thermal.Raise(new ThrottlingEpisode(Now, TimeSpan.FromSeconds(90), 92, 30, null));
        Assert.Contains("45 s", Plain(vm.ThermalDetailText));
    }

    private static string Plain(string text) => text.Replace('\u202f', ' ').Replace('\u00a0', ' ');

    [Fact]
    public async Task Firmware_limits_are_reported_with_count()
    {
        _health.Report = Report(thermal: new ThermalLimitInfo(3, Now.AddDays(-2), 0, null));
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("Vitesse du processeur limitée par le microprogramme", vm.ThermalStatusText);
        Assert.StartsWith("Windows a enregistré 3 limitations", vm.ThermalDetailText);
    }

    [Fact]
    public async Task Device_problems_are_described_without_action()
    {
        _health.Report = Report(devices: [new DeviceProblem("Contrôleur réseau", "Net", 28, null), new DeviceProblem("Inconnu", null, 99, null)]);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);

        Assert.Equal("2 périphériques signalés en erreur par Windows.", vm.DevicesStatusText);
        Assert.Equal("Pilote non installé (code 28)", vm.Devices[0].ProblemText);
        Assert.Equal("Réseau", vm.Devices[0].ClassText);
        Assert.Equal("Problème signalé par Windows (code 99)", vm.Devices[1].ProblemText);
        Assert.False(vm.Devices[1].HasClass);
        Assert.Equal(HealthLevel.Warning, vm.OverallLevel);
    }

    [Fact]
    public async Task Unavailable_sources_explain_why_nothing_is_shown()
    {
        _health.Report = HardwareHealthReport.Empty(Now);
        var vm = Create();
        await vm.OnNavigatedToAsync(null);
        Assert.Equal("L'état des disques n'a pas pu être lu.", vm.DisksUnavailableText);
        Assert.Equal("L'état de la batterie n'a pas pu être lu.", vm.NoBatteryText);
        Assert.Equal("La liste des périphériques n'a pas pu être lue.", vm.DevicesStatusText);
    }

    [Fact]
    public void Health_page_is_registered_in_navigation()
    {
        Assert.Equal(typeof(HealthViewModel), PageRegistry.ViewModelTypeFor(PageKeys.Health));
        Assert.Equal("Health_Title", PageRegistry.TitleKeyFor(PageKeys.Health));
    }
}
