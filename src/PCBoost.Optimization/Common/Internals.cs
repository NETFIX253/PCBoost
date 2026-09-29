using Microsoft.Extensions.Logging;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;

namespace PCBoost.Optimization.Common;

/// <summary>
/// Extension interne du consignateur : permet d'enregistrer l'état « après » (vérification, historique Expert).
/// <see cref="PendingChange"/> ne porte pas d'état « après » dans Core.
/// </summary>
internal interface IChangeRecorderWithAfterState : IChangeRecorder
{
    Task<OperationResult> ApplyAsync(PendingChange change, string? afterState, Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default);
}

/// <summary>Services internes du gestionnaire de restauration utilisés par la récupération et les modules.</summary>
internal interface IRollbackInternals
{
    /// <summary>La session a-t-elle été ouverte par le processus courant et n'est-elle pas encore terminée ?</summary>
    bool IsOpenInCurrentProcess(Guid sessionId);

    void NotifySessionChanged(Guid sessionId);

    /// <summary>Restaure uniquement les modifications de la session qui satisfont le filtre (ex. un module).</summary>
    Task<RollbackResult> RestoreChangesAsync(Guid sessionId, Func<ChangeRecord, bool> filter, CancellationToken cancellationToken = default);
}

internal static class ChangeRecorderExtensions
{
    public static Task<OperationResult> ApplyWithAfterStateAsync(this IChangeRecorder recorder, PendingChange change, string? afterState,
        Func<CancellationToken, Task<OperationResult>> apply, CancellationToken cancellationToken = default)
        => recorder is IChangeRecorderWithAfterState extended
            ? extended.ApplyAsync(change, afterState, apply, cancellationToken)
            : recorder.ApplyAsync(change, apply, cancellationToken);
}

internal static class JournalExtensions
{
    /// <summary>Journalise sans jamais faire échouer l'opération appelante (le journal est informatif).</summary>
    public static async Task TryLogAsync(this IActivityJournal journal, ActivityKind kind, TextRef message, string? detail, ILogger logger)
    {
        try
        {
            await journal.LogAsync(kind, message, detail).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Écriture du journal d'activité impossible ({Key})", message.Key);
        }
    }

    /// <summary>Variante « fire-and-forget » pour les méthodes synchrones.</summary>
    public static void LogInBackground(this IActivityJournal journal, ActivityKind kind, TextRef message, string? detail, ILogger logger)
        => _ = journal.TryLogAsync(kind, message, detail, logger);
}

internal static class PathUtil
{
    public static bool IsUnder(string? path, string? folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        var p = path.Replace('/', '\\');
        var f = folder.Replace('/', '\\').TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    public static string FileName(string path)
    {
        var p = path.Replace('/', '\\').TrimEnd('\\');
        var i = p.LastIndexOf('\\');
        return i >= 0 ? p[(i + 1)..] : p;
    }

    public static string WithoutExtension(string fileName)
    {
        var i = fileName.LastIndexOf('.');
        return i > 0 ? fileName[..i] : fileName;
    }

    public static string Combine(string folder, string relative)
        => folder.Replace('/', '\\').TrimEnd('\\') + "\\" + relative.Replace('/', '\\').TrimStart('\\');
}
