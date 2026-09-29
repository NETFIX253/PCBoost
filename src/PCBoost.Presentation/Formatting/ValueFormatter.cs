using System.Globalization;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Presentation.Abstractions;

namespace PCBoost.Presentation.Formatting;

/// <summary>
/// Formatage localisé selon la culture de <see cref="ILocalizer"/>. Toute valeur absente est rendue
/// « Non disponible » (clé <c>Common_NotAvailable</c>), jamais estimée.
/// </summary>
public sealed class ValueFormatter : IValueFormatter
{
    private readonly ILocalizer _localizer;
    private readonly IClock _clock;

    public ValueFormatter(ILocalizer localizer, IClock? clock = null)
    {
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _clock = clock ?? new SystemClock();
    }

    /// <summary>Fuseau utilisé pour l'affichage des dates (heure locale ; remplaçable en test).</summary>
    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Local;

    private CultureInfo Culture => _localizer.Culture;

    public string NotAvailable => _localizer.Get("Common_NotAvailable");

    public string Bytes(long? bytes)
    {
        if (bytes is not { } value) return NotAvailable;
        var sign = value < 0 ? "-" : string.Empty;
        var abs = value == long.MinValue ? long.MaxValue : Math.Abs(value);

        const double tb = 1024d * ByteSize.GiB;
        string text;
        if (abs >= tb) text = F("Common_Format_TB", OneDecimal(abs / tb));
        else if (abs >= ByteSize.GiB) text = F("Common_Format_GB", OneDecimal(abs / (double)ByteSize.GiB));
        else if (abs >= ByteSize.MiB) text = F("Common_Format_MB", N(Math.Round(abs / (double)ByteSize.MiB), 0));
        else if (abs >= ByteSize.KiB) text = F("Common_Format_KB", N(Math.Round(abs / (double)ByteSize.KiB), 0));
        else text = F("Common_Format_Bytes", N(abs, 0));
        return sign + text;
    }

    public string Percent(double? value, int decimals = 0)
        => value is { } v && double.IsFinite(v) ? F("Common_Format_Percent", N(v, decimals)) : NotAvailable;

    public string Temperature(double? celsius)
        => celsius is { } v && double.IsFinite(v) ? F("Common_Format_Temperature", N(v, 0)) : NotAvailable;

    public string Duration(TimeSpan? duration)
    {
        if (duration is not { } d) return NotAvailable;
        if (d < TimeSpan.Zero) d = d.Negate();
        if (d.TotalSeconds < 1 && d > TimeSpan.Zero) return F("Common_Format_Milliseconds", N(d.TotalMilliseconds, 0));
        if (d.TotalMinutes < 1) return F("Common_Format_Seconds", N(Math.Floor(d.TotalSeconds), 0));
        if (d.TotalHours < 1) return F("Common_Format_Minutes", N(Math.Floor(d.TotalMinutes), 0));
        if (d.TotalDays < 1) return F("Common_Format_HoursMinutes", N(Math.Floor(d.TotalHours), 0), d.Minutes.ToString("00", Culture));
        return F("Common_Format_DaysHours", N(Math.Floor(d.TotalDays), 0), N(d.Hours, 0));
    }

    public string DateTime(DateTimeOffset? value)
    {
        if (value is not { } v) return NotAvailable;
        var now = _clock.UtcNow;
        var delta = now - v;
        if (delta < TimeSpan.Zero) return DateTimeFull(v);
        if (delta.TotalMinutes < 1) return _localizer.Get("Common_Format_JustNow");
        if (delta.TotalHours < 1) return F("Common_Format_MinutesAgo", N(Math.Floor(delta.TotalMinutes), 0));

        var local = ToLocal(v);
        var localNow = ToLocal(now);
        if (local.Date == localNow.Date) return F("Common_Format_HoursAgo", N(Math.Floor(delta.TotalHours), 0));
        if (local.Date == localNow.Date.AddDays(-1)) return F("Common_Format_YesterdayAt", local.ToString("t", Culture));
        return DateTimeFull(v);
    }

    public string DateTimeFull(DateTimeOffset? value)
        => value is { } v ? ToLocal(v).ToString("g", Culture) : NotAvailable;

    public string Time(DateTimeOffset? value)
        => value is { } v ? ToLocal(v).ToString("T", Culture) : NotAvailable;

    public string Number(double? value, int decimals = 0)
        => value is { } v && double.IsFinite(v) ? N(v, decimals) : NotAvailable;

    public string Rate(double? bytesPerSecond)
    {
        if (bytesPerSecond is not { } v || !double.IsFinite(v)) return NotAvailable;
        var bytes = (long)Math.Round(Math.Max(0, v));
        string size;
        if (bytes >= ByteSize.MiB) size = F("Common_Format_MB", OneDecimal(bytes / (double)ByteSize.MiB));
        else size = Bytes(bytes);
        return F("Common_Format_Rate", size);
    }

    public string Frequency(double? megahertz)
    {
        if (megahertz is not { } v || !double.IsFinite(v) || v <= 0) return NotAvailable;
        return v >= 1000 ? F("Common_Format_GHz", OneDecimal(v / 1000d)) : F("Common_Format_MHz", N(v, 0));
    }

    public string Fps(double? fps)
        => fps is { } v && double.IsFinite(v) ? F("Common_Format_Fps", N(v, 0)) : NotAvailable;

    public string Milliseconds(double? milliseconds)
        => milliseconds is { } v && double.IsFinite(v) ? F("Common_Format_Milliseconds", N(v, 1)) : NotAvailable;

    /// <summary>Une décimale au plus, sans zéro inutile (« 9,8 », « 16 »).</summary>
    private string OneDecimal(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero).ToString("#,##0.#", Culture);

    private string N(double value, int decimals) => value.ToString("N" + Math.Clamp(decimals, 0, 6).ToString(CultureInfo.InvariantCulture), Culture);

    private string F(string key, params object[] args) => _localizer.Format(key, args);

    private DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, TimeZone);
}
