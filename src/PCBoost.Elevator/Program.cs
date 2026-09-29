using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Elevation;

namespace PCBoost.Elevator;

/// <summary>
/// Assistant élevé ponctuel (§35). Usage : PCBoost.Elevator.exe --request &lt;base64url(JSON)&gt; --result &lt;chemin&gt;.
/// Valide strictement la demande (liste blanche fermée), exécute une seule opération, écrit ElevatedResponse en JSON
/// puis se termine (code 0 = succès, 1 = échec). Aucune commande arbitraire ni aucun chemin arbitraire n'est accepté.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        var log = new ElevatorLog();
        try
        {
            return Run(args, log);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Write($"Erreur inattendue : {ex.GetType().Name}");
            return 1;
        }
    }

    private static int Run(string[] args, ElevatorLog log)
    {
        if (!ElevatorArguments.TryParse(args, out var encodedRequest, out var resultPath))
        {
            log.Write("Arguments refusés");
            return 1;
        }
        if (!ElevationPaths.IsValidResultPath(resultPath))
        {
            // Sans chemin de résultat valide, rien n'est écrit : l'application constatera l'absence de résultat.
            log.Write("Chemin de résultat refusé");
            return 1;
        }

        if (!ElevatedRequestCodec.TryDecode(encodedRequest, out var request) || request is null)
        {
            log.Write("Demande illisible");
            WriteResult(resultPath, new ElevatedResponse(
                OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Sys_ElevatedInvalidParameters"), "décodage"),
                new Dictionary<string, string>()), log);
            return 1;
        }

        var validation = ElevatedRequestValidator.Validate(request);
        if (!validation.Success || validation.Value is null)
        {
            log.Write($"Demande refusée : {Sanitize(request.Operation)} ({validation.Error}, {validation.TechnicalDetail})");
            WriteResult(resultPath, new ElevatedResponse(validation.ToResult(), new Dictionary<string, string>()), log);
            return 1;
        }

        var operation = validation.Value;
        log.Write($"Opération acceptée : {operation.Operation}");
        var response = operation switch
        {
            ValidatedCleanup cleanup => ElevatedOperationExecutor.Cleanup(cleanup, log),
            ValidatedRegistryChange registry => ElevatedOperationExecutor.Registry(registry, log),
            ValidatedTaskToggle task => ElevatedOperationExecutor.ScheduledTask(task, log),
            ValidatedFrameCapture frames => FrameCaptureOperation.Run(frames, log),
            ValidatedHealthOperation health => ElevatedOperationExecutor.Health(health, log),
            _ => new ElevatedResponse(
                OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ElevatedOperationRefused")),
                new Dictionary<string, string>()),
        };

        WriteResult(resultPath, response, log);
        log.Write($"Terminé : {(response.Outcome.Success ? "succès" : response.Outcome.Error.ToString())}");
        return response.Outcome.Success ? 0 : 1;
    }

    private static void WriteResult(string resultPath, ElevatedResponse response, ElevatorLog log)
    {
        var json = ElevationJson.SerializeResponse(response);
        if (!SecureFileWriter.TryWriteNewFile(resultPath, json, resolved => ElevationPaths.IsAcceptableResolvedResultPath(resolved, resultPath), checkedParentLevels: 4, out var error))
            log.Write($"Résultat non écrit ({error})");
    }

    /// <summary>Nom d'opération reçu (non fiable) réduit à des caractères sûrs pour le journal.</summary>
    private static string Sanitize(string value)
    {
        var safe = new string(value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_').Take(40).ToArray());
        return safe.Length == 0 ? "?" : safe;
    }
}
