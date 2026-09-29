using System.Globalization;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Platform;
using PCBoost.Platform.Elevation;
using PCBoost.Platform.Health;

namespace PCBoost.Elevator;

/// <summary>Exécution des opérations ponctuelles déjà validées par <see cref="ElevatedRequestValidator"/>.</summary>
internal static class ElevatedOperationExecutor
{
    /// <summary>Nettoyage des catégories système du catalogue via CleanupExecutor et le vrai système de fichiers.</summary>
    public static ElevatedResponse Cleanup(ValidatedCleanup operation, ElevatorLog log)
    {
        var executor = new CleanupExecutor(new FileSystemProvider(), new SystemClock());
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        var failures = 0;

        foreach (var category in operation.Categories)
        {
            try
            {
                var totals = executor.Delete(category);
                data[$"{category.Id}.bytes"] = totals.BytesFreed.ToString(CultureInfo.InvariantCulture);
                data[$"{category.Id}.deleted"] = totals.FilesDeleted.ToString(CultureInfo.InvariantCulture);
                // Les fichiers en échec n'ont pas été supprimés : ils sont comptés comme ignorés.
                data[$"{category.Id}.skipped"] = (totals.FilesSkipped + totals.FilesFailed).ToString(CultureInfo.InvariantCulture);
                log.Write($"Nettoyage {category.Id} : {totals.FilesDeleted} supprimé(s), {totals.FilesSkipped + totals.FilesFailed} ignoré(s), {totals.BytesFreed} octets");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failures++;
                data[$"{category.Id}.bytes"] = "0";
                data[$"{category.Id}.deleted"] = "0";
                data[$"{category.Id}.skipped"] = "0";
                log.Write($"Nettoyage {category.Id} : échec ({ex.GetType().Name})");
            }
        }

        var outcome = failures == 0
            ? OperationResult.Ok()
            : OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_ElevatedCleanupPartial"), $"{failures} catégorie(s) en échec");
        return new ElevatedResponse(outcome, data);
    }

    /// <summary>Écriture ou suppression d'une valeur binaire StartupApproved sous HKLM (clé de la liste blanche).</summary>
    public static ElevatedResponse Registry(ValidatedRegistryChange operation, ElevatorLog log)
    {
        var registry = new RegistryProvider();
        var outcome = operation.Value is null
            ? registry.DeleteValue(operation.Location, operation.ValueName)
            : registry.SetValue(operation.Location, operation.ValueName, RegistryValueData.Binary(operation.Value));
        log.Write($"Registre {operation.Operation} ({operation.View}) : {(outcome.Success ? "succès" : outcome.Error.ToString())}");
        return new ElevatedResponse(outcome, new Dictionary<string, string>());
    }

    /// <summary>Diagnostics et point de restauration : opérations sans paramètre, sources fixes.</summary>
    public static ElevatedResponse Health(ValidatedHealthOperation operation, ElevatorLog log)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var now = DateTimeOffset.UtcNow;
        switch (operation.Name)
        {
            case ElevatedHealthOperations.DiskReliability:
            {
                var disks = ElevatedHealthReaders.ReadDiskReliability(now, logger);
                log.Write($"Fiabilité des disques : {disks.Count} disque(s)");
                return new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeDisks(disks));
            }
            case ElevatedHealthOperations.BootPerformance:
            {
                var (boots, degradations, denied) = ElevatedHealthReaders.ReadBootPerformance();
                log.Write($"Mesures de démarrage : {boots.Count} démarrage(s), {degradations.Count} ralentissement(s)");
                return denied
                    ? new ElevatedResponse(OperationResult.Fail(OperationErrorKind.AccessDenied, TextRef.Of("Sys_ElevationFailed")), new Dictionary<string, string>())
                    : new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeBoot(boots, degradations));
            }
            case ElevatedHealthOperations.RestorePointCreate:
            {
                var (status, createdAt) = ElevatedHealthReaders.CreateRestorePoint(now, logger);
                log.Write($"Point de restauration : {status}");
                return new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeRestorePoint(status, createdAt));
            }
            case ElevatedHealthOperations.AppsLastRun:
            {
                var lastRun = ElevatedHealthReaders.ReadLastRun(logger);
                log.Write($"Dernière exécution des programmes : {lastRun.Count} entrée(s)");
                return new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeLastRun(lastRun));
            }
            default:
                return new ElevatedResponse(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_ElevatedOperationRefused")), new Dictionary<string, string>());
        }
    }

    /// <summary>Activation ou désactivation d'une tâche planifiée non Microsoft.</summary>
    public static ElevatedResponse ScheduledTask(ValidatedTaskToggle operation, ElevatorLog log)
    {
        var outcome = new ScheduledTaskProvider().SetEnabled(operation.TaskPath, operation.Enabled);
        log.Write($"Tâche planifiée : {(outcome.Success ? "succès" : outcome.Error.ToString())}");
        return new ElevatedResponse(outcome, new Dictionary<string, string>());
    }
}
