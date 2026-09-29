using System.Globalization;
using PCBoost.Platform.Elevation;

namespace PCBoost.Elevator;

/// <summary>
/// Journal minimal de l'assistant élevé (%LOCALAPPDATA%\PCBoost\Logs\elevator.log) : opération, résultat, codes d'erreur.
/// Aucun chemin de profil, nom d'utilisateur ni contenu n'y est écrit. L'écriture refuse toute redirection.
/// </summary>
internal sealed class ElevatorLog
{
    private readonly string? _path;

    public ElevatorLog()
    {
        try
        {
            var root = Path.Combine(ElevationPaths.LocalAppData, ElevationPaths.DataFolderName);
            var rootInfo = new DirectoryInfo(root);
            if (!rootInfo.Exists || (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0) return;
            var directory = ElevationPaths.LogDirectory;
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, ElevationPaths.ElevatorLogFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            _path = null;
        }
    }

    public void Write(string message)
    {
        if (_path is null) return;
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ} [{Environment.ProcessId}] {message}");
        SecureFileWriter.TryAppendLine(_path, line, ElevationPaths.IsAcceptableResolvedLogPath, checkedParentLevels: 3);
    }
}
