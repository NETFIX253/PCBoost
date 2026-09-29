using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Elevation;
using PCBoost.Platform.Gaming;

namespace PCBoost.Elevator;

/// <summary>
/// Capture ETW temps réel des présentations d'images d'un processus (§20, sans injection) :
/// Microsoft-Windows-DXGI (Present_Start 42, PresentMultiplaneOverlay_Start 55) et Microsoft-Windows-D3D9 (Present_Start 1),
/// niveau Informational. Les horodatages (TimeStampRelativeMSec) du PID cible sont écrits par lots dans le canal nommé.
/// Arrêt : canal rompu, fin du processus parent ou du jeu, ou 6 h.
/// </summary>
internal static class FrameCaptureOperation
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(6);
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(1);
    private const int PipeConnectTimeoutMs = 30_000;

    public static ElevatedResponse Run(ValidatedFrameCapture operation, ElevatorLog log)
    {
        using var target = TryGetProcess(operation.ProcessId);
        using var parent = TryGetProcess(operation.ParentProcessId);
        if (target is null) return Fail(OperationErrorKind.NotFound, "Sys_FrameCaptureTargetExited", "processus cible absent");
        if (parent is null) return Fail(OperationErrorKind.NotFound, "Sys_FrameCaptureStopped", "processus parent absent");
        if (TraceEventSession.IsElevated() != true) return Fail(OperationErrorKind.RequiresElevation, "Sys_FrameCaptureSessionFailed", "non élevé");

        var sessionName = $"PCBoost-FrameCapture-{operation.ParentProcessId}";
        StopResidualSession(sessionName, log);

        TraceEventSession session;
        try
        {
            // Faible volume (quelques centaines d'événements par seconde) : tampon commun de 4 Mo, qui préserve l'ordre
            // chronologique entre processeurs, au lieu des 64 Mo par défaut.
            session = new TraceEventSession(sessionName, TraceEventSessionOptions.Create | TraceEventSessionOptions.NoPerProcessorBuffering)
            {
                StopOnDispose = true,
                BufferSizeMB = 4,
            };
        }
        catch (Exception ex) when (IsEtwFailure(ex))
        {
            log.Write($"Session ETW impossible ({ex.GetType().Name})");
            return Fail(OperationErrorKind.Failed, "Sys_FrameCaptureSessionFailed", ex.GetType().Name);
        }

        using (session)
        {
            if (!EnableProviders(session, operation.ProcessId, log))
                return Fail(OperationErrorKind.Failed, "Sys_FrameCaptureSessionFailed", "fournisseurs ETW");

            using var pipe = new NamedPipeClientStream(".", operation.PipeName, PipeDirection.Out, PipeOptions.None);
            try
            {
                pipe.Connect(PipeConnectTimeoutMs);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                log.Write($"Canal non connecté ({ex.GetType().Name})");
                return Fail(OperationErrorKind.Timeout, "Sys_FrameCaptureTimeout", ex.GetType().Name);
            }

            var buffer = new PresentBuffer(operation.ProcessId);
            // AllEvents est appelé une seule fois par événement, après d'éventuels gestionnaires spécifiques (aucun ici) :
            // pas d'abonnement à UnhandledEvents ni à un analyseur, donc aucun double comptage.
            session.Source.AllEvents += buffer.OnEvent;

            var processing = new Thread(() => RunProcessing(session, log)) { IsBackground = true, Name = "PCBoost ETW" };
            processing.Start();
            log.Write("Capture d'images démarrée");

            var reason = Pump(pipe, buffer, target, parent, processing);
            log.Write($"Capture d'images arrêtée : {reason} ({buffer.Accepted} images)");

            session.Source.AllEvents -= buffer.OnEvent;
            session.Stop(noThrow: true);
            processing.Join(TimeSpan.FromSeconds(5));
            return new ElevatedResponse(OperationResult.Ok(), new Dictionary<string, string> { ["frames"] = buffer.Accepted.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
    }

    private static string Pump(NamedPipeClientStream pipe, PresentBuffer buffer, Process target, Process parent, Thread processing)
    {
        var clock = Stopwatch.StartNew();
        var lastWrite = TimeSpan.Zero;
        var heartbeat = new byte[FrameRecordCodec.RecordSize];
        while (true)
        {
            Thread.Sleep(FlushInterval);
            if (clock.Elapsed >= MaxDuration) return "durée maximale";
            if (HasExited(parent)) return "fin du processus parent";
            if (!processing.IsAlive) return "fin du traitement ETW";

            var records = buffer.Drain();
            try
            {
                if (records.Count > 0)
                {
                    pipe.Write(FrameRecordCodec.Encode(records));
                    lastWrite = clock.Elapsed;
                }
                else if (clock.Elapsed - lastWrite >= HeartbeatInterval)
                {
                    // Enregistrement de présence (PID 0, ignoré par le lecteur) : détecte un canal fermé même sans image.
                    FrameRecordCodec.Write(heartbeat, 0, clock.Elapsed.TotalMilliseconds);
                    pipe.Write(heartbeat);
                    lastWrite = clock.Elapsed;
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                return "canal fermé";
            }

            if (HasExited(target)) return "fin du jeu";
        }
    }

    private static void RunProcessing(TraceEventSession session, ElevatorLog log)
    {
        try
        {
            session.Source.Process();
        }
        catch (Exception ex) when (IsEtwFailure(ex))
        {
            log.Write($"Traitement ETW interrompu ({ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Active DXGI et D3D9 au niveau Informational, filtrés sur le PID cible et sur les identifiants Present_Start
    /// (filtrage noyau, Windows 8.1+). Sans prise en charge du filtrage, repli sans filtre (le tri est refait à la réception).
    /// </summary>
    private static bool EnableProviders(TraceEventSession session, int processId, ElevatorLog log)
    {
        var enabled = 0;
        foreach (var (provider, ids) in new[] { (EtwPresentEvents.DxgiProvider, EtwPresentEvents.DxgiEventIds), (EtwPresentEvents.D3D9Provider, EtwPresentEvents.D3D9EventIds) })
        {
            try
            {
                var options = new TraceEventProviderOptions { ProcessIDFilter = [processId], EventIDsToEnable = ids.ToList() };
                session.EnableProvider(provider, TraceEventLevel.Informational, ulong.MaxValue, options);
                enabled++;
                continue;
            }
            catch (Exception ex) when (IsEtwFailure(ex))
            {
                log.Write($"Filtrage ETW indisponible ({ex.GetType().Name}), repli sans filtre");
            }

            try
            {
                session.EnableProvider(provider, TraceEventLevel.Informational, ulong.MaxValue);
                enabled++;
            }
            catch (Exception ex) when (IsEtwFailure(ex))
            {
                log.Write($"Fournisseur ETW non activé ({ex.GetType().Name})");
            }
        }
        return enabled > 0;
    }

    private static void StopResidualSession(string sessionName, ElevatorLog log)
    {
        try
        {
            if (!TraceEventSession.GetActiveSessionNames().Contains(sessionName, StringComparer.OrdinalIgnoreCase)) return;
            using var residual = TraceEventSession.GetActiveSession(sessionName);
            residual?.Stop(noThrow: true);
            log.Write("Session ETW résiduelle arrêtée");
        }
        catch (Exception ex) when (IsEtwFailure(ex))
        {
            log.Write($"Session résiduelle non arrêtée ({ex.GetType().Name})");
        }
    }

    private static Process? TryGetProcess(int processId)
    {
        try
        {
            var process = System.Diagnostics.Process.GetProcessById(processId);
            if (!process.HasExited) return process;
            process.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return true;
        }
    }

    private static bool IsEtwFailure(Exception ex)
        => ex is COMException or UnauthorizedAccessException or InvalidOperationException or ArgumentException
            or NotSupportedException or Win32Exception or IOException;

    private static ElevatedResponse Fail(OperationErrorKind kind, string key, string detail)
        => new(OperationResult.Fail(kind, TextRef.Of(key), detail), new Dictionary<string, string>());

    /// <summary>Tampon partagé entre le fil ETW (producteur) et la boucle d'écriture (consommateur).</summary>
    private sealed class PresentBuffer(int processId)
    {
        private readonly Lock _lock = new();
        private readonly PresentEventFilter _filter = new();
        private List<(int ProcessId, double TimestampMs)> _records = new(256);

        public long Accepted { get; private set; }

        public void OnEvent(TraceEvent data)
        {
            if (data.ProcessID != processId) return;
            if (!EtwPresentEvents.IsPresentStart(data.ProviderGuid, (int)data.ID)) return;
            var timestamp = data.TimeStampRelativeMSec;
            lock (_lock)
            {
                if (!_filter.Accept(data.ProviderGuid, timestamp)) return;
                _records.Add((processId, timestamp));
                Accepted++;
            }
        }

        public List<(int ProcessId, double TimestampMs)> Drain()
        {
            lock (_lock)
            {
                if (_records.Count == 0) return [];
                var drained = _records;
                _records = new List<(int, double)>(Math.Max(64, drained.Count));
                drained.Sort(static (a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
                return drained;
            }
        }
    }
}
