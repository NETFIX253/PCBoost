using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Settings;
using PCBoost.Infrastructure.Settings;
using PCBoost.Persistence.Repositories;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class SettingsServiceTests
{
    private static SettingsService Create(IKeyValueStore store) => new(store, NullLogger<SettingsService>.Instance);

    [Fact]
    public async Task First_launch_uses_and_persists_defaults()
    {
        var store = new InMemoryKeyValueStore();
        var service = Create(store);
        AppSettings? notified = null;
        service.SettingsChanged += (_, s) => notified = s;

        await service.LoadAsync();

        Assert.True(service.IsLoaded);
        Assert.Equal("system", service.Current.Language);
        Assert.Equal(90, service.Current.HistoryRetentionDays);
        Assert.False(service.Current.TelemetryEnabled);
        Assert.True(service.Current.ConfirmSensitiveOperations);
        Assert.Same(service.Current, notified);
        Assert.NotNull(await store.GetAsync<AppSettings>(SettingsService.StorageKey));
    }

    [Fact]
    public async Task Save_normalizes_values_and_notifies_with_the_normalized_copy()
    {
        var store = new InMemoryKeyValueStore();
        var service = Create(store);
        await service.LoadAsync();
        AppSettings? notified = null;
        service.SettingsChanged += (_, s) => notified = s;

        var edited = service.Current.Clone();
        edited.Language = "de";
        edited.HistoryRetentionDays = 1000;
        edited.TelemetryEnabled = true;
        edited.Thresholds.RamWarningPercent = 95;
        edited.Thresholds.RamCriticalPercent = 90;
        edited.Thresholds.SystemDriveFreeWarningPercent = 5;
        edited.Thresholds.SystemDriveFreeCriticalPercent = 12;
        edited.Thresholds.CpuTemperatureWarningC = 500;
        edited.Thresholds.StartupWarningCount = 12;
        edited.Thresholds.StartupCriticalCount = 20;
        edited.Gaming.BackgroundExclusions = ["Discord", "discord.exe", " ", "OBS64.EXE"];
        edited.Gaming.CustomGames = [new() { Name = "", ExecutablePath = @"C:\Games\Jeu.exe" }, new() { Name = "x", ExecutablePath = "readme.txt" }];
        edited.DismissedRecommendations = ["a", "a", " ", "b"];

        await service.SaveAsync(edited);
        var current = service.Current;

        Assert.Equal("system", current.Language);
        Assert.Equal(365, current.HistoryRetentionDays);
        Assert.False(current.TelemetryEnabled);
        Assert.Equal(80, current.Thresholds.RamWarningPercent);
        Assert.Equal(90, current.Thresholds.RamCriticalPercent);
        Assert.Equal(15, current.Thresholds.SystemDriveFreeWarningPercent);
        Assert.Equal(10, current.Thresholds.SystemDriveFreeCriticalPercent);
        Assert.Equal(110, current.Thresholds.CpuTemperatureWarningC);
        Assert.Equal(12, current.Thresholds.StartupWarningCount);
        Assert.Equal(20, current.Thresholds.StartupCriticalCount);
        Assert.Equal(new[] { "discord.exe", "obs64.exe" }, current.Gaming.BackgroundExclusions);
        var game = Assert.Single(current.Gaming.CustomGames);
        Assert.Equal("Jeu", game.Name);
        Assert.Equal(new[] { "a", "b" }, current.DismissedRecommendations);
        Assert.Same(current, notified);
        Assert.NotSame(edited, current);
        Assert.True(edited.TelemetryEnabled);

        var persisted = await store.GetAsync<AppSettings>(SettingsService.StorageKey);
        Assert.False(persisted!.TelemetryEnabled);
        Assert.Equal(365, persisted.HistoryRetentionDays);
    }

    [Fact]
    public async Task Stored_values_are_normalized_on_load_and_rewritten()
    {
        var store = new InMemoryKeyValueStore();
        await store.SetAsync(SettingsService.StorageKey, new JsonObject
        {
            ["SchemaVersion"] = 1,
            ["Language"] = "fr-FR",
            ["TelemetryEnabled"] = true,
            ["HistoryRetentionDays"] = 2,
            ["Theme"] = 42,
        });

        var service = Create(store);
        await service.LoadAsync();

        Assert.Equal("fr", service.Current.Language);
        Assert.False(service.Current.TelemetryEnabled);
        Assert.Equal(7, service.Current.HistoryRetentionDays);
        Assert.Equal(ThemePreference.System, service.Current.Theme);
        var persisted = await store.GetAsync<AppSettings>(SettingsService.StorageKey);
        Assert.False(persisted!.TelemetryEnabled);
        Assert.Equal("fr", persisted.Language);
    }

    [Fact]
    public async Task Settings_without_schema_version_are_migrated()
    {
        var store = new InMemoryKeyValueStore();
        await store.SetAsync(SettingsService.StorageKey, new JsonObject { ["language"] = "en", ["expertMode"] = true });

        var service = Create(store);
        await service.LoadAsync();

        Assert.Equal(AppSettings.CurrentSchemaVersion, service.Current.SchemaVersion);
        Assert.Equal("en", service.Current.Language);
        Assert.True(service.Current.ExpertMode);
        Assert.Equal(AppSettings.CurrentSchemaVersion, (await store.GetAsync<AppSettings>(SettingsService.StorageKey))!.SchemaVersion);
    }

    [Fact]
    public async Task Unreadable_settings_fall_back_to_defaults()
    {
        var store = new InMemoryKeyValueStore();
        await store.SetAsync(SettingsService.StorageKey, "pas un objet");

        var service = Create(store);
        await service.LoadAsync();

        Assert.Equal("system", service.Current.Language);
        Assert.False(service.Current.TelemetryEnabled);
    }

    [Fact]
    public async Task Failing_store_never_blocks_the_application()
    {
        var service = Create(new ThrowingKeyValueStore());

        await service.LoadAsync();
        var edited = service.Current.Clone();
        edited.ExpertMode = true;
        await service.SaveAsync(edited);

        Assert.True(service.Current.ExpertMode);
    }

    [Fact]
    public async Task Settings_survive_a_restart_with_the_sqlite_store()
    {
        using var db = new TestDatabase();
        var service = Create(new SqliteKeyValueStore(db.Database, db.Clock, NullLogger<SqliteKeyValueStore>.Instance));
        await service.LoadAsync();
        var edited = service.Current.Clone();
        edited.Language = "en";
        edited.Theme = ThemePreference.Dark;
        edited.Gaming.AutoActivation = AutoGamingBehavior.Automatic;
        await service.SaveAsync(edited);

        using var reopened = db.Create();
        var again = Create(new SqliteKeyValueStore(reopened, db.Clock, NullLogger<SqliteKeyValueStore>.Instance));
        await again.LoadAsync();

        Assert.Equal("en", again.Current.Language);
        Assert.Equal(ThemePreference.Dark, again.Current.Theme);
        Assert.Equal(AutoGamingBehavior.Automatic, again.Current.Gaming.AutoActivation);
    }

    [Fact]
    public async Task Concurrent_saves_are_serialized()
    {
        var service = Create(new InMemoryKeyValueStore());
        await service.LoadAsync();

        await Task.WhenAll(Enumerable.Range(7, 30).Select(days =>
        {
            var s = service.Current.Clone();
            s.HistoryRetentionDays = days;
            return service.SaveAsync(s);
        }));

        Assert.InRange(service.Current.HistoryRetentionDays, 7, 36);
    }

    [Fact]
    public void Default_settings_need_no_correction()
        => Assert.Empty(AppSettingsNormalizer.Normalize(new AppSettings()));

    [Fact]
    public void Missing_nested_objects_are_recreated()
    {
        var settings = new AppSettings { Gaming = null!, Thresholds = null!, DismissedRecommendations = null! };

        var corrections = AppSettingsNormalizer.Normalize(settings);

        Assert.NotNull(settings.Gaming);
        Assert.NotNull(settings.Thresholds);
        Assert.NotNull(settings.DismissedRecommendations);
        Assert.Contains(nameof(AppSettings.Gaming), corrections);
    }

    private sealed class ThrowingKeyValueStore : IKeyValueStore
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => throw new IOException("disque indisponible");
        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default) => throw new IOException("disque indisponible");
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => throw new IOException("disque indisponible");
    }
}
