using System.Collections.ObjectModel;
using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Navigation;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.Presentation.ViewModels;

namespace PCBoost.Presentation.Tests;

public sealed class CollectionSyncTests
{
    private sealed record Item(int Id);

    [Fact]
    public void SyncTo_ReordersInsertsAndRemoves_PreservingInstances()
    {
        var a = new Item(1);
        var b = new Item(2);
        var c = new Item(3);
        var d = new Item(4);
        var target = new ObservableCollection<Item> { a, b, c };

        CollectionSync.SyncTo(target, [c, d, a]);

        Assert.Equal([c, d, a], target.ToArray());
        Assert.Same(a, target[2]);
    }
}

public sealed class SeriesMathTests
{
    [Fact]
    public void Compute_DownsamplesAndKeepsGapsAsNaN()
    {
        var values = new double?[] { 10, 20, null, null, 40, 60 };
        var result = SeriesMath.Compute(values.Length, i => values[i], v => v, maxPoints: 3);

        Assert.Equal(3, result.Points.Length);
        Assert.Equal(15, result.Points[0]);
        Assert.True(double.IsNaN(result.Points[1]));
        Assert.Equal(50, result.Points[2]);
        Assert.Equal(60, result.Current);
        Assert.Equal(32.5, result.Average);
        Assert.Equal(60, result.Maximum);
    }

    [Fact]
    public void Compute_NoValues_IsUnavailable()
    {
        var result = SeriesMath.Compute(0, _ => null, v => v);
        Assert.Empty(result.Points);
        Assert.Null(result.Average);
    }

    [Fact]
    public void RingBuffer_OverwritesOldest_AndFindsWindowStart()
    {
        var buffer = new MetricRingBuffer(3);
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++) buffer.Add(new MetricPoint(t0.AddSeconds(i), i, null, null, null, null, null, null, null));
        Assert.Equal(3, buffer.Count);
        Assert.Equal(2, buffer[0].Cpu);
        Assert.Equal(4, buffer[2].Cpu);
        Assert.Equal(1, buffer.FirstIndexAtOrAfter(t0.AddSeconds(3)));
    }
}

public sealed class PerformanceViewModelTests
{
    [Fact]
    public async Task LiveSamples_UpdateSeries_AndStatistics()
    {
        var ui = new TestUi();
        var monitor = new FakeMonitor();
        var vm = new PerformanceViewModel(ui.Context, monitor, new EmptyHistory(), new PCBoost.TestUtilities.FakeSettingsService());
        await vm.OnNavigatedToAsync(null);
        Assert.Equal(MonitoringMode.Active, monitor.Mode);
        Assert.False(vm.CpuSeries.IsAvailable);

        for (var i = 0; i < 4; i++)
            monitor.Publish(new SystemMetricsSample(ui.Clock.UtcNow.AddSeconds(i), 10 * (i + 1), 50, 1, 2, null, null, null, 20, null, 2048, 1024, 100));

        Assert.True(vm.CpuSeries.IsAvailable);
        Assert.Equal(4, vm.CpuSeries.Points.Count);
        Assert.Equal("40\u00a0%", vm.CpuSeries.CurrentText);
        Assert.Equal("25\u00a0%", vm.CpuSeries.AverageText);
        Assert.Equal("40\u00a0%", vm.CpuSeries.MaximumText);
        Assert.False(vm.DiskSeries.IsAvailable);
        Assert.Equal("Non disponible sur ce PC", vm.DiskSeries.UnavailableReason);
        Assert.Equal(100, vm.NetworkReceiveSeries.Points.Max());

        vm.OnNavigatedFrom();
        Assert.Equal(MonitoringMode.Background, monitor.Mode);
    }

    private sealed class EmptyHistory : PCBoost.Core.Services.IPerformanceHistoryService
    {
        public Task<IReadOnlyList<PerformanceSnapshot>> GetHistoryAsync(TimeSpan window, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PerformanceSnapshot>>([]);
    }
}

public sealed class RegistrationTests
{
    [Fact]
    public void AddPCBoostPresentation_RegistersAllPageViewModels_WithExpectedLifetimes()
    {
        var services = new ServiceCollection();
        services.AddPCBoostPresentation();

        foreach (var key in PageRegistry.Keys)
        {
            var type = PageRegistry.ViewModelTypeFor(key)!;
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
            Assert.True(typeof(INavigationAware).IsAssignableFrom(type), type.Name);
            Assert.NotNull(PageRegistry.TitleKeyFor(key));
        }

        Assert.Equal(ServiceLifetime.Singleton, services.Single(d => d.ServiceType == typeof(ShellViewModel)).Lifetime);
        Assert.Contains(services, d => d.ServiceType == typeof(IValueFormatter));
        Assert.Contains(services, d => d.ServiceType == typeof(IStringResourceSource));
    }

    [Fact]
    public void ValueFormatter_And_Context_ResolveFromContainer()
    {
        var services = new ServiceCollection();
        services.AddPCBoostPresentation();
        services.AddSingleton<ILocalizer>(new TestLocalizer());
        services.AddSingleton<IUiDispatcher, ImmediateDispatcher>();
        services.AddSingleton<INavigationService, FakeNavigationService>();
        services.AddSingleton<IDialogService, FakeDialogService>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ViewModelContext>());
        Assert.Equal("Non disponible", provider.GetRequiredService<IValueFormatter>().NotAvailable);
    }
}

public sealed class PlanPreviewTests
{
    [Fact]
    public void GroupSelectionState_IsTriStateAndDrivesChildren()
    {
        var ui = new TestUi();
        var plan = new PCBoost.Core.Models.Optimization.OptimizationPlan(
            PCBoost.Core.Models.Optimization.SessionType.Manual,
            [
                new PCBoost.Core.Models.Optimization.OptimizationPreview("x", PCBoost.Core.Common.TextRef.Literal("X"), true, null,
                [
                    new PCBoost.Core.Models.Optimization.PlannedChange("a", PCBoost.Core.Common.TextRef.Literal("A"), "t", true, true, PCBoost.Core.Common.RiskLevel.Low, 1024L * 1024 * 1024),
                    new PCBoost.Core.Models.Optimization.PlannedChange("b", PCBoost.Core.Common.TextRef.Literal("B"), "t", false, true, PCBoost.Core.Common.RiskLevel.Low),
                ], PCBoost.Core.Common.RiskLevel.Low, PCBoost.Core.Common.ImpactLevel.Low, true, true, false),
            ],
            new HashSet<string>());
        var preview = new PlanPreviewViewModel(plan, ui.Localizer, ui.Formatter);
        var group = preview.Items.Single();

        Assert.Null(group.SelectionState);
        Assert.Equal(1, preview.SelectedChangeCount);
        Assert.Equal("Espace estimé récupérable : 1\u00a0Go", preview.EstimatedTotalText);
        Assert.True(preview.RequiresElevation);
        Assert.Equal("Autorisation administrateur requise", group.ElevationText);

        group.SelectionState = true;
        Assert.All(group.Changes, c => Assert.True(c.IsSelected));
        Assert.Equal(2, preview.SelectedChangeCount);

        group.Changes[0].IsSelected = false;
        Assert.Null(group.SelectionState);
        Assert.Equal(["b"], preview.BuildPlan(false, false).SelectedChangeIds.ToArray());

        preview.SelectRecommendedCommand.Execute(null);
        Assert.Equal(["a"], preview.BuildPlan(false, false).SelectedChangeIds.ToArray());
        Assert.Null(preview.BuildConfirmation(alwaysConfirm: false));
        Assert.NotNull(preview.BuildConfirmation(alwaysConfirm: true));
    }
}

public sealed class MonitorLeaseTests
{
    private sealed class FakeLifecycle : IAppLifecycle
    {
        public bool IsWindowVisible { get; set; } = true;

        public event EventHandler<bool>? WindowVisibilityChanged;

        public void Raise(bool visible)
        {
            IsWindowVisible = visible;
            WindowVisibilityChanged?.Invoke(this, visible);
        }

        public void ShowMainWindow()
        {
        }

        public void Exit()
        {
        }
    }

    [Fact]
    public void Lease_FollowsWindowVisibility_AndRestoresBackground()
    {
        var monitor = new FakeMonitor();
        var lifecycle = new FakeLifecycle();
        var lease = MonitorLease.Acquire(monitor, monitoringEnabledInSettings: true, lifecycle);
        Assert.Equal(MonitoringMode.Active, monitor.Mode);

        lifecycle.Raise(false);
        Assert.Equal(MonitoringMode.Background, monitor.Mode);
        lifecycle.Raise(true);
        Assert.Equal(MonitoringMode.Active, monitor.Mode);

        lease.Dispose();
        Assert.Equal(MonitoringMode.Background, monitor.Mode);
        lifecycle.Raise(true);
        Assert.Equal(MonitoringMode.Background, monitor.Mode);
    }

    [Fact]
    public void Lease_RespectsUserPause_AndStopsMonitorStartedForPage()
    {
        var paused = new FakeMonitor();
        paused.SetMode(MonitoringMode.Paused);
        using (MonitorLease.Acquire(paused, true))
        {
            Assert.Equal(MonitoringMode.Paused, paused.Mode);
        }

        var stopped = new FakeMonitor();
        stopped.Stop();
        var lease = MonitorLease.Acquire(stopped, monitoringEnabledInSettings: false);
        Assert.True(stopped.IsRunning);
        Assert.Equal(MonitoringMode.Active, stopped.Mode);
        lease.Dispose();
        Assert.False(stopped.IsRunning);
    }
}
