using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Monitoring;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Services;
using PCBoost.Gaming.Metrics;

namespace PCBoost.Gaming.Services;

/// <summary>Noms des métriques comparées par <see cref="BenchmarkService.Compare"/>.</summary>
public static class BenchmarkMetrics
{
    public const string AverageFps = "AverageFps";
    public const string OnePercentLowFps = "OnePercentLowFps";
    public const string PointOnePercentLowFps = "PointOnePercentLowFps";
    public const string AverageFrameTimeMs = "AverageFrameTimeMs";
    public const string P99FrameTimeMs = "P99FrameTimeMs";
    public const string CpuAveragePercent = "CpuAveragePercent";
    public const string CpuMaxPercent = "CpuMaxPercent";
    public const string GpuAveragePercent = "GpuAveragePercent";
    public const string GpuMaxPercent = "GpuMaxPercent";
    public const string RamAveragePercent = "RamAveragePercent";
    public const string DiskActiveAveragePercent = "DiskActiveAveragePercent";
}

/// <summary>
/// Benchmark avant/après (§21) : pendant la durée demandée, moniteur en mode actif, échantillons CPU/GPU/RAM/disque et, si un
/// PID de jeu est fourni, capture des images (autorisation administrateur ponctuelle). Aucun gain n'est calculé sans mesure :
/// une métrique absente d'un côté donne un écart null.
/// </summary>
public sealed class BenchmarkService : IBenchmarkService
{
    private static readonly TimeSpan ProgressStep = TimeSpan.FromMilliseconds(250);

    private readonly IPerformanceMonitor _monitor;
    private readonly IFrameTimeSource _frames;
    private readonly IBenchmarkRepository _repository;
    private readonly IProcessProvider _processes;
    private readonly IGameDetectionService _detection;
    private readonly IClock _clock;
    private readonly GamingOptions _options;
    private readonly ILogger<BenchmarkService> _logger;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public BenchmarkService(
        IPerformanceMonitor monitor,
        IFrameTimeSource frames,
        IBenchmarkRepository repository,
        IProcessProvider processes,
        IGameDetectionService detection,
        IClock clock,
        GamingOptions? options = null,
        ILogger<BenchmarkService>? logger = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _frames = frames ?? throw new ArgumentNullException(nameof(frames));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _processes = processes ?? throw new ArgumentNullException(nameof(processes));
        _detection = detection ?? throw new ArgumentNullException(nameof(detection));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? new GamingOptions();
        _logger = logger ?? NullLogger<BenchmarkService>.Instance;
    }

    /// <summary>Disponibilité de la capture d'images lors de la dernière mesure (null si aucun jeu n'était suivi).</summary>
    public FrameCaptureAvailability? LastFrameCapture { get; private set; }

    /// <summary>Raison de l'échec de la capture d'images lors de la dernière mesure.</summary>
    public TextRef? LastFrameCaptureError { get; private set; }

    public async Task<BenchmarkRun> RunAsync(BenchmarkPhase phase, TimeSpan duration, string? label, int? gameProcessId, Guid? pairedRunId,
        IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var samples = new List<SystemMetricsSample>();
        var sampleLock = new Lock();
        var timestamps = new List<double>();
        var frameLock = new Lock();
        IFrameCaptureSession? capture = null;
        EventHandler<FrameBatch>? frameHandler = null;
        var startedMonitor = false;
        var changedMode = false;
        var previousMode = _monitor.Mode;
        var start = _clock.UtcNow;

        void OnSample(object? sender, SystemMetricsSample sample)
        {
            lock (sampleLock) samples.Add(sample);
        }

        _monitor.SampleAvailable += OnSample;
        try
        {
            if (!_monitor.IsRunning)
            {
                _monitor.Start();
                startedMonitor = true;
            }
            if (_monitor.Mode != MonitoringMode.Active)
            {
                _monitor.SetMode(MonitoringMode.Active);
                changedMode = true;
            }

            LastFrameCapture = null;
            LastFrameCaptureError = null;
            var gameName = gameProcessId is null ? null : await ResolveGameNameAsync(gameProcessId.Value, cancellationToken).ConfigureAwait(false);
            if (gameProcessId is { } pid)
            {
                // Le PID fourni par l'utilisateur vaut demande explicite de mesure des images (l'invite UAC reste la seule autorisation).
                var availability = _frames.GetAvailability();
                if (availability == FrameCaptureAvailability.Available)
                {
                    capture = await _frames.StartAsync(pid, cancellationToken).ConfigureAwait(false);
                    if (capture is null)
                    {
                        availability = FrameCaptureAvailability.RequiresElevation;
                        LastFrameCaptureError = _frames.LastError;
                    }
                    else
                    {
                        frameHandler = (_, batch) =>
                        {
                            if (batch.ProcessId != pid) return;
                            lock (frameLock) timestamps.AddRange(batch.PresentTimestampsMs);
                        };
                        capture.FramesReceived += frameHandler;
                    }
                }
                LastFrameCapture = availability;
            }

            start = _clock.UtcNow;
            var elapsed = TimeSpan.Zero;
            progress?.Report(0);
            while (elapsed < duration)
            {
                var step = duration - elapsed < ProgressStep ? duration - elapsed : ProgressStep;
                await _options.Delay(step, cancellationToken).ConfigureAwait(false);
                elapsed += step;
                progress?.Report(Math.Min(1d, elapsed / duration));
            }

            await StopCaptureAsync(capture, frameHandler).ConfigureAwait(false);
            capture = null;

            List<SystemMetricsSample> collected;
            lock (sampleLock) collected = samples.ToList();
            if (collected.Count == 0)
            {
                // Aucun événement reçu (moniteur déjà échantillonné par ailleurs) : historique de la fenêtre mesurée.
                collected = _monitor.GetHistory(duration + TimeSpan.FromSeconds(1)).Where(s => s.Timestamp >= start).ToList();
            }

            FrameStats frames;
            lock (frameLock) frames = FrameMetricsCalculator.Calculate(timestamps);

            var run = BuildRun(phase, duration, label, gameName, pairedRunId, collected, frames);
            await _repository.SaveAsync(run, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Benchmark {Phase} enregistré : {Samples} échantillon(s), {Frames} image(s)", phase, run.SampleCount, run.Frames.FrameCount);
            return run;
        }
        finally
        {
            _monitor.SampleAvailable -= OnSample;
            await StopCaptureAsync(capture, frameHandler).ConfigureAwait(false);
            try
            {
                if (changedMode && _monitor.Mode == MonitoringMode.Active) _monitor.SetMode(previousMode);
                if (startedMonitor) _monitor.Stop();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _logger.LogWarning(ex, "Moniteur non remis dans son état précédent après le benchmark");
            }
            _runGate.Release();
        }
    }

    public Task<IReadOnlyList<BenchmarkRun>> GetHistoryAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _repository.GetRecentAsync(Math.Max(1, limit), cancellationToken);

    /// <summary>Écarts mesurés « après − avant ». Null si l'une des deux mesures manque (aucun gain supposé).</summary>
    public BenchmarkComparison Compare(BenchmarkRun before, BenchmarkRun after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var b = before.Frames.HasData ? before.Frames : FrameStats.Empty;
        var a = after.Frames.HasData ? after.Frames : FrameStats.Empty;
        double? Load(BenchmarkRun run, double value) => run.SampleCount > 0 ? value : null;

        var deltas = new List<BenchmarkDelta>
        {
            Delta(BenchmarkMetrics.AverageFps, b.AverageFps, a.AverageFps, higherIsBetter: true),
            Delta(BenchmarkMetrics.OnePercentLowFps, b.OnePercentLowFps, a.OnePercentLowFps, higherIsBetter: true),
            Delta(BenchmarkMetrics.PointOnePercentLowFps, b.PointOnePercentLowFps, a.PointOnePercentLowFps, higherIsBetter: true),
            Delta(BenchmarkMetrics.AverageFrameTimeMs, b.AverageFrameTimeMs, a.AverageFrameTimeMs, higherIsBetter: false),
            Delta(BenchmarkMetrics.P99FrameTimeMs, b.P99FrameTimeMs, a.P99FrameTimeMs, higherIsBetter: false),
            Delta(BenchmarkMetrics.CpuAveragePercent, Load(before, before.CpuAveragePercent), Load(after, after.CpuAveragePercent), higherIsBetter: false),
            Delta(BenchmarkMetrics.CpuMaxPercent, Load(before, before.CpuMaxPercent), Load(after, after.CpuMaxPercent), higherIsBetter: false),
            Delta(BenchmarkMetrics.GpuAveragePercent, before.GpuAveragePercent, after.GpuAveragePercent, higherIsBetter: false),
            Delta(BenchmarkMetrics.GpuMaxPercent, before.GpuMaxPercent, after.GpuMaxPercent, higherIsBetter: false),
            Delta(BenchmarkMetrics.RamAveragePercent, Load(before, before.RamAveragePercent), Load(after, after.RamAveragePercent), higherIsBetter: false),
            Delta(BenchmarkMetrics.DiskActiveAveragePercent, before.DiskActiveAveragePercent, after.DiskActiveAveragePercent, higherIsBetter: false),
        };
        return new BenchmarkComparison(before, after, deltas);
    }

    private static BenchmarkDelta Delta(string metric, double? before, double? after, bool higherIsBetter)
        => new(metric, before, after, before.HasValue && after.HasValue ? after.Value - before.Value : null, higherIsBetter);

    private BenchmarkRun BuildRun(BenchmarkPhase phase, TimeSpan duration, string? label, string? gameName, Guid? pairedRunId,
        IReadOnlyList<SystemMetricsSample> samples, FrameStats frames)
    {
        var gpu = samples.Where(s => s.GpuPercent.HasValue).Select(s => s.GpuPercent!.Value).ToList();
        var disk = samples.Where(s => s.DiskActivePercent.HasValue).Select(s => s.DiskActivePercent!.Value).ToList();
        return new BenchmarkRun
        {
            Id = Guid.NewGuid(),
            Timestamp = _clock.UtcNow,
            Phase = phase,
            Duration = duration,
            Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim(),
            GameName = gameName,
            PairedRunId = pairedRunId,
            // SampleCount = 0 signale l'absence de mesure : ces valeurs ne sont alors pas comparées.
            CpuAveragePercent = samples.Count > 0 ? samples.Average(s => s.CpuPercent) : 0,
            CpuMaxPercent = samples.Count > 0 ? samples.Max(s => s.CpuPercent) : 0,
            GpuAveragePercent = gpu.Count > 0 ? gpu.Average() : null,
            GpuMaxPercent = gpu.Count > 0 ? gpu.Max() : null,
            RamAveragePercent = samples.Count > 0 ? samples.Average(s => s.MemoryUsedPercent) : 0,
            DiskActiveAveragePercent = disk.Count > 0 ? disk.Average() : null,
            Frames = frames,
            SampleCount = samples.Count,
        };
    }

    private async Task<string?> ResolveGameNameAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            var running = await _detection.DetectRunningGameAsync(cancellationToken).ConfigureAwait(false);
            if (running?.ProcessId == processId) return running.Game.Name;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Détection du jeu indisponible pour le benchmark");
        }
        var name = _processes.GetProcess(processId)?.Name;
        return name is null ? null : name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private async Task StopCaptureAsync(IFrameCaptureSession? capture, EventHandler<FrameBatch>? handler)
    {
        if (capture is null) return;
        if (handler is not null) capture.FramesReceived -= handler;
        try
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Arrêt de la capture d'images du benchmark incomplet");
        }
    }
}
