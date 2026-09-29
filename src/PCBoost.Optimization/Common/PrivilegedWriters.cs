using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Security;

namespace PCBoost.Optimization.Common;

/// <summary>
/// Écriture d'une valeur de registre, directement si possible, sinon (HKLM non inscriptible) via PCBoost.Elevator.
/// L'élévation n'est demandée que pour une valeur binaire d'une clé de la liste blanche
/// <see cref="ForbiddenTargetPolicy.ElevatedRegistryAllowList"/> (StartupApproved) ; l'Elevator revalide la demande.
/// </summary>
internal sealed class RegistryWriter
{
    /// <summary>Taille maximale acceptée par l'Elevator pour une valeur binaire.</summary>
    internal const int MaxElevatedBinaryLength = 16;

    private readonly IRegistryProvider _registry;
    private readonly IElevationService _elevation;
    private readonly ILogger<RegistryWriter> _logger;

    public RegistryWriter(IRegistryProvider registry, IElevationService elevation, ILogger<RegistryWriter>? logger = null)
    {
        _registry = registry;
        _elevation = elevation;
        _logger = logger ?? NullLogger<RegistryWriter>.Instance;
    }

    /// <summary>Écrit <paramref name="data"/> (ou supprime la valeur si null). Idempotent.</summary>
    public async Task<OperationResult> WriteAsync(RegistryLocation location, string valueName, RegistryValueData? data, CancellationToken cancellationToken = default)
    {
        if (ForbiddenTargetPolicy.IsForbiddenRegistryLocation(location))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Safety_ForbiddenTarget"), location.ToString());

        var isMachine = location.Hive == RegistryHiveKind.LocalMachine;
        if (!isMachine || _registry.CanWrite(location))
        {
            var direct = data is null ? _registry.DeleteValue(location, valueName) : _registry.SetValue(location, valueName, data);
            if (direct.Success || !isMachine || direct.Error is not (OperationErrorKind.AccessDenied or OperationErrorKind.RequiresElevation))
                return direct;
        }

        if (_elevation.IsElevated)
            return OperationResult.Fail(OperationErrorKind.AccessDenied, TextRef.Of("Opt_Error_AccessDenied"), location.ToString());

        return await ElevateAsync(location, valueName, data, cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult> ElevateAsync(RegistryLocation location, string valueName, RegistryValueData? data, CancellationToken cancellationToken)
    {
        var keyPath = location.KeyPath.Replace('/', '\\').Trim('\\');
        if (!ForbiddenTargetPolicy.IsAllowedElevatedRegistryPath(keyPath))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Elevation_NotAllowed"), location.ToString());

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = keyPath,
            ["name"] = valueName,
            ["view"] = location.View.ToString(),
        };

        string operation;
        if (data is null)
        {
            operation = ElevatedOperations.RegistryDeleteValue;
        }
        else
        {
            if (data.Type != RegistryValueType.Binary || data.Value is not byte[] bytes)
                return OperationResult.Fail(OperationErrorKind.RequiresElevation, TextRef.Of("Opt_Elevation_NotAllowed"), $"type {data.Type}");
            if (bytes.Length == 0 || bytes.Length > MaxElevatedBinaryLength)
                return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Opt_Elevation_NotAllowed"), $"{bytes.Length} octets");
            operation = ElevatedOperations.RegistrySetValue;
            parameters["valueBase64"] = Convert.ToBase64String(bytes);
        }

        try
        {
            var response = await _elevation.RunAsync(new ElevatedRequest(operation, parameters), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Écriture élevée {Operation} sur {Location} : {Success}", operation, location, response.Outcome.Success);
            return WithDefaultMessage(response.Outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult.FromException(ex);
        }
    }

    internal static OperationResult WithDefaultMessage(OperationResult outcome)
        => outcome.Success || outcome.Message is not null
            ? outcome
            : outcome.Error switch
            {
                OperationErrorKind.ElevationCancelled => outcome with { Message = TextRef.Of("Opt_Error_ElevationCancelled") },
                OperationErrorKind.AccessDenied or OperationErrorKind.RequiresElevation => outcome with { Message = TextRef.Of("Opt_Error_AccessDenied") },
                _ => outcome,
            };
}

/// <summary>Activation/désactivation d'une tâche planifiée non Microsoft, avec repli sur PCBoost.Elevator si l'accès est refusé.</summary>
internal sealed class ScheduledTaskWriter
{
    private readonly IScheduledTaskProvider _tasks;
    private readonly IElevationService _elevation;
    private readonly ILogger<ScheduledTaskWriter> _logger;

    public ScheduledTaskWriter(IScheduledTaskProvider tasks, IElevationService elevation, ILogger<ScheduledTaskWriter>? logger = null)
    {
        _tasks = tasks;
        _elevation = elevation;
        _logger = logger ?? NullLogger<ScheduledTaskWriter>.Instance;
    }

    public static bool IsMicrosoftTaskPath(string taskPath)
        => taskPath.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase)
           || string.Equals(taskPath.TrimEnd('\\'), @"\Microsoft", StringComparison.OrdinalIgnoreCase);

    public async Task<OperationResult> SetEnabledAsync(string taskPath, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(taskPath)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        if (IsMicrosoftTaskPath(taskPath))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Opt_Safety_MicrosoftTask"), taskPath);

        var direct = _tasks.SetEnabled(taskPath, enabled);
        if (direct.Success || direct.Error is not (OperationErrorKind.AccessDenied or OperationErrorKind.RequiresElevation))
            return direct;
        if (_elevation.IsElevated)
            return RegistryWriter.WithDefaultMessage(direct);

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["path"] = taskPath,
            ["enabled"] = enabled ? "true" : "false",
        };
        try
        {
            var response = await _elevation.RunAsync(new ElevatedRequest(ElevatedOperations.ScheduledTaskSetEnabled, parameters), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Tâche planifiée (élevée) : {Success}", response.Outcome.Success);
            return RegistryWriter.WithDefaultMessage(response.Outcome);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return OperationResult.FromException(ex);
        }
    }
}
