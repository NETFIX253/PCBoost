using Microsoft.Extensions.Logging;
using PCBoost.Infrastructure.Logging;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class LoggingTests
{
    private static readonly SensitiveDataRedactor Redactor = new(@"C:\Users\Amin", "Amin", "DESKTOP-42");

    [Theory]
    [InlineData(@"Lecture de C:\Users\Amin\AppData\Local\Temp\a.tmp", @"Lecture de %USERPROFILE%\AppData\Local\Temp\a.tmp")]
    [InlineData(@"c:\users\AMIN\Documents", @"%USERPROFILE%\Documents")]
    [InlineData("C:/Users/Amin/Downloads/x.zip", "%USERPROFILE%/Downloads/x.zip")]
    [InlineData(@"{""path"":""C:\\Users\\Amin\\x""}", @"{""path"":""%USERPROFILE%\\x""}")]
    [InlineData(@"C:\Users\Amin", "%USERPROFILE%")]
    [InlineData(@"C:\Users\Aminata\file", @"C:\Users\Aminata\file")]
    [InlineData("Session de Amin sur DESKTOP-42", "Session de <user> sur <machine>")]
    [InlineData(@"Compte DESKTOP-42\amin", @"Compte <machine>\<user>")]
    [InlineData("Aminata et Aminou ne sont pas concernés", "Aminata et Aminou ne sont pas concernés")]
    [InlineData("", "")]
    public void Personal_data_is_masked(string input, string expected)
        => Assert.Equal(expected, Redactor.Redact(input));

    [Fact]
    public void Short_or_missing_identities_are_ignored()
    {
        var redactor = new SensitiveDataRedactor(null, "a", "");

        Assert.Equal("a b c", redactor.Redact("a b c"));
        Assert.Equal(string.Empty, redactor.Redact(null));
    }

    [Fact]
    public void Linux_style_profile_is_also_masked()
        => Assert.Equal("%USERPROFILE%/.config", new SensitiveDataRedactor("/home/amin", null, null).Redact("/home/amin/.config"));

    [Fact]
    public void Log_file_contains_masked_messages_and_exceptions()
    {
        using var temp = new TempDirectory();
        using (var logging = PCBoostLogging.Create(temp.Path, verbose: false, Redactor))
        {
            var logger = logging.LoggerFactory.CreateLogger("PCBoost.Tests");
            logger.LogInformation("Fichier {Path} analysé", @"C:\Users\Amin\AppData\Local\Temp\setup.tmp");
            logger.LogError(new UnauthorizedAccessException(@"Access to the path 'C:\Users\Amin\secret.txt' is denied."), "Échec pour Amin sur DESKTOP-42");
            logger.LogCritical("Arrêt critique");
        }

        var file = Assert.Single(Directory.GetFiles(temp.Path, "pcboost-*.log"));
        var content = File.ReadAllText(file);

        Assert.Contains(@"%USERPROFILE%\AppData\Local\Temp\setup.tmp", content);
        Assert.Contains(@"%USERPROFILE%\secret.txt", content);
        Assert.Contains("Échec pour <user> sur <machine>", content);
        Assert.Contains("UnauthorizedAccessException", content);
        Assert.Contains("[INFO]", content);
        Assert.Contains("[ERROR]", content);
        Assert.Contains("[CRITICAL]", content);
        Assert.DoesNotContain("Amin", content);
        Assert.DoesNotContain("DESKTOP-42", content);
    }

    [Fact]
    public void Level_can_be_changed_at_runtime()
    {
        using var temp = new TempDirectory();
        using (var logging = PCBoostLogging.Create(temp.Path, verbose: false, Redactor))
        {
            var logger = logging.LoggerFactory.CreateLogger("PCBoost.Tests");
            Assert.False(logging.IsVerbose);
            logger.LogDebug("debug-avant");

            logging.SetVerbose(true);
            Assert.True(logging.IsVerbose);
            logger.LogDebug("debug-apres");

            logging.SetVerbose(false);
            logger.LogDebug("debug-final");
        }

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(temp.Path, "pcboost-*.log")));
        Assert.DoesNotContain("debug-avant", content);
        Assert.Contains("[DEBUG]", content);
        Assert.Contains("debug-apres", content);
        Assert.DoesNotContain("debug-final", content);
    }

    [Fact]
    public void Framework_noise_is_limited_to_warnings()
    {
        using var temp = new TempDirectory();
        using (var logging = PCBoostLogging.Create(temp.Path, verbose: true, Redactor))
        {
            logging.LoggerFactory.CreateLogger("Microsoft.Extensions.Hosting").LogInformation("bruit-framework");
            logging.LoggerFactory.CreateLogger("Microsoft.Extensions.Hosting").LogWarning("alerte-framework");
        }

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(temp.Path, "pcboost-*.log")));
        Assert.DoesNotContain("bruit-framework", content);
        Assert.Contains("alerte-framework", content);
    }

    [Fact]
    public void Logging_constants_match_the_retention_policy()
    {
        Assert.Equal("pcboost-.log", PCBoostLogging.FileNamePattern);
        Assert.Equal(7, PCBoostLogging.RetainedFileCount);
        Assert.Equal(10L * 1024 * 1024, PCBoostLogging.FileSizeLimitBytes);
    }
}
