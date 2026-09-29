using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Services;

namespace PCBoost.TestUtilities;

/// <summary>Service de santé matérielle scriptable : renvoie <see cref="Report"/> et compte les appels.</summary>
public sealed class FakeHardwareHealthService : IHardwareHealthService
{
    public HardwareHealthReport Report { get; set; } = HardwareHealthReport.Empty(DateTimeOffset.UnixEpoch);

    public OperationResult ReadResult { get; set; } = OperationResult.Ok();

    /// <summary>Rapport publié après une lecture réussie des compteurs (simule la mise à jour).</summary>
    public HardwareHealthReport? ReportAfterRead { get; set; }

    public int RefreshCount { get; private set; }

    public int ReadCount { get; private set; }

    public bool Started { get; private set; }

    public HardwareHealthReport? Latest { get; set; }

    public event EventHandler<HardwareHealthReport>? ReportUpdated;

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public Task<HardwareHealthReport> RefreshAsync(CancellationToken cancellationToken = default)
    {
        RefreshCount++;
        Latest = Report;
        ReportUpdated?.Invoke(this, Report);
        return Task.FromResult(Report);
    }

    public Task<OperationResult> ReadDiskReliabilityAsync(CancellationToken cancellationToken = default)
    {
        ReadCount++;
        if (ReadResult.Success && ReportAfterRead is { } after)
        {
            Report = after;
            Latest = after;
            ReportUpdated?.Invoke(this, after);
        }
        return Task.FromResult(ReadResult);
    }

    public void Raise(HardwareHealthReport report)
    {
        Latest = report;
        ReportUpdated?.Invoke(this, report);
    }

    public void Dispose()
    {
    }
}

public sealed class FakeThermalThrottlingDetector : IThermalThrottlingDetector
{
    public int EpisodeCount { get; set; }

    public ThrottlingEpisode? LastEpisode { get; set; }

    public bool Started { get; private set; }

    public event EventHandler<ThrottlingEpisode>? EpisodeDetected;

    public void Start() => Started = true;

    public void Stop() => Started = false;

    public void Raise(ThrottlingEpisode episode)
    {
        EpisodeCount++;
        LastEpisode = episode;
        EpisodeDetected?.Invoke(this, episode);
    }

    public void Dispose()
    {
    }
}

public sealed class FakeBootTimeService : IBootTimeService
{
    public BootTimeReport Report { get; set; } = new([], null, null, null);

    public OperationResult ReadResult { get; set; } = OperationResult.Ok();

    public BootTimeReport? ReportAfterRead { get; set; }

    public int ReadCount { get; private set; }

    public Task<BootTimeReport> GetReportAsync(CancellationToken cancellationToken = default) => Task.FromResult(Report);

    public Task<OperationResult> ReadMeasurementsAsync(CancellationToken cancellationToken = default)
    {
        ReadCount++;
        if (ReadResult.Success && ReportAfterRead is { } after) Report = after;
        return Task.FromResult(ReadResult);
    }
}
