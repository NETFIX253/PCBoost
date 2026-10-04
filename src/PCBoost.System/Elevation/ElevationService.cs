using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;
using PCBoost.Platform.Elevation;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Élévation ponctuelle (§35) : lance PCBoost.Elevator (verbe « runas ») pour une seule opération de la liste blanche,
/// attend sa fin, lit puis supprime le fichier de résultat. La demande est validée avant l'invite UAC.
/// </summary>
public sealed class ElevationService : IElevationService
{
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);

    /// <summary>Installation de pilotes : point de restauration, téléchargements et installations (parfois volumineux).</summary>
    private static readonly TimeSpan DriverInstallMaxDuration = TimeSpan.FromHours(2);

    private const int MaxProgressLineLength = 64;

    private readonly ILogger<ElevationService> _logger;

    public ElevationService(ILogger<ElevationService>? logger = null)
    {
        _logger = logger ?? NullLogger<ElevationService>.Instance;
    }

    public bool IsElevated => TokenHelper.IsCurrentProcessElevated;

    public Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
        => RunCoreAsync(request, cancellationToken);

    /// <summary>Processus « PCBoost.Elevator » présent dans la session (nom visible sans élévation).</summary>
    public bool IsHelperRunning()
    {
        Process[] processes = [];
        try
        {
            processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ElevationPaths.ElevatorExecutableName));
            return processes.Length > 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // État inconnu : considéré comme occupé (aucune opération concurrente sur les pilotes).
            _logger.LogDebug(ex, "Liste des processus indisponible");
            return true;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    /// <summary>
    /// Opérations qui transmettent leur progression (installation de pilotes) : un canal nommé à nom aléatoire est créé
    /// par l'application, l'Elevator s'y connecte en écriture seule ; les lignes reçues sont relayées telles quelles.
    /// Sans connexion, l'opération se déroule normalement, sans progression.
    /// </summary>
    public async Task<ElevatedResponse> RunWithProgressAsync(ElevatedRequest request, IProgress<string>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (progress is null || request.Operation != ElevatedDriverOperations.Install || request.Parameters.ContainsKey(DriverElevatedData.ProgressPipeKey))
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);

        var pipeName = ElevatedRequestValidator.NewProgressPipeName();
        NamedPipeServerStream server;
        try
        {
            server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _logger.LogDebug(ex, "Canal de progression indisponible");
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }

        using (server)
        using (var stopReading = new CancellationTokenSource())
        {
            var parameters = new Dictionary<string, string>(request.Parameters, StringComparer.Ordinal) { [DriverElevatedData.ProgressPipeKey] = pipeName };
            var reading = ReadProgressAsync(server, progress, stopReading.Token);
            try
            {
                return await RunCoreAsync(request with { Parameters = parameters }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await stopReading.CancelAsync().ConfigureAwait(false);
                await reading.ConfigureAwait(false);
            }
        }
    }

    internal static async Task ReadProgressAsync(Stream server, IProgress<string> progress, CancellationToken cancellationToken)
    {
        try
        {
            if (server is NamedPipeServerStream pipe) await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 256, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (line.Length is > 0 and <= MaxProgressLineLength) progress.Report(line);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Fin de l'opération ou Elevator sans canal : la progression est facultative.
        }
    }

    private async Task<ElevatedResponse> RunCoreAsync(ElevatedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var validation = ElevatedRequestValidator.Validate(request);
        if (!validation.Success) return Response(validation.ToResult());
        if (validation.Value is ValidatedFrameCapture)
        {
            // La capture d'images est une session longue gérée par IFrameTimeSource, pas une opération ponctuelle.
            return Response(OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Sys_ElevatedInvalidParameters"), "frames.capture"));
        }

        string resultPath;
        try
        {
            resultPath = ElevatorLauncher.PrepareResultPath();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Response(OperationResult.FromException(ex) with { Message = TextRef.Of("Sys_ElevationFailed") });
        }

        // ShellExecuteEx (« runas ») bloque pendant l'invite UAC : jamais sur le fil appelant (souvent celui de l'interface).
        var started = await Task.Run(() => ElevatorLauncher.Start(request, resultPath), CancellationToken.None).ConfigureAwait(false);
        if (!started.Success || started.Value is null)
        {
            _logger.LogInformation("Élévation non obtenue pour {Operation} : {Error}", request.Operation, started.Error);
            return Response(started.ToResult());
        }

        using var process = started.Value;
        using var timeout = new CancellationTokenSource(request.Operation == ElevatedDriverOperations.Install ? DriverInstallMaxDuration : MaxDuration);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Un processus élevé ne peut pas être arrêté depuis un processus non élevé : on cesse seulement d'attendre.
            var kind = cancellationToken.IsCancellationRequested ? OperationErrorKind.Cancelled : OperationErrorKind.Timeout;
            _logger.LogWarning("Attente de l'opération élevée {Operation} interrompue ({Kind})", request.Operation, kind);
            return Response(OperationResult.Fail(kind, TextRef.Of("Sys_ElevationFailed")));
        }

        var exitCode = process.ExitCode;
        var response = ElevatorLauncher.ReadAndDeleteResult(resultPath);
        if (response is null)
        {
            _logger.LogWarning("Opération élevée {Operation} terminée sans résultat (code {ExitCode})", request.Operation, exitCode);
            return Response(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_ElevationNoResult", exitCode), $"exit code {exitCode}"));
        }

        _logger.LogInformation("Opération élevée {Operation} : {Success} (code {ExitCode})", request.Operation, response.Outcome.Success, exitCode);
        return response;
    }

    private static ElevatedResponse Response(OperationResult outcome)
        => new(outcome, new Dictionary<string, string>());
}
