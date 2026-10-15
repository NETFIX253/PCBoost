using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Infrastructure.Updates;

namespace PCBoost.Core.Tests.CrossCutting;

/// <summary>Signe des manifestes de test avec une clé ECDSA P-256 générée pour le test (jamais la clé de production).</summary>
internal sealed class ManifestSigner : IDisposable
{
    public const string Repository = "NETFIX253/PCBoost";
    public const string KeyId = "test-key";

    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public IReadOnlyDictionary<string, string> Trust => new Dictionary<string, string> { [KeyId] = Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo()) };

    public static string PackageUrl(string fileName, string version = "1.1.0")
        => $"https://github.com/{Repository}/releases/download/v{version}/{fileName}";

    public static string Payload(string version, string fileName, long size, string sha256, string? url = null, string product = "PCBoost",
        string? arch = null, string? notes = "Corrections", string? releaseUrl = null)
        => JsonSerializer.Serialize(new
        {
            product,
            version,
            publishedAt = "2026-10-01T08:00:00Z",
            notes,
            releaseUrl = releaseUrl ?? $"https://github.com/{Repository}/releases/tag/v{version}",
            packages = new[]
            {
                new { arch = arch ?? SignedUpdateManifest.CurrentArchitecture, kind = "msi", fileName, url = url ?? PackageUrl(fileName, version), size, sha256 },
            },
        });

    public string Sign(string payload, ECDsa? key = null, string keyId = KeyId)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        var signature = (key ?? Key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return JsonSerializer.Serialize(new
        {
            format = 1,
            payload = Convert.ToBase64String(bytes),
            signatures = new[] { new { keyId, sig = Convert.ToBase64String(signature) } },
        });
    }

    public void Dispose() => Key.Dispose();
}

public sealed class UpdateServiceTests : IDisposable
{
    private const string PackageName = "PCBoost-1.1.0-x64.msi";

    private readonly TempDirectory _temp = new();
    private readonly ManifestSigner _signer = new();
    private readonly string _feed;
    private readonly string _data;
    private readonly byte[] _package = RandomNumberGenerator.GetBytes(1_200_000);

    public UpdateServiceTests()
    {
        _feed = _temp.SubDirectory("feed");
        _data = _temp.SubDirectory("data");
        File.WriteAllBytes(Path.Combine(_feed, PackageName), _package);
    }

    public void Dispose()
    {
        _signer.Dispose();
        _temp.Dispose();
    }

    private string PackageHash => Convert.ToHexString(SHA256.HashData(_package));

    private string DownloadDirectory => Path.Combine(_data, "updates");

    private string Payload(string version = "1.1.0", string package = PackageName, long? size = null, string? sha256 = null)
        => ManifestSigner.Payload(version, package, size ?? _package.Length, sha256 ?? PackageHash.ToLowerInvariant());

    private void WriteFeed(string? manifest = null)
        => File.WriteAllText(Path.Combine(_feed, LocalUpdateProvider.FeedFileName), manifest ?? _signer.Sign(Payload()));

    private UpdateService CreateService(string? feedPath, Version? current = null, HttpMessageHandler? http = null, string? repository = null)
    {
        var options = new UpdateOptions { LocalFeedPath = feedPath, Repository = repository, TrustedKeys = _signer.Trust };
        var appInfo = new FakeAppInfo(_data, current ?? new Version(1, 0, 0, 0));
        IUpdateProvider[] providers =
        [
            new LocalUpdateProvider(options, appInfo, NullLogger<LocalUpdateProvider>.Instance),
            new GitHubUpdateProvider(options, appInfo, NullLogger<GitHubUpdateProvider>.Instance, http ?? new FakeHttp()),
        ];
        return new UpdateService(providers, appInfo, options, NullLogger<UpdateService>.Instance);
    }

    // ---- Source locale (manifeste signé) ----------------------------------------------------------------------------

    [Fact]
    public async Task Without_any_source_the_status_is_not_configured()
    {
        var result = await CreateService(null).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.NotConfigured, result.Status);
        Assert.Null(result.Update);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Newer_signed_version_in_the_local_feed_is_reported()
    {
        WriteFeed();

        var result = await CreateService(_feed).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        var update = result.Update!;
        Assert.Equal(new Version(1, 1, 0), update.Version);
        Assert.Equal(Path.Combine(_feed, PackageName), update.PackageLocation);
        Assert.Equal(PackageHash, update.Sha256);
        Assert.Equal(_package.Length, update.Size);
        Assert.Equal("Corrections", update.ReleaseNotes);
        Assert.Equal("https://github.com/NETFIX253/PCBoost/releases/tag/v1.1.0", update.ReleaseUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero), update.PublishedAt);
        Assert.NotNull(update.SignedManifest);
        Assert.Equal(new object[] { "1.1.0" }, result.Message!.Args);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.0")]
    [InlineData("0.9.5")]
    public async Task Same_or_older_version_is_up_to_date(string version)
    {
        WriteFeed(_signer.Sign(Payload(version)));

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

    [Fact]
    public async Task Unsigned_tampered_or_foreign_manifests_are_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload();
        var signed = _signer.Sign(payload);
        var tampered = signed.Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(payload.Replace("1.1.0", "9.9.0", StringComparison.Ordinal))), StringComparison.Ordinal);
        string[] manifests =
        [
            _signer.Sign(payload, other),                       // clé inconnue sous un identifiant connu
            _signer.Sign(payload, _signer.Key, "autre-cle"),    // identifiant inconnu
            tampered,                                           // charge utile modifiée après signature
            JsonSerializer.Serialize(new { format = 1, payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload)), signatures = Array.Empty<object>() }),
        ];
        foreach (var manifest in manifests)
        {
            WriteFeed(manifest);
            var result = await CreateService(_feed).CheckForUpdatesAsync();
            Assert.Equal(UpdateCheckStatus.Failed, result.Status);
            Assert.Equal("Infra_Update_SignatureInvalid", result.Message!.Key);
        }
    }

    [Fact]
    public async Task One_valid_signature_among_several_is_enough()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var payload = Payload();
        var bytes = Encoding.UTF8.GetBytes(payload);
        var manifest = JsonSerializer.Serialize(new
        {
            format = 1,
            payload = Convert.ToBase64String(bytes),
            signatures = new[]
            {
                new { keyId = "ancienne", sig = Convert.ToBase64String(other.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) },
                new { keyId = ManifestSigner.KeyId, sig = Convert.ToBase64String(_signer.Key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) },
            },
        });
        WriteFeed(manifest);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, (await CreateService(_feed).CheckForUpdatesAsync()).Status);
    }

    public static TheoryData<string> InvalidPayloads()
    {
        var sha = new string('a', 64);
        return
        [
            ManifestSigner.Payload("pas-une-version", PackageName, 2_000_000, sha),
            ManifestSigner.Payload("1.1.0.0.0", PackageName, 2_000_000, sha),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, sha, product: "Autre"),
            ManifestSigner.Payload("1.1.0", @"..\evil.msi", 2_000_000, sha, url: "https://github.com/NETFIX253/PCBoost/releases/download/v1.1.0/evil.msi"),
            ManifestSigner.Payload("1.1.0", "setup.exe", 2_000_000, sha),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, "1234"),
            ManifestSigner.Payload("1.1.0", PackageName, 1000, sha),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, sha, url: "http://github.com/NETFIX253/PCBoost/releases/download/v1.1.0/" + PackageName),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, sha, url: "https://github.com/NETFIX253/PCBoost/releases/download/v1.1.0/autre.msi"),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, sha, arch: "mips"),
            ManifestSigner.Payload("1.1.0", PackageName, 2_000_000, sha, notes: new string('x', 9000)),
        ];
    }

    [Theory]
    [MemberData(nameof(InvalidPayloads))]
    public async Task Signed_but_invalid_content_is_refused(string payload)
    {
        WriteFeed(_signer.Sign(payload));

        var result = await CreateService(_feed).CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal("Infra_Update_FeedInvalid", result.Message!.Key);
    }

    [Fact]
    public async Task Malformed_json_and_missing_feed_fail()
    {
        var missing = await CreateService(Path.Combine(_temp.Path, "absent")).CheckForUpdatesAsync();
        WriteFeed("{ \"format\": ");
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
    public async Task Package_with_a_wrong_hash_or_size_is_deleted_and_refused()
    {
        var wrongHash = Convert.ToHexString(SHA256.HashData([1, 2, 3]));
        WriteFeed(_signer.Sign(Payload(sha256: wrongHash)));
        var service = CreateService(_feed);
        var download = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);

        Assert.Equal(OperationErrorKind.Blocked, download.Outcome.Error);
        Assert.Equal("Infra_Update_HashMismatch", download.Outcome.Message!.Key);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, PackageName)));
        Assert.Equal(OperationErrorKind.NotFound, service.LaunchInstaller(Path.Combine(DownloadDirectory, PackageName)).Error);

        WriteFeed(_signer.Sign(Payload(size: _package.Length + 1)));
        service = CreateService(_feed);
        var wrongSize = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);
        Assert.Equal("Infra_Update_SizeMismatch", wrongSize.Outcome.Message!.Key);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, PackageName)));
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

    // ---- Installation -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Installation_only_accepts_a_verified_msi_from_the_updates_folder()
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
        var options = new UpdateInstallOptions(UpdateRelaunch.None, null, null);

        foreach (var path in new[] { outside, traversal, notMsi, unverified })
        {
            Assert.Equal(OperationErrorKind.Blocked, service.LaunchInstaller(path).Error);
            Assert.Equal(OperationErrorKind.Blocked, service.PrepareInstall(path, options).Error);
        }
        Assert.Equal(OperationErrorKind.InvalidInput, service.LaunchInstaller(" ").Error);
        Assert.Equal(OperationErrorKind.NotFound, service.LaunchInstaller(Path.Combine(DownloadDirectory, "absent.msi")).Error);

        if (!OperatingSystem.IsWindows())
        {
            // Hors Windows, le paquet vérifié passe tous les contrôles puis s'arrête avant msiexec.
            Assert.Equal(OperationErrorKind.NotSupported, service.LaunchInstaller(download.LocalPath!).Error);
            Assert.Equal(OperationErrorKind.NotSupported, service.PrepareInstall(download.LocalPath!, options).Error);
        }
    }

    [Fact]
    public async Task Package_modified_after_verification_is_refused_and_deleted()
    {
        WriteFeed();
        var service = CreateService(_feed);
        var download = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);
        File.WriteAllBytes(download.LocalPath!, [0x4D, 0x5A]);

        var result = service.PrepareInstall(download.LocalPath!, new UpdateInstallOptions(UpdateRelaunch.Window, null, null));

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Infra_Update_HashMismatch", result.Message!.Key);
        Assert.False(File.Exists(download.LocalPath));
    }

    [Fact]
    public async Task Package_from_a_previous_session_is_verified_again_from_its_signed_manifest()
    {
        WriteFeed();
        var first = CreateService(_feed);
        var update = (await first.CheckForUpdatesAsync()).Update!;
        var path = (await first.DownloadAndVerifyAsync(update)).LocalPath!;

        // Nouvelle instance (redémarrage) : le paquet n'est installable qu'après une nouvelle vérification.
        var second = CreateService(null);
        Assert.Equal(OperationErrorKind.Blocked, second.PrepareInstall(path, new UpdateInstallOptions(UpdateRelaunch.None, null, null)).Error);
        var verified = await second.VerifyDownloadedAsync(update, path);
        Assert.True(verified.Outcome.Success, verified.Outcome.TechnicalDetail);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(OperationErrorKind.NotSupported, second.PrepareInstall(path, new UpdateInstallOptions(UpdateRelaunch.None, null, null)).Error);

        // Manifeste conservé altéré, autre version annoncée, paquet hors du dossier : refusés.
        var forged = await second.VerifyDownloadedAsync(update with { SignedManifest = update.SignedManifest!.Replace("\"format\":1", "\"format\":2", StringComparison.Ordinal) }, path);
        var otherVersion = await second.VerifyDownloadedAsync(update with { Version = new Version(1, 2, 0) }, path);
        var elsewhere = await second.VerifyDownloadedAsync(update, Path.Combine(_feed, PackageName));
        Assert.Equal("Infra_Update_SignatureInvalid", forged.Outcome.Message!.Key);
        Assert.Equal("Infra_Update_InvalidPackage", otherVersion.Outcome.Message!.Key);
        Assert.Equal("Infra_Update_InvalidPackage", elsewhere.Outcome.Message!.Key);

        // Paquet modifié entre-temps : supprimé.
        File.WriteAllBytes(path, [1, 2, 3]);
        var modified = await second.VerifyDownloadedAsync(update, path);
        Assert.Equal(OperationErrorKind.Blocked, modified.Outcome.Error);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Quiet_installation_arguments_keep_the_folder_log_and_relaunch_mode()
    {
        var root = Path.GetTempPath();
        var package = Path.Combine(root, "updates", "PCBoost-1.3.0-x64.msi");
        var folder = Path.Combine(root, "Program Files", "PCBoost") + Path.DirectorySeparatorChar;
        var log = Path.Combine(root, "updates", "install-1.3.0.log");

        Assert.Equal($"/i \"{package}\" /qr /norestart PCBOOST_RELAUNCH=window INSTALLFOLDER=\"{folder.TrimEnd(Path.DirectorySeparatorChar)}\" /l*v \"{log}\"",
            UpdateService.BuildQuietArguments(package, new UpdateInstallOptions(UpdateRelaunch.Window, folder, log)));
        Assert.Equal($"/i \"{package}\" /qr /norestart PCBOOST_RELAUNCH=background",
            UpdateService.BuildQuietArguments(package, new UpdateInstallOptions(UpdateRelaunch.Background, null, null)));
        Assert.Equal($"/i \"{package}\" /qr /norestart",
            UpdateService.BuildQuietArguments(package, new UpdateInstallOptions(UpdateRelaunch.None, null, null)));
        Assert.Null(UpdateService.BuildQuietArguments("relatif.msi", new UpdateInstallOptions(UpdateRelaunch.None, null, null)));
        Assert.Null(UpdateService.BuildQuietArguments(package, new UpdateInstallOptions(UpdateRelaunch.None, folder + "\" /x \"", null)));
        Assert.Null(UpdateService.BuildQuietArguments(package, new UpdateInstallOptions(UpdateRelaunch.None, null, "journal.log")));
    }

    // ---- Releases GitHub --------------------------------------------------------------------------------------------

    private FakeHttp GitHub(string? manifest = null, byte[]? package = null)
    {
        var http = new FakeHttp();
        http.Responses[$"https://github.com/{ManifestSigner.Repository}/releases/latest/download/pcboost-update.json"] =
            () => FakeHttp.Text(manifest ?? _signer.Sign(Payload()));
        http.Responses[ManifestSigner.PackageUrl(PackageName)] = () => FakeHttp.Bytes(package ?? _package);
        return http;
    }

    [Fact]
    public async Task GitHub_release_manifest_is_verified_then_the_package_is_downloaded()
    {
        var http = GitHub();
        var service = CreateService(null, http: http, repository: ManifestSigner.Repository);

        var check = await service.CheckForUpdatesAsync();
        var progress = new List<double>();
        var download = await service.DownloadAndVerifyAsync(check.Update!, new SynchronousProgress(progress.Add));

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, check.Status);
        Assert.Equal(ManifestSigner.PackageUrl(PackageName), check.Update!.PackageLocation);
        Assert.True(download.Outcome.Success, download.Outcome.TechnicalDetail);
        Assert.Equal(_package, File.ReadAllBytes(download.LocalPath!));
        Assert.Equal(100d, progress[^1]);
        Assert.All(http.Requests, r => Assert.Equal("https", r.Scheme));
        Assert.Contains(http.UserAgents, ua => ua.StartsWith("PCBoost/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitHub_manifest_pointing_outside_the_repository_is_refused()
    {
        var payload = ManifestSigner.Payload("1.1.0", PackageName, _package.Length, PackageHash,
            url: "https://github.com/autre/depot/releases/download/v1.1.0/" + PackageName);
        var service = CreateService(null, http: GitHub(_signer.Sign(payload)), repository: ManifestSigner.Repository);

        var check = await service.CheckForUpdatesAsync();

        Assert.Equal(UpdateCheckStatus.Failed, check.Status);
        Assert.Equal("Infra_Update_FeedInvalid", check.Message!.Key);
    }

    [Fact]
    public async Task GitHub_failures_are_reported_without_installing_anything()
    {
        var empty = new FakeHttp();
        var offline = new FakeHttp { Throw = true };
        var notFound = await CreateService(null, http: empty, repository: ManifestSigner.Repository).CheckForUpdatesAsync();
        var unreachable = await CreateService(null, http: offline, repository: ManifestSigner.Repository).CheckForUpdatesAsync();
        var invalidRepository = await CreateService(null, http: GitHub(), repository: "pas un dépôt").CheckForUpdatesAsync();

        Assert.Equal("Infra_Update_FeedNotFound", notFound.Message!.Key);
        Assert.Equal("Infra_Update_Offline", unreachable.Message!.Key);
        Assert.Equal(UpdateCheckStatus.NotConfigured, invalidRepository.Status);
    }

    [Fact]
    public async Task GitHub_download_with_unexpected_size_is_refused()
    {
        var longer = _package.Concat(new byte[] { 1, 2, 3 }).ToArray();
        var service = CreateService(null, http: GitHub(package: longer), repository: ManifestSigner.Repository);
        var check = await service.CheckForUpdatesAsync();

        var download = await service.DownloadAndVerifyAsync(check.Update!);

        Assert.Equal(OperationErrorKind.Blocked, download.Outcome.Error);
        Assert.Equal("Infra_Update_SizeMismatch", download.Outcome.Message!.Key);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, PackageName)));
        Assert.Empty(Directory.GetFiles(DownloadDirectory, "*.partial"));
    }

    [Fact]
    public async Task Interrupted_GitHub_download_is_resumed_where_it_stopped()
    {
        var cut = _package.Length / 3;
        var http = GitHub();
        var attempt = 0;
        http.Handler = request =>
        {
            if (request.RequestUri!.AbsoluteUri != ManifestSigner.PackageUrl(PackageName)) return null;
            attempt++;
            if (attempt == 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new CutStream(_package, cut)) };
            var from = request.Headers.Range!.Ranges.Single().From!.Value;
            var rest = new ByteArrayContent(_package[(int)from..]);
            rest.Headers.ContentRange = new ContentRangeHeaderValue(from, _package.Length - 1, _package.Length);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = rest };
        };
        var service = CreateService(null, http: http, repository: ManifestSigner.Repository);
        var update = (await service.CheckForUpdatesAsync()).Update!;

        var interrupted = await service.DownloadAndVerifyAsync(update);
        var partial = Assert.Single(Directory.GetFiles(DownloadDirectory, "*.partial"));
        Assert.Equal("Infra_Update_DownloadFailed", interrupted.Outcome.Message!.Key);
        Assert.Equal(cut, new FileInfo(partial).Length);

        var resumed = await service.DownloadAndVerifyAsync(update);

        Assert.True(resumed.Outcome.Success, resumed.Outcome.TechnicalDetail);
        Assert.Equal(_package, File.ReadAllBytes(resumed.LocalPath!));
        Assert.Equal(cut, http.Ranges[^1]!.Ranges.Single().From);
        Assert.Empty(Directory.GetFiles(DownloadDirectory, "*.partial"));
    }

    [Fact]
    public async Task Server_ignoring_the_range_restarts_the_download_from_the_beginning()
    {
        var http = GitHub();
        var service = CreateService(null, http: http, repository: ManifestSigner.Repository);
        var update = (await service.CheckForUpdatesAsync()).Update!;
        Directory.CreateDirectory(DownloadDirectory);
        var partial = Path.Combine(DownloadDirectory, $"{PackageName}.{PackageHash[..16].ToLowerInvariant()}.partial");
        File.WriteAllBytes(partial, new byte[1000]);

        var download = await service.DownloadAndVerifyAsync(update);

        Assert.True(download.Outcome.Success, download.Outcome.TechnicalDetail);
        Assert.Equal(1000, http.Ranges[^1]!.Ranges.Single().From);
        Assert.Equal(_package, File.ReadAllBytes(download.LocalPath!));
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task Local_feed_package_larger_than_signed_is_never_copied()
    {
        WriteFeed(_signer.Sign(Payload(size: _package.Length - 10)));
        var service = CreateService(_feed);

        var download = await service.DownloadAndVerifyAsync((await service.CheckForUpdatesAsync()).Update!);

        Assert.Equal("Infra_Update_SizeMismatch", download.Outcome.Message!.Key);
        Assert.False(File.Exists(Path.Combine(DownloadDirectory, PackageName)));
        Assert.True(!Directory.Exists(DownloadDirectory) || Directory.GetFiles(DownloadDirectory).Length == 0);
    }

    [Fact]
    public async Task Local_feed_is_consulted_before_GitHub()
    {
        WriteFeed();
        var http = GitHub();
        var service = CreateService(_feed, http: http, repository: ManifestSigner.Repository);

        var check = await service.CheckForUpdatesAsync();

        Assert.Equal(Path.Combine(_feed, PackageName), check.Update!.PackageLocation);
        Assert.Empty(http.Requests);
    }

    [Theory]
    [InlineData("NETFIX253/PCBoost", true)]
    [InlineData("a/b.c-d_e", true)]
    [InlineData("NETFIX253", false)]
    [InlineData("NETFIX253/PCBoost/autre", false)]
    [InlineData("../PCBoost", false)]
    [InlineData("NETFIX253/..", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Repository_names_are_validated(string? repository, bool valid)
        => Assert.Equal(valid, new UpdateOptions { Repository = repository }.ResolveRepository() is not null);

    [Theory]
    [InlineData("1.2", true)]
    [InlineData("1.2.0", true)]
    [InlineData("1.2.0.7", true)]
    [InlineData("1", false)]
    [InlineData("1.2.0.0.0", false)]
    [InlineData("1.-2", false)]
    [InlineData("1.70000", false)]
    [InlineData(" 1.2", false)]
    public void Manifest_versions_are_strict(string text, bool valid)
        => Assert.Equal(valid, SignedUpdateManifest.TryParseVersion(text, out _));

    private sealed class SynchronousProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    /// <summary>Flux qui se coupe (erreur réseau) après <paramref name="cut"/> octets.</summary>
    private sealed class CutStream(byte[] data, int cut) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= cut) throw new IOException("connexion interrompue");
            var n = Math.Min(count, cut - _position);
            Array.Copy(data, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Réponses HTTP simulées (aucun accès réseau).</summary>
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, Func<HttpResponseMessage>> Responses { get; } = new(StringComparer.Ordinal);

    public List<Uri> Requests { get; } = [];

    public List<string> UserAgents { get; } = [];

    public bool Throw { get; set; }

    /// <summary>Réponse calculée à partir de la requête (en-têtes Range…), prioritaire sur <see cref="Responses"/>.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Handler { get; set; }

    public List<RangeHeaderValue?> Ranges { get; } = [];

    public static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        UserAgents.Add(request.Headers.UserAgent.ToString());
        Ranges.Add(request.Headers.Range);
        if (Throw) throw new HttpRequestException("hors ligne");
        var response = Handler?.Invoke(request)
                       ?? (Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var factory) ? factory() : new HttpResponseMessage(HttpStatusCode.NotFound));
        response.RequestMessage = request;
        return Task.FromResult(response);
    }
}
