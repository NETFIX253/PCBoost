using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.SystemInfo;

namespace PCBoost.TestUtilities;

public sealed class FakeSystemInfoProvider : ISystemInfoProvider
{
    public OsInfo Os { get; set; } = new("Windows 11 Pro", "24H2", 26100, 1000, ProcessorArchitecture.X64, true, true, TimeSpan.FromHours(20), false);
    public CpuInfo Cpu { get; set; } = new("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz", "GenuineIntel", 4, 8, 1600, 3400, 6144);
    public MemoryInfo Memory { get; set; } = new(8L * ByteSize.GiB, 3L * ByteSize.GiB, 9L * ByteSize.GiB, 16L * ByteSize.GiB, 2400, 2);
    public List<GpuInfo> Gpus { get; set; } = [new("Intel(R) UHD Graphics 620", GpuVendor.Intel, 128 * ByteSize.MiB, 4L * ByteSize.GiB, "31.0.101", false, true)];
    public List<StorageDrive> Drives { get; set; } = [new(@"C:\", "Windows", "NTFS", 256L * ByteSize.GiB, 60L * ByteSize.GiB, true, false, StorageMediaType.Ssd, StorageBusType.Sata, "SSD 256")];
    public PowerStatus Power { get; set; } = new(PowerSource.AC, 100, true);
    public int? InstalledPrograms { get; set; } = 85;

    public OsInfo GetOsInfo() => Os;
    public CpuInfo GetCpuInfo() => Cpu;
    public MemoryInfo GetMemoryInfo() => Memory;
    public IReadOnlyList<GpuInfo> GetGpus() => Gpus;
    public IReadOnlyList<StorageDrive> GetDrives() => Drives;
    public PowerStatus GetPowerStatus() => Power;
    public int? GetInstalledProgramCount() => InstalledPrograms;
}

/// <summary>Métriques scriptées : renvoie les échantillons en file, puis répète le dernier.</summary>
public sealed class FakeSystemMetricsProvider : ISystemMetricsProvider
{
    private readonly Queue<SystemMetricsSample> _queue = new();
    private SystemMetricsSample _last;
    private readonly IClock _clock;

    public FakeSystemMetricsProvider(IClock? clock = null)
    {
        _clock = clock ?? new FakeClock();
        _last = Create(20, 50);
    }

    public int SampleCalls { get; private set; }

    public SystemMetricsSample Create(double cpu, double ramPercent, double? disk = 5, double? gpu = 3, long totalRam = 8L * ByteSize.GiB)
        => new(_clock.UtcNow, cpu, ramPercent, (long)(totalRam * ramPercent / 100), totalRam, disk, 0, 0, gpu, null, 0, 0, 150);

    public void Enqueue(params SystemMetricsSample[] samples)
    {
        foreach (var s in samples) _queue.Enqueue(s);
    }

    public SystemMetricsSample Sample()
    {
        SampleCalls++;
        if (_queue.Count > 0) _last = _queue.Dequeue();
        return _last with { Timestamp = _clock.UtcNow };
    }

    public void Dispose() { }
}

public sealed class FakeHardwareProvider : IHardwareProvider
{
    public TemperatureReadings Readings { get; set; } = TemperatureReadings.None;
    public TemperatureReadings GetTemperatures() => Readings;
}

public sealed class FakePowerProvider : IPowerProvider
{
    public List<PowerScheme> Schemes { get; } =
    [
        new(PowerScheme.Balanced, "Utilisation normale"),
        new(PowerScheme.HighPerformance, "Performances élevées"),
        new(PowerScheme.PowerSaver, "Économie d'énergie"),
    ];

    public Guid ActiveId { get; set; } = PowerScheme.Balanced;
    public int SetCalls { get; private set; }

    public PowerScheme? GetActiveScheme() => Schemes.FirstOrDefault(s => s.Id == ActiveId);
    public IReadOnlyList<PowerScheme> GetSchemes() => Schemes;

    public OperationResult SetActiveScheme(Guid schemeId)
    {
        if (Schemes.All(s => s.Id != schemeId)) return OperationResult.Fail(OperationErrorKind.NotFound);
        ActiveId = schemeId;
        SetCalls++;
        return OperationResult.Ok();
    }
}

public sealed class FakeVisualEffectsProvider : IVisualEffectsProvider
{
    public VisualEffectsState State { get; set; } = new(true, true, true, true, true, true, true, true, true);
    public VisualEffectsState? GetState() => State;
    public OperationResult SetState(VisualEffectsState state) { State = state; return OperationResult.Ok(); }
}

public sealed class FakeRecycleBinProvider : IRecycleBinProvider
{
    public long Bytes { get; set; } = 500 * ByteSize.MiB;
    public long Items { get; set; } = 42;
    public bool Emptied { get; private set; }
    public OperationResult<(long Bytes, long Items)> Query() => OperationResult<(long, long)>.Ok((Bytes, Items));
    public OperationResult Empty() { Emptied = true; Bytes = 0; Items = 0; return OperationResult.Ok(); }
}

public sealed class FakeScheduledTaskProvider : IScheduledTaskProvider
{
    public List<ScheduledTaskInfo> Tasks { get; } = [];
    public IReadOnlyList<ScheduledTaskInfo> GetLogonTasks() => Tasks;
    public bool? IsEnabled(string taskPath) => Tasks.FirstOrDefault(t => t.Path == taskPath)?.Enabled;
    public OperationResult SetEnabled(string taskPath, bool enabled)
    {
        var i = Tasks.FindIndex(t => t.Path == taskPath);
        if (i < 0) return OperationResult.Fail(OperationErrorKind.NotFound);
        Tasks[i] = Tasks[i] with { Enabled = enabled };
        return OperationResult.Ok();
    }
}

public sealed class FakeElevationService : IElevationService
{
    public bool IsElevated { get; set; }
    public bool UserCancels { get; set; }
    public List<ElevatedRequest> Requests { get; } = [];
    public Func<ElevatedRequest, ElevatedResponse>? Handler { get; set; }

    public Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        if (UserCancels)
            return Task.FromResult(new ElevatedResponse(OperationResult.Fail(OperationErrorKind.ElevationCancelled), new Dictionary<string, string>()));
        return Task.FromResult(Handler?.Invoke(request) ?? new ElevatedResponse(OperationResult.Ok(), new Dictionary<string, string>()));
    }
}

public sealed class FakeFileMetadataProvider : IFileMetadataProvider
{
    public Dictionary<string, Core.Models.Processes.FileVersionMetadata> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Core.Models.Processes.FileVersionMetadata? GetVersionInfo(string path) => Files.GetValueOrDefault(path);
}

public sealed class FakeSignatureVerifier : ISignatureVerifier
{
    public Dictionary<string, Core.Models.Processes.SignatureInfo> Signatures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Core.Models.Processes.SignatureInfo Verify(string path)
        => Signatures.GetValueOrDefault(path) ?? new Core.Models.Processes.SignatureInfo(Core.Models.Processes.SignatureStatus.Unsigned, null);
}

public sealed class FakeShortcutResolver : IShortcutResolver
{
    public Dictionary<string, ShortcutTarget> Targets { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ShortcutTarget? Resolve(string shortcutPath) => Targets.GetValueOrDefault(shortcutPath);
}

public sealed class FakeHardwareHealthProvider : IHardwareHealthProvider
{
    public OperationResult<IReadOnlyList<Core.Models.Health.DiskHealthInfo>> Disks { get; set; }
        = OperationResult<IReadOnlyList<Core.Models.Health.DiskHealthInfo>>.Ok([]);
    public OperationResult<IReadOnlyList<Core.Models.Health.BatteryInfo>> Batteries { get; set; }
        = OperationResult<IReadOnlyList<Core.Models.Health.BatteryInfo>>.Ok([]);
    public OperationResult<IReadOnlyList<Core.Models.Health.DeviceProblem>> DeviceProblems { get; set; }
        = OperationResult<IReadOnlyList<Core.Models.Health.DeviceProblem>>.Ok([]);
    public List<DateTimeOffset> FirmwareLimitEvents { get; } = [];
    public List<Core.Models.Health.BootSession> Boots { get; } = [];

    public OperationResult<IReadOnlyList<Core.Models.Health.DiskHealthInfo>> GetDisks() => Disks;
    public OperationResult<IReadOnlyList<Core.Models.Health.BatteryInfo>> GetBatteries() => Batteries;
    public OperationResult<IReadOnlyList<Core.Models.Health.DeviceProblem>> GetDeviceProblems() => DeviceProblems;
    public IReadOnlyList<DateTimeOffset> GetFirmwareLimitEvents(DateTimeOffset since) => FirmwareLimitEvents.Where(t => t >= since).ToList();
    public IReadOnlyList<Core.Models.Health.BootSession> GetRecentBoots(int max) => Boots.Take(max).ToList();
}

/// <summary>Lanceur de désinstallation scriptable : enregistre les commandes et exécute un effet (ex. retirer la clé).</summary>
public sealed class FakeUninstallerLauncher : IUninstallerLauncher
{
    public List<Core.Models.Programs.UninstallCommand> Commands { get; } = [];

    public OperationResult<int> Result { get; set; } = OperationResult<int>.Ok(0);

    public Action<Core.Models.Programs.UninstallCommand>? OnRun { get; set; }

    public Task<OperationResult<int>> RunAsync(Core.Models.Programs.UninstallCommand command, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        if (Result.Success) OnRun?.Invoke(command);
        return Task.FromResult(Result);
    }
}

/// <summary>Informations d'application fixes pour les tests.</summary>
public sealed class TestAppInfo : Core.Services.IAppInfo
{
    public string ProductName { get; set; } = "PCBoost";
    public Version Version { get; set; } = new(1, 1, 0);
    public string DotNetVersion => "10.0";
    public string WindowsAppSdkVersion => "2.3";
    public string DataDirectory { get; set; } = @"C:\Users\Test\AppData\Local\PCBoost";
    public string LogDirectory { get; set; } = @"C:\Users\Test\AppData\Local\PCBoost\logs";
}
