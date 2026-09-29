using System.ComponentModel;
using System.Diagnostics;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform.Elevation;

/// <summary>Démarrage de PCBoost.Elevator avec le verbe « runas » (invite UAC si le processus n'est pas déjà élevé).</summary>
internal static class ElevatorLauncher
{
    public static bool IsAvailable => File.Exists(ElevationPaths.ElevatorPath);

    /// <summary>Prépare le dossier de résultat et renvoie un nouveau chemin de résultat.</summary>
    public static string PrepareResultPath()
    {
        Directory.CreateDirectory(ElevationPaths.ElevationDirectory);
        ElevationPaths.DeleteStaleResults();
        return ElevationPaths.NewResultPath();
    }

    /// <summary>
    /// Lance l'Elevator. Renvoie le processus, ou un échec : ElevationCancelled (UAC refusée, erreur 1223),
    /// NotSupported (Elevator absent) ou l'erreur classée.
    /// </summary>
    public static OperationResult<Process> Start(ElevatedRequest request, string resultPath)
    {
        if (!IsAvailable)
            return OperationResult<Process>.Fail(OperationErrorKind.NotSupported, TextRef.Of("Sys_ElevatorMissing"));

        var info = new ProcessStartInfo(ElevationPaths.ElevatorPath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = ElevatorArguments.Build(ElevatedRequestCodec.Encode(request), resultPath),
        };

        try
        {
            var process = Process.Start(info);
            return process is null
                ? OperationResult<Process>.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_ElevationFailed"))
                : OperationResult<Process>.Ok(process);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Win32Errors.ERROR_CANCELLED)
        {
            return OperationResult<Process>.Fail(OperationErrorKind.ElevationCancelled, TextRef.Of("Sys_ElevationCancelled"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            var failure = OperationResult<Process>.FromException(ex);
            return failure with { Message = TextRef.Of("Sys_ElevationFailed") };
        }
    }

    /// <summary>Lit puis supprime le fichier de résultat (null s'il est absent ou illisible).</summary>
    public static ElevatedResponse? ReadAndDeleteResult(string resultPath)
    {
        try
        {
            if (!File.Exists(resultPath)) return null;
            var info = new FileInfo(resultPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > 1024 * 1024) return null;
            var bytes = File.ReadAllBytes(resultPath);
            return ElevationJson.DeserializeResponse(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(resultPath)) File.Delete(resultPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Supprimé plus tard par DeleteStaleResults.
            }
        }
    }
}
