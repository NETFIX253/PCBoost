using PCBoost.Core.Common;
using PCBoost.Presentation.Formatting;
using PCBoost.Presentation.Tests.Infrastructure;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests;

public sealed class ValueFormatterTests
{
    private const string Nb = "\u00a0";
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero));

    private ValueFormatter Fr() => new(new TestLocalizer("fr-FR"), _clock) { TimeZone = TimeZoneInfo.Utc };

    private ValueFormatter En() => new(new TestLocalizer("en-US"), _clock) { TimeZone = TimeZoneInfo.Utc };

    [Theory]
    [InlineData(0L, "0" + Nb + "o")]
    [InlineData(512L, "512" + Nb + "o")]
    [InlineData(1536L, "2" + Nb + "Ko")]
    [InlineData(5L * 1024 * 1024, "5" + Nb + "Mo")]
    [InlineData(1024L * 1024 * 1024, "1" + Nb + "Go")]
    public void Bytes_UsesFrenchUnits_WithOneDecimalFromGigabytes(long bytes, string expected)
        => Assert.Equal(expected, Fr().Bytes(bytes));

    [Fact]
    public void Bytes_NineCommaEightGigabytes()
        => Assert.Equal("9,8" + Nb + "Go", Fr().Bytes((long)(9.8 * ByteSize.GiB)));

    [Fact]
    public void Bytes_Terabytes_AndEnglishUnits()
    {
        Assert.Equal("2" + Nb + "To", Fr().Bytes(2L * 1024 * ByteSize.GiB));
        Assert.Equal("16" + Nb + "Go", Fr().Bytes(16L * ByteSize.GiB));
        Assert.Equal("9.8" + Nb + "GB", En().Bytes((long)(9.8 * ByteSize.GiB)));
        Assert.Equal("300" + Nb + "MB", En().Bytes(300L * ByteSize.MiB));
    }

    [Fact]
    public void NullValues_AreNotAvailable_NeverEstimated()
    {
        var f = Fr();
        Assert.Equal("Non disponible", f.Bytes(null));
        Assert.Equal("Non disponible", f.Percent(null));
        Assert.Equal("Non disponible", f.Temperature(null));
        Assert.Equal("Non disponible", f.Duration(null));
        Assert.Equal("Non disponible", f.DateTime(null));
        Assert.Equal("Non disponible", f.Rate(null));
        Assert.Equal("Non disponible", f.Fps(null));
        Assert.Equal("Non disponible", f.Percent(double.NaN));
        Assert.Equal("Not available", En().Temperature(null));
    }

    [Fact]
    public void Percent_And_Temperature()
    {
        Assert.Equal("34" + Nb + "%", Fr().Percent(34.4));
        Assert.Equal("34,4" + Nb + "%", Fr().Percent(34.44, 1));
        Assert.Equal("34%", En().Percent(34.4));
        Assert.Equal("62" + Nb + "°C", Fr().Temperature(62.3));
    }

    [Theory]
    [InlineData(45, "45" + Nb + "s")]
    [InlineData(12 * 60 + 30, "12" + Nb + "min")]
    [InlineData(3 * 3600 + 5 * 60, "3" + Nb + "h" + Nb + "05" + Nb + "min")]
    [InlineData(4 * 86400 + 2 * 3600, "4" + Nb + "j" + Nb + "2" + Nb + "h")]
    public void Duration_IsReadable(int seconds, string expected)
        => Assert.Equal(expected, Fr().Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DateTime_IsRelativeAndShort()
    {
        var f = Fr();
        var now = _clock.UtcNow;
        Assert.Equal("à l'instant", f.DateTime(now.AddSeconds(-20)));
        Assert.Equal("il y a 5" + Nb + "min", f.DateTime(now.AddMinutes(-5)));
        Assert.Equal("il y a 3" + Nb + "h", f.DateTime(now.AddHours(-3)));
        Assert.Equal("hier à 18:30", f.DateTime(new DateTimeOffset(2026, 9, 27, 18, 30, 0, TimeSpan.Zero)));
        Assert.Equal("20/09/2026 08:15", f.DateTime(new DateTimeOffset(2026, 9, 20, 8, 15, 0, TimeSpan.Zero)));
        Assert.Equal("5" + Nb + "min ago", En().DateTime(now.AddMinutes(-5)));
    }

    [Fact]
    public void Rate_Frequency_Fps_Milliseconds()
    {
        var f = Fr();
        Assert.Equal("1,5" + Nb + "Mo/s", f.Rate(1.5 * ByteSize.MiB));
        Assert.Equal("512" + Nb + "Ko/s", f.Rate(512 * 1024));
        Assert.Equal("3,6" + Nb + "GHz", f.Frequency(3600));
        Assert.Equal("800" + Nb + "MHz", f.Frequency(800));
        Assert.Equal("60" + Nb + "FPS", f.Fps(59.6));
        Assert.Equal("16,7" + Nb + "ms", f.Milliseconds(16.66));
    }
}
