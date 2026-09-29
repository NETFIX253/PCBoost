using PCBoost.Core.Privacy;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Display;

namespace PCBoost.Infrastructure.Logging;

/// <summary>
/// Journalisation locale (§34) : fichier roulant quotidien « pcboost-AAAAMMJJ.log » (7 fichiers, 10 Mo par fichier),
/// niveau modifiable à chaud, données personnelles masquées. Aucune sortie réseau.
/// </summary>
public static class PCBoostLogging
{
    public const string FileNamePattern = "pcboost-.log";
    public const int RetainedFileCount = 7;
    public const long FileSizeLimitBytes = 10L * 1024 * 1024;

    internal const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{" + LevelNameEnricher.PropertyName + "}] {SourceContext:l}: {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Crée la journalisation. Ne lève pas d'exception si le dossier est inaccessible (le journal est alors simplement absent).
    /// </summary>
    /// <param name="logDirectory">Dossier des journaux (créé au besoin).</param>
    /// <param name="verbose">true : niveau Debug ; false : Information.</param>
    /// <param name="redactor">Filtre des données personnelles (par défaut : utilisateur et machine courants).</param>
    public static PCBoostLoggingHost Create(string logDirectory, bool verbose, SensitiveDataRedactor? redactor = null)
    {
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("Le dossier des journaux est requis.", nameof(logDirectory));

        // Le puits fichier crée le dossier lui-même et absorbe ses propres erreurs d'écriture :
        // un dossier inaccessible prive seulement l'application de journal, sans la bloquer.
        var directory = Path.GetFullPath(logDirectory);
        var levelSwitch = new LoggingLevelSwitch(LevelFor(verbose));
        var formatter = new RedactingTextFormatter(
            new MessageTemplateTextFormatter(OutputTemplate, CultureInfo.InvariantCulture),
            redactor ?? SensitiveDataRedactor.ForCurrentUser());

        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.With(new LevelNameEnricher())
            .WriteTo.File(
                formatter,
                Path.Combine(directory, FileNamePattern),
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedFileCount,
                encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .CreateLogger();

        return new PCBoostLoggingHost(logger, levelSwitch, directory);
    }

    internal static LogEventLevel LevelFor(bool verbose) => verbose ? LogEventLevel.Debug : LogEventLevel.Information;
}

/// <summary>
/// Journalisation active : fabrique <see cref="ILoggerFactory"/> et niveau modifiable à chaud.
/// À libérer à la fermeture de l'application (vide les tampons et ferme le fichier).
/// </summary>
public sealed class PCBoostLoggingHost : IDisposable
{
    private readonly Logger _logger;
    private bool _disposed;

    internal PCBoostLoggingHost(Logger logger, LoggingLevelSwitch levelSwitch, string logDirectory)
    {
        _logger = logger;
        LevelSwitch = levelSwitch;
        LogDirectory = logDirectory;
        LoggerFactory = new SerilogLoggerFactory(logger, dispose: false);
    }

    public ILoggerFactory LoggerFactory { get; }

    public LoggingLevelSwitch LevelSwitch { get; }

    public string LogDirectory { get; }

    public bool IsVerbose => LevelSwitch.MinimumLevel <= LogEventLevel.Debug;

    /// <summary>Change le niveau à chaud : Debug (journalisation détaillée) ou Information. Voir <c>SettingsBindings</c>.</summary>
    public void SetVerbose(bool verbose) => LevelSwitch.MinimumLevel = PCBoostLogging.LevelFor(verbose);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LoggerFactory.Dispose();
        _logger.Dispose();
    }
}
