using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Platform.Elevation;
using PCBoost.Platform.Gaming;

namespace PCBoost.Platform;

/// <summary>
/// Horodatages de présentation d'images sans injection (§20) : PCBoost.Elevator ouvre une session ETW temps réel
/// (DXGI / D3D9) filtrée sur le PID du jeu et diffuse les horodatages par un canal nommé créé ici.
/// La capture demande une autorisation administrateur à chaque démarrage.
/// </summary>
public sealed class FrameTimeSource : IFrameTimeSource
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<FrameTimeSource> _logger;

    public FrameTimeSource(ILogger<FrameTimeSource>? logger = null)
    {
        _logger = logger ?? NullLogger<FrameTimeSource>.Instance;
    }

    /// <summary>Raison du dernier échec de <see cref="StartAsync"/> (null si le dernier démarrage a réussi ou a été annulé).</summary>
    public TextRef? LastError { get; private set; }

    public FrameCaptureAvailability GetAvailability()
        => ElevatorLauncher.IsAvailable ? FrameCaptureAvailability.Available : FrameCaptureAvailability.NotSupported;

    public async Task<IFrameCaptureSession?> StartAsync(int processId, CancellationToken cancellationToken = default)
    {
        LastError = null;
        if (!ElevatorLauncher.IsAvailable)
        {
            LastError = TextRef.Of("Sys_FrameCaptureUnavailable");
            return null;
        }

        var pipeName = ElevatedRequestValidator.NewFramePipeName();
        var request = new ElevatedRequest(ElevatedOperations.FrameCapture, new Dictionary<string, string>
        {
            ["pid"] = processId.ToString(CultureInfo.InvariantCulture),
            ["pipe"] = pipeName,
            ["parentPid"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
        });
        var validation = ElevatedRequestValidator.Validate(request);
        if (!validation.Success)
        {
            LastError = validation.Message ?? TextRef.Of("Sys_ElevatedInvalidParameters");
            return null;
        }

        NamedPipeServerStream server;
        string resultPath;
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            resultPath = ElevatorLauncher.PrepareResultPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Canal de capture d'images impossible à créer");
            LastError = TextRef.Of("Sys_FrameCaptureUnavailable");
            return null;
        }

        // ShellExecuteEx (« runas ») bloque pendant l'invite UAC : jamais sur le fil appelant.
        var started = await Task.Run(() => ElevatorLauncher.Start(request, resultPath), CancellationToken.None).ConfigureAwait(false);
        if (!started.Success || started.Value is null)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            LastError = started.Error == OperationErrorKind.ElevationCancelled
                ? TextRef.Of("Sys_FrameCaptureCancelled")
                : started.Message ?? TextRef.Of("Sys_FrameCaptureUnavailable");
            _logger.LogInformation("Capture d'images non démarrée : {Error}", started.Error);
            return null;
        }

        var elevator = started.Value;
        using var timeout = new CancellationTokenSource(ConnectionTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var connect = server.WaitForConnectionAsync(linked.Token);
        var exited = elevator.WaitForExitAsync(linked.Token);
        await Task.WhenAny(connect, exited).ConfigureAwait(false);

        if (connect.IsCompletedSuccessfully)
        {
            await linked.CancelAsync().ConfigureAwait(false); // Libère l'attente de fin de processus.
            _logger.LogInformation("Capture d'images démarrée pour le processus {ProcessId}", processId);
            return new FrameCaptureSession(processId, server, elevator, resultPath, _logger);
        }

        await linked.CancelAsync().ConfigureAwait(false);
        await ObserveAsync(connect).ConfigureAwait(false);
        await ObserveAsync(exited).ConfigureAwait(false);
        await server.DisposeAsync().ConfigureAwait(false);

        if (elevator.HasExited)
        {
            var response = ElevatorLauncher.ReadAndDeleteResult(resultPath);
            LastError = response?.Outcome.Message ?? TextRef.Of("Sys_FrameCaptureUnavailable");
            _logger.LogWarning("Capture d'images : l'assistant s'est arrêté avant la connexion ({Error})", response?.Outcome.TechnicalDetail);
        }
        else if (cancellationToken.IsCancellationRequested)
        {
            LastError = null;
        }
        else
        {
            LastError = TextRef.Of("Sys_FrameCaptureTimeout");
            _logger.LogWarning("Capture d'images : pas de connexion après {Timeout} s", ConnectionTimeout.TotalSeconds);
        }
        elevator.Dispose();
        return null;
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Attente interrompue volontairement.
        }
    }
}

/// <summary>Session de capture : lit les enregistrements du canal et publie des lots toutes les ~250 ms.</summary>
internal sealed class FrameCaptureSession : IFrameCaptureSession
{
    private static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(250);

    private readonly NamedPipeServerStream _pipe;
    private readonly Process _elevator;
    private readonly string _resultPath;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();
    private readonly Task _reader;
    private readonly Task _publisher;
    private List<double> _pending = new(512);
    private volatile bool _active = true;
    private volatile bool _disposing;
    private int _disposed;

    public FrameCaptureSession(int processId, NamedPipeServerStream pipe, Process elevator, string resultPath, ILogger logger)
    {
        ProcessId = processId;
        _pipe = pipe;
        _elevator = elevator;
        _resultPath = resultPath;
        _logger = logger;
        _reader = Task.Run(ReadLoopAsync);
        _publisher = Task.Run(PublishLoopAsync);
    }

    public int ProcessId { get; }

    public bool IsActive => _active;

    public TextRef? Error { get; private set; }

    public event EventHandler<FrameBatch>? FramesReceived;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _disposing = true;
        await _cts.CancelAsync().ConfigureAwait(false);
        // Fermer le canal rompt la connexion : l'Elevator arrête sa session ETW et se termine.
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_reader, _publisher).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }

        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _elevator.WaitForExitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("L'assistant de capture ne s'est pas encore arrêté");
        }
        ElevatorLauncher.ReadAndDeleteResult(_resultPath);
        _elevator.Dispose();
        _cts.Dispose();
        _active = false;
    }

    private async Task ReadLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        var carry = 0;
        var decoded = new List<double>(1024);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var read = await _pipe.ReadAsync(buffer.AsMemory(carry), _cts.Token).ConfigureAwait(false);
                if (read == 0) break; // Canal fermé par l'Elevator.
                var available = carry + read;
                decoded.Clear();
                var consumed = FrameRecordCodec.Decode(buffer.AsSpan(0, available), ProcessId, decoded);
                if (decoded.Count > 0)
                {
                    lock (_lock) _pending.AddRange(decoded);
                }
                carry = available - consumed;
                if (carry > 0) Buffer.BlockCopy(buffer, consumed, buffer, 0, carry);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Arrêt demandé ou canal rompu.
        }
        finally
        {
            _active = false;
            if (!_disposing)
            {
                Error = TextRef.Of("Sys_FrameCaptureStopped");
                _logger.LogInformation("Capture d'images arrêtée pour le processus {ProcessId}", ProcessId);
            }
        }
    }

    private async Task PublishLoopAsync()
    {
        using var timer = new PeriodicTimer(PublishInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                Publish();
                if (!_active) break;
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé.
        }
        Publish();
    }

    private void Publish()
    {
        List<double> batch;
        lock (_lock)
        {
            if (_pending.Count == 0) return;
            batch = _pending;
            _pending = new List<double>(Math.Max(64, batch.Count));
        }
        try
        {
            FramesReceived?.Invoke(this, new FrameBatch(ProcessId, batch));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Abonné FramesReceived en erreur");
        }
    }
}
