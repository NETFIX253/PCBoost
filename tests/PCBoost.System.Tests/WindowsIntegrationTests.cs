using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform;
using PCBoost.TestUtilities;

namespace PCBoost.System.Tests;

/// <summary>
/// Tests d'intégration en LECTURE SEULE sur une vraie machine Windows (ignorés ailleurs).
/// Aucun de ces tests ne modifie le système.
/// </summary>
public sealed class WindowsIntegrationTests
{
    [WindowsFact]
    public void Os_BuildIsSupported()
    {
        var os = new SystemInfoProvider().GetOsInfo();

        Assert.True(os.BuildNumber > 0);
        Assert.True(os.BuildNumber >= OsInfo.MinimumSupportedBuild);
        Assert.StartsWith("Windows", os.ProductName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(os.BuildNumber >= 22000, os.IsWindows11);
        Assert.True(os.Uptime > TimeSpan.Zero);
    }

    [WindowsFact]
    public void Cpu_MemoryAndDrives_AreRead()
    {
        var info = new SystemInfoProvider();
        var cpu = info.GetCpuInfo();
        var memory = info.GetMemoryInfo();
        var drives = info.GetDrives();

        Assert.False(string.IsNullOrWhiteSpace(cpu.Name));
        Assert.True(cpu.LogicalProcessors >= 1);
        Assert.True(cpu.PhysicalCores >= 1 && cpu.PhysicalCores <= cpu.LogicalProcessors);
        Assert.True(memory.TotalBytes > 0);
        Assert.InRange(memory.AvailableBytes, 0, memory.TotalBytes);
        Assert.True(memory.CommitLimitBytes >= memory.CommitTotalBytes);
        Assert.Contains(drives, d => d.IsSystemDrive);
        Assert.All(drives, d => Assert.InRange(d.FreeBytes, 0, d.TotalBytes));
    }

    [WindowsFact]
    public void Gpus_AreEnumeratedWithoutSoftwareAdapterWhenHardwareExists()
    {
        var gpus = new SystemInfoProvider().GetGpus();

        if (gpus.Any(g => !g.IsSoftwareAdapter))
            Assert.DoesNotContain(gpus, g => g.IsSoftwareAdapter);
        Assert.All(gpus, g => Assert.False(string.IsNullOrWhiteSpace(g.Name)));
    }

    [WindowsFact]
    public void PowerStatus_AndInstalledPrograms_AreConsistent()
    {
        var info = new SystemInfoProvider();
        var power = info.GetPowerStatus();

        if (power.BatteryPercent is { } percent) Assert.InRange(percent, 0, 100);
        if (!power.HasBattery) Assert.Null(power.BatteryPercent);
        Assert.True(info.GetInstalledProgramCount() is null or >= 0);
    }

    [WindowsFact]
    public void Metrics_TwoSuccessiveSamples_AreWithinBounds()
    {
        using var metrics = new SystemMetricsProvider(new SystemClock());
        var first = metrics.Sample();
        Thread.Sleep(1100);
        var second = metrics.Sample();

        foreach (var sample in new[] { first, second })
        {
            Assert.InRange(sample.CpuPercent, 0, 100);
            Assert.InRange(sample.MemoryUsedPercent, 0, 100);
            Assert.True(sample.MemoryTotalBytes > 0);
            Assert.InRange(sample.MemoryUsedBytes, 0, sample.MemoryTotalBytes);
            Assert.True(sample.ProcessCount > 0);
            if (sample.DiskActivePercent is { } disk) Assert.InRange(disk, 0, 100);
            if (sample.GpuPercent is { } gpu) Assert.InRange(gpu, 0, 100);
            if (sample.DiskReadBytesPerSec is { } read) Assert.True(read >= 0);
            if (sample.NetworkReceiveBytesPerSec is { } received) Assert.True(received >= 0);
        }
        Assert.True(second.Timestamp >= first.Timestamp);
        // Premier appel : les débits ne sont pas encore mesurables.
        Assert.Null(first.DiskReadBytesPerSec);
        Assert.Null(first.GpuPercent);
    }

    [WindowsFact]
    public void Metrics_AfterDispose_Throws()
    {
        var metrics = new SystemMetricsProvider(new SystemClock());
        metrics.Dispose();
        Assert.Throws<ObjectDisposedException>(() => metrics.Sample());
    }

    [WindowsFact]
    public void Processes_ContainCurrentProcessWithPath()
    {
        var provider = new ProcessProvider();
        var processes = provider.GetProcesses();
        var current = Assert.Single(processes, p => p.ProcessId == Environment.ProcessId);

        Assert.False(string.IsNullOrWhiteSpace(current.ExecutablePath));
        Assert.True(current.IsCurrentUser);
        Assert.True(current.WorkingSetBytes > 0);
        Assert.NotNull(current.StartTime);
        Assert.Equal(provider.CurrentSessionId, current.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(provider.GetExecutablePath(Environment.ProcessId)));
        Assert.True(provider.IsRunning(Environment.ProcessId, current.StartTime));
        Assert.False(provider.IsRunning(Environment.ProcessId, current.StartTime!.Value.AddMinutes(-5)));
        Assert.NotNull(provider.GetProcess(Environment.ProcessId));
    }

    [WindowsFact]
    public void ProcessIdentities_AreLightAndContainCurrentProcess()
    {
        var identities = new ProcessProvider().GetProcessIdentities();

        var current = Assert.Single(identities, p => p.ProcessId == Environment.ProcessId);
        Assert.EndsWith(".exe", current.Name, StringComparison.OrdinalIgnoreCase);
        Assert.True(identities.Count > 10);
    }

    [WindowsFact]
    public void ProcessControl_ReadsPriorityOfCurrentProcess()
    {
        var control = new ProcessControl();
        var priority = control.GetPriority(Environment.ProcessId);

        Assert.True(priority.Success);
        Assert.NotEqual(ProcessPriority.Unknown, priority.Value);
        Assert.Equal(OperationErrorKind.NotFound, control.GetPriority(int.MaxValue - 3).Error);
    }

    [WindowsFact]
    public void Registry_CurrentVersionIsReadable()
    {
        var registry = new RegistryProvider();
        var location = new RegistryLocation(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", RegistryViewKind.Registry64);

        Assert.True(registry.KeyExists(location));
        var build = registry.GetValue(location, "CurrentBuildNumber");
        Assert.NotNull(build);
        Assert.Equal(RegistryValueType.String, build.Type);
        Assert.Contains("CurrentBuildNumber", registry.GetValueNames(location));
        Assert.False(registry.CanWrite(location) && !new ElevationService().IsElevated);
        Assert.True(registry.CanWrite(new RegistryLocation(RegistryHiveKind.CurrentUser, @"Software\PCBoostReadOnlyProbe\Missing")));
    }

    [WindowsFact]
    public void KnownFolders_Exist()
    {
        var fs = new FileSystemProvider();
        foreach (var folder in new[]
                 {
                     KnownFolder.UserProfile, KnownFolder.LocalAppData, KnownFolder.RoamingAppData, KnownFolder.ProgramData,
                     KnownFolder.UserTemp, KnownFolder.WindowsDirectory, KnownFolder.WindowsTemp, KnownFolder.ProgramFiles,
                     KnownFolder.Downloads, KnownFolder.StartupUser, KnownFolder.StartupCommon,
                 })
        {
            var path = fs.GetKnownFolder(folder);
            Assert.False(string.IsNullOrWhiteSpace(path), folder.ToString());
            Assert.True(fs.DirectoryExists(path!), $"{folder}: {path}");
        }
        Assert.NotEmpty(fs.GetFixedDriveRoots());
    }

    [WindowsFact]
    public void Signature_ExplorerIsSignedByMicrosoft()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var verifier = new SignatureVerifier();

        var signature = verifier.Verify(explorer);

        Assert.Equal(SignatureStatus.Signed, signature.Status);
        Assert.True(signature.IsMicrosoft, signature.Signer);
        Assert.Same(signature, verifier.Verify(explorer)); // Cache par chemin et date.
    }

    [WindowsFact]
    public void Signature_CatalogSignedSystemFileIsSigned()
    {
        // notepad.exe et la plupart des binaires de System32 sont signés par catalogue.
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        if (!File.Exists(notepad)) return;

        Assert.Equal(SignatureStatus.Signed, new SignatureVerifier().Verify(notepad).Status);
    }

    [WindowsFact]
    public void Signature_UnsignedFileIsReportedUnsigned()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcboost-unsigned-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(path, "not signed");
        try
        {
            Assert.Equal(SignatureStatus.Unsigned, new SignatureVerifier().Verify(path).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [WindowsFact]
    public void FileMetadata_ExplorerHasCompany()
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var metadata = new FileMetadataProvider().GetVersionInfo(explorer);

        Assert.NotNull(metadata);
        Assert.Contains("Microsoft", metadata.CompanyName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsFact]
    public void PowerSchemes_AreEnumeratedWithAnActiveOne()
    {
        var power = new PowerProvider();
        var schemes = power.GetSchemes();
        var active = power.GetActiveScheme();

        Assert.NotEmpty(schemes);
        Assert.NotNull(active);
        Assert.Contains(schemes, s => s.Id == active.Id);
        Assert.All(schemes, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [WindowsFact]
    public void VisualEffects_AreReadable()
        => Assert.NotNull(new VisualEffectsProvider().GetState());

    [WindowsFact]
    public void Temperatures_AreEitherMeasuredOrExplicitlyUnavailable()
    {
        using var hardware = new HardwareProvider(new SystemInfoProvider());
        var readings = hardware.GetTemperatures();

        foreach (var reading in new[] { readings.Cpu, readings.Gpu, readings.Storage })
        {
            if (reading.HasValue)
            {
                Assert.InRange(reading.Value!.Value, 0.1, 120);
                Assert.False(string.IsNullOrWhiteSpace(reading.Source));
            }
            else
            {
                Assert.Null(reading.Value);
                Assert.NotEqual(Availability.Available, reading.Availability);
            }
        }
    }

    [WindowsFact]
    public void RecycleBin_IsQueryable()
    {
        var result = new RecycleBinProvider().Query();

        Assert.True(result.Success, result.TechnicalDetail);
        Assert.True(result.Value.Bytes >= 0 && result.Value.Items >= 0);
    }

    [WindowsFact]
    public void ScheduledTasks_LogonTasksAreListed()
    {
        var tasks = new ScheduledTaskProvider().GetLogonTasks();

        Assert.All(tasks, t => Assert.StartsWith("\\", t.Path));
        Assert.All(tasks.Where(t => t.Path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)), t => Assert.True(t.IsMicrosoft));
    }

    [WindowsFact]
    public void ForegroundWindow_QueriesDoNotThrow()
    {
        var provider = new ForegroundWindowProvider();
        var pid = provider.GetForegroundProcessId();
        _ = provider.IsForegroundFullscreen();
        Assert.True(pid is null or > 0);
    }

    [WindowsFact]
    public void Shortcut_StartMenuShortcutResolves()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
        var shortcut = Directory.Exists(programs)
            ? Directory.EnumerateFiles(programs, "*.lnk", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (shortcut is null) return;

        var target = new ShortcutResolver().Resolve(shortcut);
        Assert.True(target is null || !string.IsNullOrWhiteSpace(target.TargetPath));
    }

    [WindowsFact]
    public void FrameCapture_AvailabilityIsConsistent()
    {
        var availability = new FrameTimeSource().GetAvailability();
        var elevatorPresent = File.Exists(Path.Combine(AppContext.BaseDirectory, "PCBoost.Elevator.exe"));

        Assert.Equal(elevatorPresent ? FrameCaptureAvailability.Available : FrameCaptureAvailability.NotSupported, availability);
    }

    [WindowsFact]
    public void Container_ResolvesEveryService()
    {
        var services = new ServiceCollection();
        services.AddPCBoostPlatform();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<IProcessProvider>());
        Assert.NotNull(provider.GetRequiredService<ISystemMetricsProvider>());
        Assert.NotNull(provider.GetRequiredService<IHardwareProvider>());
        Assert.NotNull(provider.GetRequiredService<IAutoStartRegistration>());
        Assert.NotNull(provider.GetRequiredService<IFrameTimeSource>());
    }
}
