using PCBoost.Core.Localization;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;
using PCBoost.Infrastructure.Logging;

namespace PCBoost.Infrastructure.Settings;

/// <summary>
/// Applique les préférences transverses (langue, journalisation détaillée) maintenant et à chaque changement.
/// À appeler par l'hôte après <see cref="ISettingsService.LoadAsync"/> ; libérer le résultat à la fermeture.
/// </summary>
public static class SettingsBindings
{
    public static IDisposable Bind(ISettingsService settings, ILocalizer localizer, PCBoostLoggingHost? logging = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(localizer);

        void Apply(AppSettings current)
        {
            localizer.SetLanguage(current.Language);
            logging?.SetVerbose(current.VerboseLogging);
        }

        void OnChanged(object? sender, AppSettings current) => Apply(current);

        Apply(settings.Current);
        settings.SettingsChanged += OnChanged;
        return new Binding(() => settings.SettingsChanged -= OnChanged);
    }

    private sealed class Binding(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
