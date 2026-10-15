using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Infrastructure.Updates;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class AutoUpdateServiceTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _data;
    private readonly string _app;
    private readonly FakeUpdates _updates = new();
    private readonly FakeSettingsService _settings = new();
    private readonly InMemoryKeyValueStore _store = new();
    private readonly FakeNetworkCostProvider _network = new();
    private readonly FakeNotificationService _notifications = new();
    private readonly FakeActivityJournal _journal = new();
    private readonly InMemoryRegistryProvider _registry = new();
    private readonly FakeElevationService _elevation = new();
    private readonly FakeClock _clock = new();

    public AutoUpdateServiceTests()
    {
        _data = _temp.SubDirectory("data");
        _app = _temp.SubDirectory("app");
    }

    public void Dispose() => _temp.Dispose();

    private string Downloads => Path.Combine(_data, "updates");

    private static UpdateInfo Update(string version = "1.3.0")
        => new(Version.Parse(version), $"https://github.com/NETFIX253/PCBoost/releases/download/v{version}/PCBoost-{version}-x64.msi", new string('A', 64),
            "Nouveautés " + version, null, 2_000_000, $"https://github.com/NETFIX253/PCBoost/releases/tag/v{version}", "{\"format\":1}");

    private void MarkInstalled(string? folder = null)
        => _registry.Set(RegistryHiveKind.LocalMachine, AutoUpdateService.InstallKey.KeyPath, AutoUpdateService.InstallFolderValue,
            new RegistryValueData(RegistryValueType.String, folder ?? _app));

    private AutoUpdateService Create(string version = "1.2.0")
        => new(_updates, _settings, _store, _network, _notifications, _journal, _registry, _elevation, new FakeAppInfo(_data, Version.Parse(version)),
            _clock, new UpdateOptions(), null, _app);

    [Fact]
    public async Task Installed_copy_downloads_the_signed_update_and_announces_installation_on_close()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(sessionStart: false);

        await service.CheckCoreAsync(automatic: true, CancellationToken.None);

        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.True(service.State.CanInstall);
        Assert.Single(_updates.Downloads);
        var ready = Assert.Single(_notifications.Shown);
        Assert.Equal("Infra_AutoUpdate_Notify_Ready_Title", ready.Title.Key);
        Assert.Equal(AutoUpdateService.InstallActionId, ready.ActionId);
        Assert.Contains(_journal.Entries, e => e.Message.Key == "Infra_AutoUpdate_Journal_Ready");
    }

    [Fact]
    public async Task Portable_copy_and_metered_connection_never_download_automatically()
    {
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using (var portable = Create())
        {
            await portable.StartAsync(false);
            await portable.CheckCoreAsync(automatic: true, CancellationToken.None);
            Assert.False(portable.State.IsInstalledCopy);
            Assert.Equal(AutoUpdatePhase.Available, portable.State.Phase);
            Assert.Equal("Infra_AutoUpdate_Notify_Available_Portable", Assert.Single(_notifications.Shown).Body.Key);
            Assert.Equal(OperationErrorKind.NotSupported, portable.RequestInstallNow().Error);
        }

        MarkInstalled();
        _network.Metered = true;
        _notifications.Shown.Clear();
        await _store.RemoveAsync(AutoUpdateService.StateKey);
        using var metered = Create();
        await metered.StartAsync(false);
        await metered.CheckCoreAsync(automatic: true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.Available, metered.State.Phase);
        Assert.True(metered.State.IsMetered);
        Assert.Equal("Infra_AutoUpdate_Notify_Available_Metered", Assert.Single(_notifications.Shown).Body.Key);
        Assert.Empty(_updates.Downloads);

        // Le même avis n'est montré qu'une fois par version.
        await metered.CheckCoreAsync(automatic: true, CancellationToken.None);
        Assert.Single(_notifications.Shown);
    }

    [Fact]
    public async Task Disabled_setting_or_policy_stops_automatic_downloads_and_installs()
    {
        MarkInstalled();
        _settings.Current.AutoUpdate = false;
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);

        await service.CheckNowAsync();
        Assert.Equal(AutoUpdatePhase.Available, service.State.Phase);
        Assert.Empty(_updates.Downloads);

        await service.DownloadNowAsync();
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.Null(await service.PrepareInstallOnExitAsync());

        _settings.Current.AutoUpdate = true;
        _registry.Set(RegistryHiveKind.LocalMachine, AutoUpdateService.PolicyKey.KeyPath, AutoUpdateService.DisableAutoUpdateValue, RegistryValueData.DWord(1));
        Assert.True(service.IsDisabledByPolicy);
        Assert.False(service.IsAutomatic);
        Assert.Null(await service.PrepareInstallOnExitAsync());
    }

    [Fact]
    public async Task Closing_installs_the_ready_update_in_the_current_folder_without_relaunch()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);

        using var prepared = await service.PrepareInstallOnExitAsync();

        Assert.NotNull(prepared);
        var (path, options) = Assert.Single(_updates.Prepared);
        Assert.Equal(_updates.Downloads[0], path);
        Assert.Equal(UpdateRelaunch.None, options.Relaunch);
        Assert.Equal(_app, options.InstallFolder);
        Assert.Equal(Path.Combine(Downloads, "install-1.3.0.log"), options.LogPath);
        Assert.Contains(_journal.Entries, e => e.Message.Key == "Infra_AutoUpdate_Journal_Installing");
    }

    [Fact]
    public async Task Install_now_asks_the_host_to_close_then_relaunches_with_the_window()
    {
        MarkInstalled();
        _settings.Current.AutoUpdate = false;
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);
        Assert.Equal(OperationErrorKind.InvalidInput, service.RequestInstallNow().Error);
        await service.CheckNowAsync();
        await service.DownloadNowAsync();
        var requests = new List<UpdateExitRequest>();
        service.ExitForInstallRequested += (_, request) => requests.Add(request);

        Assert.True(service.RequestInstallNow().Success);
        using var prepared = await service.PrepareInstallOnExitAsync();

        Assert.False(Assert.Single(requests).Automatic);
        Assert.NotNull(prepared);
        Assert.Equal(UpdateRelaunch.Window, _updates.Prepared[0].Options.Relaunch);
    }

    [Fact]
    public async Task Pending_update_from_a_previous_session_is_verified_again_and_installed_at_session_start()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using (var first = Create())
        {
            await first.StartAsync(false);
            await first.CheckCoreAsync(true, CancellationToken.None);
        }

        _updates.Downloads.Clear();
        using var second = Create();
        await second.StartAsync(sessionStart: true);

        Assert.Single(_updates.Verified);
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, second.State.Phase);
        Assert.Empty(_updates.Downloads);

        // Invalide à la revérification (manifeste ou paquet altéré) : abandonné.
        _updates.VerifyFails = true;
        using var third = Create();
        await third.StartAsync(false);
        Assert.NotEqual(AutoUpdatePhase.ReadyToInstall, third.State.Phase);
    }

    [Fact]
    public async Task Two_automatic_attempts_without_effect_stop_automatic_installation()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using (var service = Create())
        {
            await service.StartAsync(false);
            await service.CheckCoreAsync(true, CancellationToken.None);
            (await service.PrepareInstallOnExitAsync())?.Dispose();
        }
        using (var service = Create())
        {
            await service.StartAsync(false);
            Assert.False(service.State.InstallBlocked);
            (await service.PrepareInstallOnExitAsync())?.Dispose();
        }

        // Toujours en 1.2.0 après deux tentatives (autorisation refusée…) : plus d'installation automatique.
        using var blocked = Create();
        await blocked.StartAsync(sessionStart: true);
        Assert.True(blocked.State.InstallBlocked);
        Assert.Equal("Infra_AutoUpdate_InstallBlocked", blocked.State.Message!.Key);
        Assert.Null(await blocked.PrepareInstallOnExitAsync());
        Assert.True(blocked.RequestInstallNow().Success);
        Assert.NotNull(await blocked.PrepareInstallOnExitAsync());
    }

    [Fact]
    public async Task After_the_update_the_new_version_announces_it_and_cleans_old_packages()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using (var old = Create("1.2.0"))
        {
            await old.StartAsync(false);
            await old.CheckCoreAsync(true, CancellationToken.None);
        }
        Directory.CreateDirectory(Downloads);
        File.WriteAllText(Path.Combine(Downloads, "PCBoost-1.3.0-x64.msi"), "paquet");
        _notifications.Shown.Clear();

        using var updated = Create("1.3.0");
        await updated.StartAsync(false);

        var done = Assert.Single(_notifications.Shown);
        Assert.Equal("Infra_AutoUpdate_Notify_Updated_Title", done.Title.Key);
        Assert.Equal("navigate:settings", done.ActionId);
        Assert.Contains(_journal.Entries, e => e.Message.Key == "Infra_AutoUpdate_Journal_Updated");
        Assert.Equal("Nouveautés 1.3.0", updated.InstalledReleaseNotes);
        Assert.Empty(Directory.GetFiles(Downloads, "*.msi"));
        Assert.Empty(_updates.Verified);

        // Démarrage suivant : rien à annoncer.
        _notifications.Shown.Clear();
        using var again = Create("1.3.0");
        await again.StartAsync(false);
        Assert.Empty(_notifications.Shown);
    }

    [Fact]
    public async Task Transient_failures_keep_a_ready_update_ready()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);

        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.Failed, null, TextRef.Of("Infra_Update_Offline"));
        await service.CheckCoreAsync(true, CancellationToken.None);

        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.NotNull(service.State.LastCheckedAt);
    }

    [Fact]
    public async Task Failed_download_is_reported_and_retried_at_the_next_check()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        _updates.DownloadFails = true;
        using var service = Create();
        await service.StartAsync(false);

        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.Failed, service.State.Phase);
        Assert.Equal("Infra_Update_DownloadFailed", service.State.Message!.Key);

        _updates.DownloadFails = false;
        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
    }

    [Fact]
    public async Task Elevated_instance_never_downloads_nor_installs_automatically()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        _elevation.IsElevated = true;
        using (var elevated = Create())
        {
            await elevated.StartAsync(false);
            await elevated.CheckCoreAsync(true, CancellationToken.None);
            Assert.Equal(AutoUpdatePhase.Available, elevated.State.Phase);
            Assert.Empty(_updates.Downloads);
            await elevated.DownloadNowAsync();
            Assert.Empty(_updates.Downloads);
            Assert.Equal(AutoUpdatePhase.Failed, elevated.State.Phase);
            Assert.Equal("Infra_AutoUpdate_Elevated", elevated.State.Message!.Key);
        }

        // Mise à jour prête (téléchargée par une instance normale), puis PCBoost relancé en administrateur.
        _elevation.IsElevated = false;
        await _store.RemoveAsync(AutoUpdateService.StateKey);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        _elevation.IsElevated = true;

        var refused = service.RequestInstallNow();
        Assert.Equal(OperationErrorKind.Blocked, refused.Error);
        Assert.Equal("Infra_AutoUpdate_Elevated", refused.Message!.Key);
        Assert.Null(await service.PrepareInstallOnExitAsync());
        Assert.Empty(_updates.Prepared);

        _notifications.Shown.Clear();
        service.OnSessionInstallDue();
        Assert.Empty(_notifications.Shown);
    }

    [Fact]
    public async Task Session_start_install_is_an_automatic_request_the_host_can_cancel()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);
        var requests = new List<UpdateExitRequest>();
        service.ExitForInstallRequested += (_, request) => requests.Add(request);
        _notifications.Shown.Clear();

        service.OnSessionInstallDue();

        Assert.True(Assert.Single(requests).Automatic);
        Assert.Equal("Infra_AutoUpdate_Notify_Installing_Title", Assert.Single(_notifications.Shown).Title.Key);

        // Fenêtre déjà utilisée : l'hôte reporte. La fermeture ordinaire suivante installe sans relance.
        service.CancelInstallRequest();
        using (var ordinary = await service.PrepareInstallOnExitAsync())
        {
            Assert.NotNull(ordinary);
            Assert.Equal(UpdateRelaunch.None, _updates.Prepared[^1].Options.Relaunch);
        }

        // Une demande automatique respecte le réglage, contrairement à « Installer maintenant ».
        service.OnSessionInstallDue();
        _settings.Current.AutoUpdate = false;
        Assert.Null(await service.PrepareInstallOnExitAsync());
        Assert.True(service.RequestInstallNow().Success);
        using var explicitInstall = await service.PrepareInstallOnExitAsync();
        Assert.NotNull(explicitInstall);
        Assert.Equal(UpdateRelaunch.Window, _updates.Prepared[^1].Options.Relaunch);
    }

    [Fact]
    public async Task Ready_update_stays_ready_until_a_newer_one_is_downloaded()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update("1.3.0"), null);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);

        // Même version publiée : rien n'est retéléchargé.
        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Single(_updates.Downloads);

        // Version plus récente dont le téléchargement échoue : la 1.3.0 reste prête et installable.
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update("1.4.0"), null);
        _updates.DownloadFails = true;
        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.Equal(new Version(1, 3, 0), service.State.Update!.Version);
        using (var prepared = await service.PrepareInstallOnExitAsync())
        {
            Assert.NotNull(prepared);
            Assert.Equal(_updates.Downloads[0], _updates.Prepared[^1].Path);
        }

        // Puis téléchargée : la 1.4.0 remplace la 1.3.0.
        _updates.DownloadFails = false;
        await service.CheckCoreAsync(true, CancellationToken.None);
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        Assert.Equal(new Version(1, 4, 0), service.State.Update!.Version);
    }

    [Fact]
    public async Task Cancelled_check_or_download_restores_the_previous_state()
    {
        MarkInstalled();
        _settings.Current.AutoUpdate = false;
        using var service = Create();
        await service.StartAsync(false);

        _updates.CancelCheck = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckNowAsync());
        Assert.Equal(AutoUpdatePhase.Idle, service.State.Phase);

        _updates.CancelCheck = false;
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        await service.CheckNowAsync();
        Assert.Equal(AutoUpdatePhase.Available, service.State.Phase);

        _updates.CancelDownload = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadNowAsync());
        Assert.Equal(AutoUpdatePhase.Available, service.State.Phase);
        Assert.Null(service.State.DownloadPercent);

        _updates.CancelDownload = false;
        await service.DownloadNowAsync();
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
        _updates.CancelCheck = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckNowAsync());
        Assert.Equal(AutoUpdatePhase.ReadyToInstall, service.State.Phase);
    }

    [Fact]
    public async Task Concurrent_state_changes_are_all_kept()
    {
        MarkInstalled();
        _updates.Check = new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, Update(), null);
        using var service = Create();
        await service.StartAsync(false);
        await service.CheckCoreAsync(true, CancellationToken.None);

        // Deux préparations simultanées (fermeture + action) : chaque tentative est comptée, aucune n'est perdue.
        var first = service.PrepareInstallOnExitAsync();
        var second = service.PrepareInstallOnExitAsync();
        (await first)?.Dispose();
        (await second)?.Dispose();

        using var restarted = Create();
        await restarted.StartAsync(false);
        Assert.True(restarted.State.InstallBlocked);
    }

    [Fact]
    public async Task Old_partial_downloads_are_cleaned_recent_ones_are_kept_for_resume()
    {
        Directory.CreateDirectory(Downloads);
        var recent = Path.Combine(Downloads, "PCBoost-1.3.0-x64.msi.0123456789abcdef.partial");
        var old = Path.Combine(Downloads, "PCBoost-1.2.5-x64.msi.fedcba9876543210.partial");
        File.WriteAllText(recent, "début");
        File.WriteAllText(old, "début");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));

        using var service = Create();
        await service.StartAsync(false);

        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void Copy_is_installed_only_when_the_installer_folder_is_the_application_folder()
    {
        using (var portable = Create()) Assert.False(portable.IsInstalledCopy());
        MarkInstalled(Path.Combine(_temp.Path, "ailleurs"));
        using (var elsewhere = Create()) Assert.False(elsewhere.IsInstalledCopy());
        MarkInstalled(_app + Path.DirectorySeparatorChar);
        using var installed = Create();
        Assert.True(installed.IsInstalledCopy());
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public UpdateCheckResult Check { get; set; } = new(UpdateCheckStatus.UpToDate, null, null);
        public bool DownloadFails { get; set; }
        public bool VerifyFails { get; set; }
        public bool CancelCheck { get; set; }
        public bool CancelDownload { get; set; }
        public List<string> Downloads { get; } = [];
        public List<string> Verified { get; } = [];
        public List<(string Path, UpdateInstallOptions Options)> Prepared { get; } = [];

        public Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
            => CancelCheck ? Task.FromCanceled<UpdateCheckResult>(new CancellationToken(canceled: true)) : Task.FromResult(Check);

        public Task<UpdateDownloadResult> DownloadAndVerifyAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (DownloadFails) return Task.FromResult(new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Infra_Update_DownloadFailed")), null));
            if (CancelDownload)
            {
                progress?.Report(40);
                return Task.FromCanceled<UpdateDownloadResult>(new CancellationToken(canceled: true));
            }
            progress?.Report(100);
            var path = Path.Combine(Path.GetTempPath(), $"PCBoost-{update.Version}-x64.msi");
            Downloads.Add(path);
            return Task.FromResult(new UpdateDownloadResult(OperationResult.Ok(), path));
        }

        public Task<UpdateDownloadResult> VerifyDownloadedAsync(UpdateInfo update, string packagePath, CancellationToken cancellationToken = default)
        {
            Verified.Add(packagePath);
            return Task.FromResult(VerifyFails
                ? new UpdateDownloadResult(OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Infra_Update_SignatureInvalid")), null)
                : new UpdateDownloadResult(OperationResult.Ok(), packagePath));
        }

        public OperationResult LaunchInstaller(string verifiedPackagePath) => OperationResult.Ok();

        public OperationResult<IPreparedUpdateInstall> PrepareInstall(string verifiedPackagePath, UpdateInstallOptions options)
        {
            Prepared.Add((verifiedPackagePath, options));
            return OperationResult<IPreparedUpdateInstall>.Ok(new PreparedInstall());
        }

        private sealed class PreparedInstall : IPreparedUpdateInstall
        {
            public Version Version => new(1, 3, 0);
            public OperationResult Start() => OperationResult.Ok();
            public void Dispose() { }
        }
    }
}
