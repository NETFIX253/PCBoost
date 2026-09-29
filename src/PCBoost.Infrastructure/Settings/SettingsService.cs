using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.Infrastructure.Settings;

/// <summary>
/// Préférences utilisateur stockées localement en JSON (clé « settings.app »). Valeurs par défaut au premier lancement,
/// migration de schéma via <see cref="AppSettings.SchemaVersion"/>, normalisation à chaque lecture et écriture.
/// Thread-safe. <see cref="Current"/> est un instantané à traiter en lecture seule : pour modifier, cloner
/// (<see cref="AppSettings.Clone"/>) puis appeler <see cref="SaveAsync"/>.
/// Un échec de lecture ou d'écriture du stockage ne bloque jamais l'application (journalisé, valeurs en mémoire conservées).
/// </summary>
public sealed class SettingsService : ISettingsService
{
    public const string StorageKey = "settings.app";

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    /// <summary>
    /// Étapes de migration du JSON stocké : clé = version de départ. Une version absente de la table n'a pas changé
    /// la forme des données (seul le numéro de version est mis à jour).
    /// </summary>
    private static readonly IReadOnlyDictionary<int, Action<JsonObject>> Migrations = new Dictionary<int, Action<JsonObject>>();

    private readonly IKeyValueStore _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AppSettings _current = CreateDefaults();

    public SettingsService(IKeyValueStore store, ILogger<SettingsService> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AppSettings Current => Volatile.Read(ref _current);

    /// <summary>Les préférences ont-elles été lues depuis le stockage ?</summary>
    public bool IsLoaded { get; private set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        AppSettings loaded;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            loaded = await ReadAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _current, loaded);
            IsLoaded = true;
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged(loaded);
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        var corrections = AppSettingsNormalizer.Normalize(copy);
        if (corrections.Count > 0)
            _logger.LogDebug("Préférences corrigées avant enregistrement : {Fields}.", string.Join(", ", corrections));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _current, copy);
            await PersistAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
        RaiseChanged(copy);
    }

    private async Task<AppSettings> ReadAsync(CancellationToken cancellationToken)
    {
        JsonObject? raw;
        try
        {
            raw = await _store.GetAsync<JsonObject>(StorageKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stockage illisible : valeurs par défaut pour cette session, sans écraser ce qui est stocké.
            _logger.LogError(ex, "Impossible de lire les préférences ; les valeurs par défaut sont utilisées pour cette session.");
            return CreateDefaults();
        }

        if (raw is null)
        {
            _logger.LogInformation("Premier lancement : préférences par défaut créées.");
            var defaults = CreateDefaults();
            await PersistAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        var storedVersion = ReadSchemaVersion(raw);
        if (storedVersion > AppSettings.CurrentSchemaVersion)
        {
            _logger.LogWarning(
                "Préférences enregistrées par une version plus récente (schéma {StoredVersion}, connu : {KnownVersion}) : seuls les réglages connus sont conservés.",
                storedVersion, AppSettings.CurrentSchemaVersion);
        }
        Migrate(raw, storedVersion);

        AppSettings? settings;
        try
        {
            settings = raw.Deserialize<AppSettings>(JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Préférences enregistrées illisibles : les valeurs par défaut sont rétablies.");
            settings = null;
        }

        if (settings is null)
        {
            var defaults = CreateDefaults();
            await PersistAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }

        var corrections = AppSettingsNormalizer.Normalize(settings);
        if (corrections.Count > 0 || storedVersion != AppSettings.CurrentSchemaVersion)
        {
            if (corrections.Count > 0)
                _logger.LogInformation("Préférences enregistrées corrigées : {Fields}.", string.Join(", ", corrections));
            await PersistAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        return settings;
    }

    private void Migrate(JsonObject raw, int storedVersion)
    {
        for (var version = Math.Max(0, storedVersion); version < AppSettings.CurrentSchemaVersion; version++)
        {
            if (Migrations.TryGetValue(version, out var migrate)) migrate(raw);
            SetSchemaVersion(raw, version + 1);
            _logger.LogInformation("Préférences migrées vers le schéma {Version}.", version + 1);
        }
    }

    private static int ReadSchemaVersion(JsonObject raw)
    {
        foreach (var (name, value) in raw)
        {
            if (!string.Equals(name, nameof(AppSettings.SchemaVersion), StringComparison.OrdinalIgnoreCase)) continue;
            return value is JsonValue v && v.TryGetValue<int>(out var version) ? version : 0;
        }
        return 0;
    }

    private static void SetSchemaVersion(JsonObject raw, int version)
    {
        var existing = raw.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, nameof(AppSettings.SchemaVersion), StringComparison.OrdinalIgnoreCase));
        if (existing is not null) raw.Remove(existing);
        raw[nameof(AppSettings.SchemaVersion)] = version;
    }

    private async Task PersistAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            await _store.SetAsync(StorageKey, settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Impossible d'enregistrer les préférences ; elles restent appliquées pour cette session.");
        }
    }

    private void RaiseChanged(AppSettings settings)
    {
        var handlers = SettingsChanged;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AppSettings>>())
        {
            try
            {
                handler(this, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Un abonné au changement de préférences a échoué.");
            }
        }
    }

    private static AppSettings CreateDefaults()
    {
        var settings = new AppSettings();
        AppSettingsNormalizer.Normalize(settings);
        return settings;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.General) { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
