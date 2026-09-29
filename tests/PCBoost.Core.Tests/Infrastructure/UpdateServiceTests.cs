using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Infrastructure.Updates;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class UpdateServiceTests : IDisposable
{
    private const string PackageName = "PCBoost-1.1.0-x64.msi";

    private readonly TempDirectory _temp = new();
    private readonly string _feed;
    private readonly string _data;
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(300_000);

    public UpdateServiceTests()
    {
        _feed = _temp.SubDirectory("feed");
        _data = _temp.SubDirectory("data");
        File.WriteAllBytes(Path.Combine(_feed, PackageName), _package);
    }

    public void Dispose() => _temp.Dispose();

    private string PackageHash => Convert.ToHexString(SHA256.HashData(_package));

    private string DownloadDirectory => Path.Combine(_data, "updates");

    private void WriteFeed(string version = "1.1.0", string package = PackageName, string? sha256 = null, string? publishedAt = "2026-10-01T08:00:00Z")
        => File.WriteAllText(Path.Combine(_feed, LocalUpdateProvider.FeedFileName), $$"""
            { "version": "{{version}}", "package": "{{package.Replace("\\", "\\\\")}}", "sha256": "{{sha256 ?? PackageHash.ToLowerInvariant()}}",
              "notes": "Corrections", "publishedAt": "{{publishedAt}}" }
            """);

    private UpdateService CreateService(string? feedPath, Version? current = null)
    {
        var options = new UpdateOptions { LocalFeedPath = feedPath };
        var appInfo = new FakeAppInfo(_data, current ?? new Version(1, 0, 0, 0));
        IUpdateProvider[] providers =
        [
            new LocalUpdateProvider(options, appInfo, NullLogger<LocalUpdateProvider>.Instance),
            new PlaceholderRemoteUpdateProvider(),
        ];
        return new UpdateService(providers, appInfo, options, NullLogger<UpdateService>.Instance);
    }

    [Fact]
    public async Task Without_a_feed_the_status_is_not_configured()
    {
        var result = await CreateService(null).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.Null(result.Update);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Newer_version_in_the_feed_is_reported()
    {
        WriteFeed();

        var result = await CreateService(_feed).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new Version(1, 1, 0), result.Update!.Version);
        Assert.Equal(Path.Combine(_feed, PackageName), result.Update.PackageLocation);
        Assert.Equal(PackageHash, result.Update.Sha256);
        Assert.Equal("Corrections", result.Update.ReleaseNotes);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), result.Update.PublishedAt);
        Assert.Equal(new object[] { "1.1.0" }, result.Message!.Args);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0")]
    [InlineData("0.9.5")]
    public async Task Same_or_older_version_is_up_to_date(string version)
    {
        WriteFeed(version);

        var result = await CreateService(_feed, new Version(1, 0, 0, 0)).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Feed_file_path_can_be_given_directly()
    {
        WriteFeed();

        var result = await CreateService(Path.Combine(_feed, LocalUpdateProvider.FeedFileName)).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
    }

    [Theory]
    [InlineData("pas-une-version", PackageName, null)]
    [InlineData("1.1.0", @"..\evil.msi", null)]
    [InlineData("1.1.0", "sub/evil.msi", null)]
    [InlineData("1.1.0", "setup.exe", null)]
    [InlineData("1.1.0", PackageName, "1234")]
    [InlineData("1.1.0", PackageName, "zz" + "0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task Invalid_feed_content_fails(string version, string package, string? sha)
    {
        WriteFeed(version, package, sha);

        var result = await CreateService(_feed).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("Infra_Update_FeedInvalid", result.Message!.Key);
    }

    [Fact]
    public async Task Malformed_json_and_missing_feed_fail()
    {
        var missing = await CreateService(Path.Combine(_temp.Path, "absent")).CheckForUpdatesAsync();
        File.WriteAllText(Path.Combine(_feed, LocalUpdateProvider.FeedFileName), "{ \"version\": ");
        var malformed = await CreateService(_feed).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, missing.Status);
        Assert.Equal("Infra_Update_FeedNotFound", missing.Message!.Key);
        Assert.Equal(UpdateCheckStatus.Failed, malformed.Status);
        Assert.Equal("Infra_Update_FeedInvalid", malformed.Message!.Key);
    }

    [Fact]
    public async Task Valid_package_is_copied_and_verified()
    {
        WriteFeed();
        var service = CreateService(_feed);
        var check = await service.CheckForUpdatesAsync();
        var progress = new List<double>();

        var download = await service.DownloadAndVerifyAsync(check.Update!, new SynchronousProgress(progress.Add));

        Assert.True(download.Outcome.Success, download.Outcome.TechnicalDetail);
        Assert.Equal(Path.Combine(DownloadDirectory, PackageName), download.LocalPath);
        Assert.Equal(_package, File.ReadAllBytes(download.LocalPath!));
        Assert.Equal(100d, progress[^1]);
        Assert.Empty(Directory.GetFiles(DownloadDirectory, "*.partial"));
    }

    [Fact]
    public async Task Package_with_a_wrong_hash_is_deleted_and_refused()
    {
        var wrongHash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        WriteFeed(sha256: wrongHash);
        var service = CreateService(_feed);
        var check = await service.CheckForUpdatesAsync();

        var download = await service.DownloadAndVerifyAsync(check.Update!);

        Assert.False(download.Outcome.Success);
        Assert.Equal(OperationErrorKind.Blocked, download.Outcome.Error);
        Assert.Equal("Infra_Update_HashMismatch", download.Outcome.Message!.Key);
        Assert.Null(download.LocalPath);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, PackageName)));
        Assert.Equal(OperationErrorKind.NotFound, service.LaunchInstaller(Path.Combine(DownloadDirectory, PackageName)).Error);
    }

    [Fact]
    public async Task Malformed_expected_hash_is_rejected_before_copying()
    {
        var service = CreateService(_feed);
        var update = new UpdateInfo(new Version(1, 1, 0), Path.Combine(_feed, PackageName), "abc", null, null);

        var download = await service.DownloadAndVerifyAsync(update);

        Assert.Equal(OperationErrorKind.InvalidInput, download.Outcome.Error);
        Assert.False(Directory.Exists(DownloadDirectory));
    }

    [Fact]
    public async Task Package_outside_the_feed_folder_is_never_copied()
    {
        var outside = _temp.File("other.msi");
        File.WriteAllBytes(outside, _package);
        var service = CreateService(_feed);

        var download = await service.DownloadAndVerifyAsync(new UpdateInfo(new Version(9, 0), outside, PackageHash, null, null));

        Assert.False(download.Outcome.Success);
        Assert.Equal(OperationErrorKind.Blocked, download.Outcome.Error);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, "other.msi")));
    }

    [Fact]
    public async Task LaunchInstaller_only_accepts_a_verified_msi_from_the_updates_folder()
    {
        WriteFeed();
        var service = CreateService(_feed);
        var download = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);
        var outside = _temp.File("PCBoost-1.1.0-x64.msi");
        File.WriteAllBytes(outside, _package);
        var notMsi = Path.Combine(DownloadDirectory, "setup.exe");
        File.WriteAllBytes(notMsi, _package);
        var unverified = Path.Combine(DownloadDirectory, "unverified.msi");
        File.WriteAllBytes(unverified, _package);
        var traversal = Path.Combine(DownloadDirectory, "..", "..", "PCBoost-1.1.0-x64.msi");

        Assert.Equal(OperationErrorKind.Blocked, service.LaunchInstaller(outside).Error);
        Assert.Equal(OperationErrorKind.Blocked, service.LaunchInstaller(traversal).Error);
        Assert.Equal(OperationErrorKind.Blocked, service.LaunchInstaller(notMsi).Error);
        Assert.Equal(OperationErrorKind.Blocked, service.LaunchInstaller(unverified).Error);
        Assert.Equal(OperationErrorKind.InvalidInput, service.LaunchInstaller(" ").Error);
        Assert.Equal(OperationErrorKind.NotFound, service.LaunchInstaller(Path.Combine(DownloadDirectory, "absent.msi")).Error);

        if (!OperatingSystem.IsWindows())
        {
            // Hors Windows, le paquet vérifié passe tous les contrôles puis s'arrête avant msiexec.
            Assert.Equal(OperationErrorKind.NotSupported, service.LaunchInstaller(download.LocalPath!).Error);
        }
    }

    [Fact]
    public async Task Package_modified_after_verification_is_refused_and_deleted()
    {
        WriteFeed();
        var service = CreateService(_feed);
        var download = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);
        File.WriteAllBytes(download.LocalPath!, [0x4D, 0x5A]);

        var result = service.LaunchInstaller(download.LocalPath!);

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Infra_Update_HashMismatch", result.Message!.Key);
        Assert.False(File.Exists(download.LocalPath));
    }

    [Fact]
    public async Task Remote_placeholder_never_reports_an_update()
    {
        var provider = new PlaceholderRemoteUpdateProvider();

        var check = await provider.CheckAsync(new Version(0, 1));
        var download = await provider.DownloadAsync(new UpdateInfo(new Version(9, 9), "https://example.invalid/p.msi", new string('A', 64), null, null));

        Assert.Equal(UpdateCheckStatus.NotConfigured, check.Status);
        Assert.Equal(OperationErrorKind.NotSupported, download.Outcome.Error);
        Assert.Equal("remote", provider.Name);
    }

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
