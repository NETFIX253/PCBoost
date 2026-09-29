using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Programs;
using PCBoost.Core.Programs;

namespace PCBoost.Platform.Programs;

/// <summary>
/// Exécute la commande officielle validée par <see cref="UninstallCommandParser"/> : Windows Installer du dossier système
/// (« msiexec /x {code} », interface complète) ou exécutable de l'éditeur désigné par un chemin absolu existant.
/// Lancement par le shell pour que Windows gère l'invite d'autorisation ; aucune option silencieuse n'est ajoutée.
/// </summary>
public sealed class UninstallerLauncher : IUninstallerLauncher
{
    private const int ErrorCancelled = 1223;
    private readonly ILogger<UninstallerLauncher> _logger;

    public UninstallerLauncher(ILogger<UninstallerLauncher>? logger = null) => _logger = logger ?? NullLogger<UninstallerLauncher>.Instance;

    public async Task<OperationResult<int>> RunAsync(UninstallCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Revalidation : seule une commande produite par l'analyseur est acceptée.
        var file = command.IsWindowsInstaller ? Path.Combine(Environment.SystemDirectory, UninstallCommandParser.WindowsInstaller) : command.FileName;
        if (command.IsWindowsInstaller
                ? command.ProductCode is null || command.Arguments != $"/x {command.ProductCode}"
                : UninstallCommandParser.Parse($"\"{command.FileName}\" {command.Arguments}") is not { IsWindowsInstaller: false } reparsed || reparsed.FileName != command.FileName)
            return OperationResult<int>.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_UninstallBlocked"));
        if (!File.Exists(file)) return OperationResult<int>.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_UninstallerMissing"));

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, command.Arguments)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(file) ?? Environment.SystemDirectory,
            });
            if (process is null) return OperationResult<int>.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_UninstallerFailed"));
            _logger.LogInformation("Programme de désinstallation lancé ({Kind})", command.IsWindowsInstaller ? "Windows Installer" : "éditeur");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<int>.Ok(process.ExitCode);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return OperationResult<int>.Fail(OperationErrorKind.ElevationCancelled, TextRef.Of("Sys_UninstallDeclined"));
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogWarning("Programme de désinstallation non lancé : {Type}", ex.GetType().Name);
            return OperationResult<int>.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_UninstallerFailed"), ex.Message);
        }
    }
}
