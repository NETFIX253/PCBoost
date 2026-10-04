using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Drivers;
using PCBoost.Platform.Drivers;
using PCBoost.Platform.Elevation;

namespace PCBoost.Elevator;

/// <summary>Opérations sur les pilotes déjà validées par <see cref="ElevatedRequestValidator"/>.</summary>
internal static class DriverOperations
{
    public static ElevatedResponse Install(ValidatedDriverInstall operation, ElevatorLog log)
    {
        using var progress = ProgressPipeWriter.Connect(operation.ProgressPipe);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var installer = new DriverInstaller(logger, log.Write, p => progress?.Write(DriverElevatedData.FormatProgress(p)));
        log.Write($"Pilotes : {operation.UpdateIds.Count} mise(s) à jour demandée(s)");
        var report = installer.Install(operation.UpdateIds, operation.EnableProtection);
        var data = DriverElevatedData.EncodeInstall(report.Stop, report.RestorePoint, report.RestorePointAt, report.ProtectionEnabled, report.Drivers, report.RebootRequired);

        // L'opération elle-même a abouti (résultats détaillés par pilote) ; un arrêt avant installation est un échec.
        var outcome = report.Stop is DriverInstallStop.None or DriverInstallStop.DeviceProblem
            ? OperationResult.Ok()
            : OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Drv_Stop_" + report.Stop), report.Stop.ToString());
        return new ElevatedResponse(outcome, data);
    }

    /// <summary>Point de restauration neuf (même création que l'installation), protection activée seulement si autorisée.</summary>
    public static ElevatedResponse RestorePoint(ValidatedDriverRestorePoint operation, ElevatorLog log)
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var enabled = false;
        var (status, createdAt) = Platform.Health.ElevatedHealthReaders.CreateDriverRestorePoint(DateTimeOffset.UtcNow, logger);
        if (status == Core.Models.Health.RestorePointStatus.Disabled && operation.EnableProtection)
        {
            enabled = Platform.Health.ElevatedHealthReaders.EnableSystemProtection(logger);
            if (enabled) (status, createdAt) = Platform.Health.ElevatedHealthReaders.CreateDriverRestorePoint(DateTimeOffset.UtcNow, logger);
        }
        log.Write($"Point de restauration à la demande : {status} (protection activée : {enabled})");
        return new ElevatedResponse(OperationResult.Ok(), DriverElevatedData.EncodeRestorePoint(status, createdAt, enabled));
    }

    public static ElevatedResponse Rollback(ValidatedDriverRollback operation, ElevatorLog log)
    {
        log.Write($"Retour au pilote précédent : {operation.Targets.Count} périphérique(s)");
        var (outcomes, reboot) = new DriverRollback(log.Write).Run(operation.Targets, operation.InstalledVersion);
        var data = DriverElevatedData.EncodeRollback(outcomes, reboot);
        // Détail par périphérique dans les données ; l'application en tire le message (absent, modifié depuis, échec).
        var notRestored = outcomes.Count(o => !o.IsRestored);
        var outcome = notRestored == 0
            ? OperationResult.Ok()
            : OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Drv_Rollback_Failed"), $"{notRestored} non restauré(s)");
        return new ElevatedResponse(outcome, data);
    }
}

/// <summary>
/// Écriture de la progression dans le canal nommé créé par l'application (écriture seule, lignes courtes). Sans canal
/// ou en cas d'erreur, la progression est simplement abandonnée : elle n'interrompt jamais l'opération.
/// </summary>
internal sealed class ProgressPipeWriter : IDisposable
{
    private const int ConnectTimeoutMs = 5_000;

    private NamedPipeClientStream? _pipe;

    private ProgressPipeWriter(NamedPipeClientStream pipe) => _pipe = pipe;

    public static ProgressPipeWriter? Connect(string? pipeName)
    {
        if (!ElevatedRequestValidator.IsValidProgressPipeName(pipeName)) return null;
        // Identification seulement : le serveur (application non élevée) ne peut pas emprunter le jeton administrateur.
        var pipe = new NamedPipeClientStream(".", pipeName!, PipeDirection.Out, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(ConnectTimeoutMs);
            return new ProgressPipeWriter(pipe);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            pipe.Dispose();
            return null;
        }
    }

    public void Write(string line)
    {
        if (_pipe is null) return;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            _pipe.Write(bytes, 0, bytes.Length);
            _pipe.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            _pipe.Dispose();
            _pipe = null;
        }
    }

    public void Dispose()
    {
        _pipe?.Dispose();
        _pipe = null;
    }
}
