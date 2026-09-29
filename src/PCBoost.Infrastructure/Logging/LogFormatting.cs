using PCBoost.Core.Privacy;
using System.Globalization;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace PCBoost.Infrastructure.Logging;

/// <summary>Applique <see cref="SensitiveDataRedactor"/> à la ligne complète (message, propriétés, exception et pile d'appels).</summary>
internal sealed class RedactingTextFormatter : ITextFormatter
{
    private readonly ITextFormatter _inner;
    private readonly SensitiveDataRedactor _redactor;

    public RedactingTextFormatter(ITextFormatter inner, SensitiveDataRedactor redactor)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new StringWriter(CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(_redactor.Redact(buffer.ToString()));
    }
}

/// <summary>Nom de niveau lisible aligné sur Microsoft.Extensions.Logging (Fatal → CRITICAL).</summary>
internal sealed class LevelNameEnricher : ILogEventEnricher
{
    public const string PropertyName = "LevelName";

    private static readonly Dictionary<LogEventLevel, LogEventProperty> Properties = new()
    {
        [LogEventLevel.Verbose] = new(PropertyName, new ScalarValue("TRACE")),
        [LogEventLevel.Debug] = new(PropertyName, new ScalarValue("DEBUG")),
        [LogEventLevel.Information] = new(PropertyName, new ScalarValue("INFO")),
        [LogEventLevel.Warning] = new(PropertyName, new ScalarValue("WARNING")),
        [LogEventLevel.Error] = new(PropertyName, new ScalarValue("ERROR")),
        [LogEventLevel.Fatal] = new(PropertyName, new ScalarValue("CRITICAL")),
    };

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        ArgumentNullException.ThrowIfNull(logEvent);
        if (Properties.TryGetValue(logEvent.Level, out var property)) logEvent.AddOrUpdateProperty(property);
    }
}
