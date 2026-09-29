using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
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

    private readonly ILogger<ElevationService> _logger;

    public ElevationService(ILogger<ElevationService>? logger = null)
    {
        _logger = logger ?? NullLogger<ElevationService>.Instance;
    }

    public bool IsElevated => TokenHelper.IsCurrentProcessElevated;

    public async Task<ElevatedResponse> RunAsync(ElevatedRequest request, CancellationToken cancellationToken = default)
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
        using var timeout = new CancellationTokenSource(MaxDuration);
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
