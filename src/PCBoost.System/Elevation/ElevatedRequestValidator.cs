using System.Globalization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Security;

namespace PCBoost.Platform.Elevation;

/// <summary>Opération privilégiée validée : seules ces formes peuvent être exécutées par PCBoost.Elevator.</summary>
internal abstract record ValidatedElevatedOperation(string Operation);

/// <summary>Catégories de nettoyage du catalogue fermé (identifiants uniquement, jamais de chemin).</summary>
internal sealed record ValidatedCleanup(IReadOnlyList<CleanupCategoryDefinition> Categories)
    : ValidatedElevatedOperation(ElevatedOperations.CleanupCategory);

/// <summary>Écriture (Value non null) ou suppression (Value null) d'une valeur binaire HKLM de la liste blanche StartupApproved.</summary>
internal sealed record ValidatedRegistryChange(string KeyPath, string ValueName, RegistryViewKind View, byte[]? Value)
    : ValidatedElevatedOperation(Value is null ? ElevatedOperations.RegistryDeleteValue : ElevatedOperations.RegistrySetValue)
{
    public RegistryLocation Location => new(RegistryHiveKind.LocalMachine, KeyPath, View);
}

internal sealed record ValidatedTaskToggle(string TaskPath, bool Enabled)
    : ValidatedElevatedOperation(ElevatedOperations.ScheduledTaskSetEnabled);

internal sealed record ValidatedFrameCapture(int ProcessId, string PipeName, int ParentProcessId)
    : ValidatedElevatedOperation(ElevatedOperations.FrameCapture);

/// <summary>
/// Opérations de diagnostic et de sécurité sans paramètre : lectures seules (fiabilité des disques, mesures de démarrage,
/// dernière exécution des programmes) ou création d'un point de restauration à la description fixe.
/// </summary>
internal sealed record ValidatedHealthOperation(string Name) : ValidatedElevatedOperation(Name);

/// <summary>
/// Validation stricte des demandes d'élévation (liste blanche fermée), appliquée par l'application avant l'invite UAC
/// et à nouveau par PCBoost.Elevator. Toute opération inconnue, tout paramètre inattendu ou hors limites est refusé.
/// </summary>
internal static class ElevatedRequestValidator
{
    public const string FramePipePrefix = "PCBoost.Frames.";
    public const int MaxCategories = 16;
    public const int MaxRegistryBinaryLength = 16;
    public const int MaxValueNameLength = 260;
    public const int MaxTaskPathLength = 1024;

    public static OperationResult<ValidatedElevatedOperation> Validate(ElevatedRequest? request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Operation) || request.Parameters is null)
            return Invalid("requête vide");

        return request.Operation switch
        {
            ElevatedOperations.CleanupCategory => ValidateCleanup(request.Parameters),
            ElevatedOperations.RegistrySetValue => ValidateRegistry(request.Parameters, isSet: true),
            ElevatedOperations.RegistryDeleteValue => ValidateRegistry(request.Parameters, isSet: false),
            ElevatedOperations.ScheduledTaskSetEnabled => ValidateTask(request.Parameters),
            ElevatedOperations.FrameCapture => ValidateFrameCapture(request.Parameters),
            ElevatedHealthOperations.DiskReliability or ElevatedHealthOperations.BootPerformance
                or ElevatedHealthOperations.RestorePointCreate or ElevatedHealthOperations.AppsLastRun
                => request.Parameters.Count == 0 ? Ok(new ValidatedHealthOperation(request.Operation)) : Invalid("paramètres"),
            _ => OperationResult<ValidatedElevatedOperation>.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ElevatedOperationRefused"), "opération inconnue"),
        };
    }

    private static OperationResult<ValidatedElevatedOperation> ValidateCleanup(IReadOnlyDictionary<string, string> parameters)
    {
        if (!HasOnlyKeys(parameters, ["categories"], [])) return Invalid("paramètres");
        var ids = parameters["categories"].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (ids.Length == 0 || ids.Length > MaxCategories) return Invalid("nombre de catégories");

        var categories = new List<CleanupCategoryDefinition>();
        foreach (var id in ids)
        {
            var category = CleanupCatalog.Find(id);
            if (category is null || !string.Equals(category.Id, id, StringComparison.Ordinal))
                return Refused("catégorie inconnue");
            if (!category.RequiresElevation || category.IsRecycleBin)
                return Refused("catégorie non élevée");
            if (categories.All(c => c.Id != category.Id)) categories.Add(category);
        }
        return Ok(new ValidatedCleanup(categories));
    }

    private static OperationResult<ValidatedElevatedOperation> ValidateRegistry(IReadOnlyDictionary<string, string> parameters, bool isSet)
    {
        string[] required = isSet ? ["path", "name", "valueBase64"] : ["path", "name"];
        if (!HasOnlyKeys(parameters, required, ["view"])) return Invalid("paramètres");

        var path = parameters["path"].Trim().Trim('\\');
        if (!ForbiddenTargetPolicy.IsAllowedElevatedRegistryPath(path) || ForbiddenTargetPolicy.IsForbiddenRegistryPath(path))
            return Refused("clé hors liste blanche");

        var name = parameters["name"];
        if (name.Length == 0 || name.Length > MaxValueNameLength || name.Any(char.IsControl)) return Invalid("nom de valeur");

        var view = RegistryViewKind.Default;
        if (parameters.TryGetValue("view", out var viewText) && !TryParseView(viewText, out view)) return Invalid("vue");

        byte[]? value = null;
        if (isSet)
        {
            var buffer = new byte[MaxRegistryBinaryLength + 1];
            if (!Convert.TryFromBase64String(parameters["valueBase64"], buffer, out var written) || written == 0 || written > MaxRegistryBinaryLength)
                return Invalid("valeur binaire");
            value = buffer[..written];
        }

        var canonicalPath = ForbiddenTargetPolicy.ElevatedRegistryAllowList.First(a => string.Equals(a, path, StringComparison.OrdinalIgnoreCase));
        return Ok(new ValidatedRegistryChange(canonicalPath, name, view, value));
    }

    private static OperationResult<ValidatedElevatedOperation> ValidateTask(IReadOnlyDictionary<string, string> parameters)
    {
        if (!HasOnlyKeys(parameters, ["path", "enabled"], [])) return Invalid("paramètres");
        var path = parameters["path"];
        if (!ScheduledTaskProvider.IsValidTaskPath(path) || path.Length > MaxTaskPathLength) return Invalid("chemin de tâche");
        if (ScheduledTaskProvider.IsMicrosoftPath(path)) return Refused("tâche Microsoft");
        if (!TryParseBool(parameters["enabled"], out var enabled)) return Invalid("enabled");
        return Ok(new ValidatedTaskToggle(path, enabled));
    }

    private static OperationResult<ValidatedElevatedOperation> ValidateFrameCapture(IReadOnlyDictionary<string, string> parameters)
    {
        if (!HasOnlyKeys(parameters, ["pid", "pipe", "parentPid"], [])) return Invalid("paramètres");
        if (!TryParsePositiveInt(parameters["pid"], out var pid) || pid <= 4) return Invalid("pid");
        if (!TryParsePositiveInt(parameters["parentPid"], out var parentPid) || parentPid <= 4) return Invalid("parentPid");
        var pipe = parameters["pipe"];
        if (!IsValidFramePipeName(pipe)) return Invalid("canal");
        return Ok(new ValidatedFrameCapture(pid, pipe, parentPid));
    }

    /// <summary>« PCBoost.Frames. » suivi d'un GUID au format N (32 chiffres hexadécimaux).</summary>
    public static bool IsValidFramePipeName(string? pipe)
        => pipe is not null
           && pipe.Length == FramePipePrefix.Length + 32
           && pipe.StartsWith(FramePipePrefix, StringComparison.Ordinal)
           && Guid.TryParseExact(pipe[FramePipePrefix.Length..], "N", out _);

    public static string NewFramePipeName() => FramePipePrefix + Guid.NewGuid().ToString("N");

    private static bool HasOnlyKeys(IReadOnlyDictionary<string, string> parameters, string[] required, string[] optional)
    {
        foreach (var key in required)
        {
            if (!parameters.TryGetValue(key, out var value) || value is null) return false;
        }
        return parameters.Keys.All(k => required.Contains(k, StringComparer.Ordinal) || optional.Contains(k, StringComparer.Ordinal));
    }

    private static bool TryParseView(string text, out RegistryViewKind view)
    {
        view = text switch
        {
            nameof(RegistryViewKind.Default) => RegistryViewKind.Default,
            nameof(RegistryViewKind.Registry32) => RegistryViewKind.Registry32,
            nameof(RegistryViewKind.Registry64) => RegistryViewKind.Registry64,
            _ => (RegistryViewKind)(-1),
        };
        return Enum.IsDefined(view);
    }

    private static bool TryParseBool(string text, out bool value)
    {
        switch (text)
        {
            case "true":
            case "True":
                value = true;
                return true;
            case "false":
            case "False":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static bool TryParsePositiveInt(string text, out int value)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    private static OperationResult<ValidatedElevatedOperation> Ok(ValidatedElevatedOperation operation)
        => OperationResult<ValidatedElevatedOperation>.Ok(operation);

    private static OperationResult<ValidatedElevatedOperation> Invalid(string detail)
        => OperationResult<ValidatedElevatedOperation>.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Sys_ElevatedInvalidParameters"), detail);

    private static OperationResult<ValidatedElevatedOperation> Refused(string detail)
        => OperationResult<ValidatedElevatedOperation>.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ElevatedOperationRefused"), detail);
}
