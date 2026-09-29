using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.Platform;

/// <summary>
/// Exécution sécurisée de commandes système (§65) : seul un exécutable de la liste blanche situé directement dans
/// %WINDIR%\System32 est accepté ; arguments passés un par un (ArgumentList), sorties capturées et bornées,
/// délai d'expiration avec arrêt de l'arborescence, journalisation de chaque exécution.
/// </summary>
public sealed class CommandRunner : ICommandRunner
{
    /// <summary>Liste blanche fermée des exécutables autorisés.</summary>
    internal static readonly IReadOnlySet<string> AllowedExecutables = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "powercfg.exe" };

    private const int MaxOutputChars = 1024 * 1024;
    private const int MaxArguments = 32;
    private const int MaxArgumentLength = 1024;
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(5);

    private readonly ILogger<CommandRunner> _logger;

    static CommandRunner()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public CommandRunner(ILogger<CommandRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<CommandRunner>.Instance;
    }

    public async Task<OperationResult<CommandResult>> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var windows = Environment.GetEnvironmentVariable("WINDIR") ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!IsAllowedExecutable(spec.FileName, windows))
        {
            _logger.LogWarning("Commande refusée (hors liste blanche) : {File}", Path.GetFileName(spec.FileName));
            return OperationResult<CommandResult>.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_CommandNotAllowed"));
        }
        if (!AreArgumentsValid(spec.Arguments))
            return OperationResult<CommandResult>.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Sys_CommandNotAllowed"));
        if (!File.Exists(spec.FileName))
            return OperationResult<CommandResult>.Fail(OperationErrorKind.NotFound);

        var timeout = spec.Timeout <= TimeSpan.Zero || spec.Timeout > MaxTimeout ? MaxTimeout : spec.Timeout;
        var encoding = ConsoleEncoding();
        var info = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
            WorkingDirectory = Path.GetDirectoryName(spec.FileName) ?? string.Empty,
        };
        foreach (var argument in spec.Arguments) info.ArgumentList.Add(argument);

        var stopwatch = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start a renvoyé null.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Démarrage impossible : {File}", Path.GetFileName(spec.FileName));
            return OperationResult<CommandResult>.FromException(ex);
        }

        using (process)
        {
            var stdout = ReadBoundedAsync(process.StandardOutput);
            var stderr = ReadBoundedAsync(process.StandardError);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                Kill(process);
                if (!timedOut)
                {
                    // Les lectures se terminent à la fermeture des flux du processus arrêté.
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                    _logger.LogInformation("Commande annulée : {File}", Path.GetFileName(spec.FileName));
                    return OperationResult<CommandResult>.Fail(OperationErrorKind.Cancelled);
                }
            }

            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            var exitCode = timedOut ? -1 : process.ExitCode;
            var result = new CommandResult(exitCode, output, error, timedOut);
            _logger.LogInformation("Commande {File} {Arguments} : code {ExitCode} en {Elapsed} ms{TimedOut}",
                Path.GetFileName(spec.FileName), string.Join(' ', spec.Arguments), exitCode, stopwatch.ElapsedMilliseconds, timedOut ? " (délai dépassé)" : string.Empty);

            return timedOut
                ? new OperationResult<CommandResult>(false, result, OperationErrorKind.Timeout, TextRef.Of("Sys_CommandTimedOut"))
                : OperationResult<CommandResult>.Ok(result);
        }
    }

    /// <summary>
    /// Chemin absolu normalisé dont le dossier parent est exactement %WINDIR%\System32 et dont le nom figure dans la liste blanche.
    /// </summary>
    internal static bool IsAllowedExecutable(string? fileName, string? windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(windowsDirectory)) return false;
        if (fileName.Contains('"') || fileName.Any(char.IsControl)) return false;
        if (!IsWindowsFullyQualified(fileName) || !IsWindowsFullyQualified(windowsDirectory)) return false;

        var normalized = fileName.Replace('/', '\\');
        if (normalized.Split('\\').Any(segment => segment is "." or "..")) return false;

        var separator = normalized.LastIndexOf('\\');
        if (separator <= 0) return false;
        var directory = normalized[..separator];
        var name = normalized[(separator + 1)..];
        var system32 = windowsDirectory.Replace('/', '\\').TrimEnd('\\') + @"\System32";
        return string.Equals(directory, system32, StringComparison.OrdinalIgnoreCase) && AllowedExecutables.Contains(name);
    }

    internal static bool AreArgumentsValid(IReadOnlyList<string>? arguments)
        => arguments is not null
           && arguments.Count <= MaxArguments
           && arguments.All(a => a is not null && a.Length <= MaxArgumentLength && !a.Any(c => c is '\0' or '\r' or '\n'));

    private static bool IsWindowsFullyQualified(string path)
        => path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');

    /// <summary>Les outils console écrivent dans la page de codes OEM lorsque leur sortie est redirigée.</summary>
    private static Encoding ConsoleEncoding()
    {
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var builder = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                var room = MaxOutputChars - builder.Length;
                if (room > 0) builder.Append(buffer, 0, Math.Min(read, room));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Flux fermé par l'arrêt du processus : la sortie déjà lue est conservée.
        }
        return builder.ToString();
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Déjà terminé.
        }
    }
}
