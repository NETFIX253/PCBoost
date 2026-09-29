using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Presentation;
using PCBoost.Presentation.Abstractions;
using PCBoost.Presentation.Common;
using PCBoost.Presentation.Formatting;
using PCBoost.TestUtilities;

namespace PCBoost.Presentation.Tests.Infrastructure;

/// <summary>
/// Localiseur de test : ressources réelles de PCBoost.Presentation + clés fournies par d'autres modules
/// (Common_NotAvailable, Error_*). Renvoie « [clé] » si absente, comme le localiseur de l'application.
/// </summary>
public sealed class TestLocalizer : ILocalizer
{
    private static readonly Dictionary<string, (string Fr, string En)> External = new(StringComparer.Ordinal)
    {
        ["Common_NotAvailable"] = ("Non disponible", "Not available"),
        ["Error_Failed"] = ("L'opération a échoué.", "The operation failed."),
        ["Error_AccessDenied"] = ("Accès refusé.", "Access denied."),
        ["Error_ElevationCancelled"] = ("Autorisation refusée.", "Permission declined."),
        ["Error_InUse"] = ("Fichier utilisé.", "File in use."),
        ["Error_Blocked"] = ("Action bloquée.", "Action blocked."),
    };

    private readonly IStringResourceSource _source = ResourceManagerStringSource.ForAssembly(
        typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Presentation.Resources.Strings");

    public TestLocalizer(string culture = "fr-FR") => Culture = CultureInfo.GetCultureInfo(culture);

    public CultureInfo Culture { get; private set; }

    public IReadOnlyList<LanguageOption> AvailableLanguages { get; } = [new("fr", "Français"), new("en", "English")];

    public event EventHandler? LanguageChanged;

    public List<string> Requested { get; } = [];

    public string Get(string key)
    {
        Requested.Add(key);
        if (External.TryGetValue(key, out var ext)) return Culture.TwoLetterISOLanguageName == "en" ? ext.En : ext.Fr;
        return _source.GetString(key, Culture) ?? $"[{key}]";
    }

    public string Format(string key, params object[] args) => string.Format(Culture, PluralRules.Resolve(Get(key), args, Culture), args);

    public string Format(TextRef text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.IsLiteral ? Convert.ToString(text.Args[0], Culture) ?? string.Empty : Format(text.Key, text.Args);
    }

    public void SetLanguage(string languageCode)
    {
        Culture = CultureInfo.GetCultureInfo(languageCode == "en" ? "en-US" : "fr-FR");
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class ImmediateDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;

    public void Post(Action action) => action();

    public Task InvokeAsync(Func<Task> action) => action();
}

public sealed class FakeNavigationService : INavigationService
{
    public List<(string Key, object? Parameter)> Navigations { get; } = [];

    public string? CurrentPageKey { get; private set; }

    public bool CanGoBack => Navigations.Count > 1;

    public event EventHandler<string>? Navigated;

    public bool Navigate(string pageKey, object? parameter = null)
    {
        Navigations.Add((pageKey, parameter));
        CurrentPageKey = pageKey;
        Navigated?.Invoke(this, pageKey);
        return true;
    }

    public void GoBack()
    {
    }
}

public sealed class FakeDialogService : IDialogService
{
    public DialogResultKind NextResult { get; set; } = DialogResultKind.Primary;

    public List<ConfirmationRequest> Confirmations { get; } = [];

    public List<(TextRef Title, TextRef Message)> Messages { get; } = [];

    public List<OperationResult> Errors { get; } = [];

    public Task<DialogResultKind> ConfirmAsync(ConfirmationRequest request)
    {
        Confirmations.Add(request);
        return Task.FromResult(NextResult);
    }

    public Task ShowErrorAsync(OperationResult result, TextRef? context = null)
    {
        Errors.Add(result);
        return Task.CompletedTask;
    }

    public Task ShowMessageAsync(TextRef title, TextRef message)
    {
        Messages.Add((title, message));
        return Task.CompletedTask;
    }
}

/// <summary>Contexte complet de ViewModel pour les tests (fr-FR, UTC, horloge fixe).</summary>
public sealed class TestUi
{
    public TestUi(string culture = "fr-FR")
    {
        Localizer = new TestLocalizer(culture);
        Clock = new FakeClock();
        Formatter = new ValueFormatter(Localizer, Clock) { TimeZone = TimeZoneInfo.Utc };
        Context = new ViewModelContext(Localizer, Formatter, Dispatcher, Navigation, Dialogs, NullLoggerFactory.Instance, Clock);
    }

    public TestLocalizer Localizer { get; }

    public FakeClock Clock { get; }

    public ValueFormatter Formatter { get; }

    public ImmediateDispatcher Dispatcher { get; } = new();

    public FakeNavigationService Navigation { get; } = new();

    public FakeDialogService Dialogs { get; } = new();

    public ViewModelContext Context { get; }
}
