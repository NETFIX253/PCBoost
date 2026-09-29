using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Infrastructure;
using PCBoost.Infrastructure.Localization;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class LocalizerTests
{
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private static Localizer Create(CultureInfo? systemUi = null, CultureInfo? regional = null, ListLogger<Localizer>? logger = null, params IStringResourceSource[] extra)
    {
        var sources = new List<IStringResourceSource>(extra)
        {
            new DictionaryStringSource(
                new() { ["Greeting"] = "Bonjour {0}", ["Size"] = "{0:N1} Go", ["Broken"] = "Valeur {1}", ["Plain"] = "Texte {sans} argument", ["Wrap"] = "« {0} »" },
                new() { ["Greeting"] = "Hello {0}", ["Size"] = "{0:N1} GB", ["Broken"] = "Value {1}", ["Plain"] = "Text {without} argument", ["Wrap"] = "“{0}”" }),
        };
        return new Localizer(sources, (Microsoft.Extensions.Logging.ILogger<Localizer>?)logger ?? NullLogger<Localizer>.Instance, systemUi ?? French, regional ?? French);
    }

    [Fact]
    public void Missing_key_returns_bracketed_key_and_is_logged_once()
    {
        var logger = new ListLogger<Localizer>();
        var localizer = Create(logger: logger);

        Assert.Equal("[Nope]", localizer.Get("Nope"));
        Assert.Equal("[Nope]", localizer.Format("Nope", 1));
        Assert.Equal("[Nope]", localizer.Format(TextRef.Of("Nope", 1)));
        Assert.Single(logger.Entries, e => e.Level == Microsoft.Extensions.Logging.LogLevel.Debug && e.Message.Contains("Nope", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fr", 0, "0 modification appliquée, 0 échec")]
    [InlineData("fr", 1, "1 modification appliquée, 1 échec")]
    [InlineData("fr", 2, "2 modifications appliquées, 2 échecs")]
    [InlineData("en", 0, "0 changes applied, 0 failures")]
    [InlineData("en", 1, "1 change applied, 1 failure")]
    [InlineData("en", 3, "3 changes applied, 3 failures")]
    public void Plural_forms_follow_the_count_and_language_rules(string language, int count, string expected)
    {
        var source = new DictionaryStringSource(
            new() { ["Done"] = "{0} {0:plural:modification appliquée|modifications appliquées}, {1} {1:plural:échec|échecs}" },
            new() { ["Done"] = "{0} {0:plural:change|changes} applied, {1} {1:plural:failure|failures}" });
        var localizer = Create(extra: new IStringResourceSource[] { source });
        localizer.SetLanguage(language);

        Assert.Equal(expected, localizer.Format("Done", count, count));
        Assert.Equal(expected, localizer.Format(TextRef.Of("Done", count, count)));
    }

    [Fact]
    public void First_registered_source_wins()
    {
        var first = new DictionaryStringSource(new() { ["Greeting"] = "Salut {0}" });
        var localizer = Create(extra: new IStringResourceSource[] { first });

        Assert.Equal("Salut Amin", localizer.Format("Greeting", "Amin"));
    }

    [Fact]
    public void Literal_text_is_returned_verbatim_even_with_braces()
    {
        var localizer = Create();

        Assert.Equal("setup{0}.exe", localizer.Format(TextRef.Literal("setup{0}.exe")));
        Assert.Equal(string.Empty, localizer.Format(new TextRef(TextRef.LiteralKey)));
    }

    [Fact]
    public void Arguments_are_formatted_with_the_language_culture()
    {
        var localizer = Create();
        Assert.Equal("1,5 Go", localizer.Format("Size", 1.5).Replace(' ', ' ').Replace(' ', ' '));

        localizer.SetLanguage("en");
        Assert.Equal("1.5 GB", localizer.Format("Size", 1.5));
    }

    [Fact]
    public void Invalid_format_falls_back_to_the_raw_template_without_throwing()
    {
        var localizer = Create();

        Assert.Equal("Valeur {1}", localizer.Format("Broken", 1));
        Assert.Equal("Texte {sans} argument", localizer.Format("Plain"));
        Assert.Equal("Texte {sans} argument", localizer.Format("Plain", 1));
    }

    [Fact]
    public void Json_element_arguments_are_converted_to_values()
    {
        var localizer = Create();
        using var document = JsonDocument.Parse("[1.26, \"Amin\", true, 7]");
        var items = document.RootElement.EnumerateArray().ToArray();

        Assert.Equal("1,3 Go", localizer.Format(TextRef.Of("Size", items[0])).Replace(' ', ' '));
        Assert.Equal("Bonjour Amin", localizer.Format(TextRef.Of("Greeting", items[1])));
        Assert.Equal("Bonjour True", localizer.Format(TextRef.Of("Greeting", items[2])));
        Assert.Equal("Bonjour 7", localizer.Format("Greeting", items[3]));
    }

    [Fact]
    public void Nested_text_arguments_are_localized()
    {
        var localizer = Create();

        Assert.Equal("« Bonjour Amin »", localizer.Format(TextRef.Of("Wrap", TextRef.Of("Greeting", "Amin"))));
    }

    [Fact]
    public void Changing_language_raises_the_event_only_on_actual_change()
    {
        var localizer = Create();
        var raised = 0;
        localizer.LanguageChanged += (_, _) => raised++;

        localizer.SetLanguage("en");
        localizer.SetLanguage("EN-gb");
        Assert.Equal("Hello Amin", localizer.Format("Greeting", "Amin"));
        localizer.SetLanguage("fr");

        Assert.Equal(2, raised);
        Assert.Equal("fr", localizer.LanguageCode);
        Assert.Equal("Bonjour Amin", localizer.Format("Greeting", "Amin"));
    }

    [Fact]
    public void System_language_follows_the_windows_interface_language()
    {
        var onFrench = Create(systemUi: CultureInfo.GetCultureInfo("fr-CA"), regional: CultureInfo.GetCultureInfo("fr-CA"));
        var onGerman = Create(systemUi: German, regional: German);
        var onEnglish = Create(systemUi: English, regional: CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal("fr-CA", onFrench.Culture.Name);
        Assert.Equal("en-US", onGerman.Culture.Name);
        Assert.Equal("en-GB", onEnglish.Culture.Name);
        Assert.Equal("Hello Amin", onGerman.Format("Greeting", "Amin"));
        Assert.Equal("system", onGerman.LanguageCode);
    }

    [Fact]
    public void Explicit_french_on_a_non_french_system_uses_fr_FR()
    {
        var localizer = Create(systemUi: German, regional: German);

        localizer.SetLanguage("fr");

        Assert.Equal("fr-FR", localizer.Culture.Name);
        Assert.Equal("Bonjour Amin", localizer.Format("Greeting", "Amin"));
    }

    [Theory]
    [InlineData(null, "system")]
    [InlineData("", "system")]
    [InlineData("de", "system")]
    [InlineData("SYSTEM", "system")]
    [InlineData("fr-BE", "fr")]
    [InlineData("en_US", "en")]
    public void Language_codes_are_normalized(string? code, string expected)
        => Assert.Equal(expected, SupportedLanguages.Normalize(code));

    [Fact]
    public void Common_resources_are_available_in_both_languages()
    {
        var localizer = new Localizer([ServiceCollectionExtensions.CreateCommonStringSource()], NullLogger<Localizer>.Instance, French, French);

        Assert.Equal("Non disponible", localizer.Get("Common_NotAvailable"));
        Assert.Equal("Windows a refusé cette opération car elle nécessite des privilèges supplémentaires.", localizer.Get("Error_AccessDenied"));
        Assert.Equal("L'autorisation administrateur a été refusée. Aucune modification n'a été faite.", localizer.Get("Error_ElevationCancelled"));
        Assert.Equal("Le fichier est utilisé par une autre application.", localizer.Get("Error_InUse"));
        Assert.Equal(3, localizer.AvailableLanguages.Count);
        Assert.Equal("Langue du système", localizer.AvailableLanguages[0].NativeName);

        localizer.SetLanguage("en");
        Assert.Equal("Not available", localizer.Get("Common_NotAvailable"));
        Assert.Equal("System language", localizer.AvailableLanguages[0].NativeName);
    }

    [Fact]
    public void Every_error_kind_has_a_human_message_in_both_languages()
    {
        var localizer = new Localizer([ServiceCollectionExtensions.CreateCommonStringSource()], NullLogger<Localizer>.Instance, French, French);
        foreach (var language in new[] { "fr", "en" })
        {
            localizer.SetLanguage(language);
            foreach (var kind in Enum.GetValues<OperationErrorKind>())
            {
                var text = localizer.FormatError(kind);
                Assert.False(text.StartsWith('['), $"Error_{kind} ({language})");
                Assert.False(string.IsNullOrWhiteSpace(text));
            }
        }
    }

    [Fact]
    public void Formatting_helpers_show_not_available_for_missing_values()
    {
        var localizer = new Localizer([ServiceCollectionExtensions.CreateCommonStringSource()], NullLogger<Localizer>.Instance, French, French);
        string Normalize(string s) => s.Replace(' ', ' ').Replace(' ', ' ');

        Assert.Equal("Non disponible", localizer.FormatBytes(null));
        Assert.Equal("512 o", Normalize(localizer.FormatBytes(512)));
        Assert.Equal("1,5 Go", Normalize(localizer.FormatBytes(1536L * 1024 * 1024)));
        Assert.Equal("120 Go", Normalize(localizer.FormatBytes(120L * 1024 * 1024 * 1024)));
        Assert.Equal("45 %", Normalize(localizer.FormatPercent(45.2)));
        Assert.Equal("Non disponible", localizer.FormatPercent(double.NaN));
        Assert.Equal("Non disponible", localizer.FormatTemperature(SensorReading.Unavailable(Availability.NoSensor)));
        Assert.Equal("62 °C", Normalize(localizer.FormatTemperature(SensorReading.Of(61.6, "test"))));
        Assert.Equal("45 s", Normalize(localizer.FormatDuration(TimeSpan.FromSeconds(45))));
        Assert.Equal("3 min 05 s", Normalize(localizer.FormatDuration(TimeSpan.FromSeconds(185))));
        Assert.Equal("2 h 07 min", Normalize(localizer.FormatDuration(new TimeSpan(2, 7, 0))));
        Assert.Equal("3 j 4 h", Normalize(localizer.FormatDuration(new TimeSpan(3, 4, 0, 0))));
        Assert.Equal("Oui", localizer.FormatYesNo(true));
        Assert.Equal("L'opération a été annulée.", localizer.FormatResult(OperationResult.Fail(OperationErrorKind.Cancelled)));

        localizer.SetLanguage("en");
        Assert.Equal("1.5 GB", Normalize(localizer.FormatBytes(1536L * 1024 * 1024)));
        Assert.Equal("45%", localizer.FormatPercent(45.2));
    }
}
