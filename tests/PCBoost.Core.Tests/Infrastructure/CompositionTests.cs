using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Branding;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Services;
using PCBoost.Infrastructure;
using PCBoost.Infrastructure.Localization;
using PCBoost.Infrastructure.Logging;
using PCBoost.Infrastructure.Settings;
using PCBoost.Infrastructure.Updates;
using PCBoost.Persistence;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class CompositionTests
{
    [Fact]
    public void AppInfo_uses_branding_versions_and_default_directories()
    {
        var options = new InfrastructureOptions
        {
            Branding = new BrandingOptions { ProductName = "MonOutil", DataFolderName = "MonOutil" },
            WindowsAppSdkVersion = " 2.3.9 ",
        };

        var info = new AppInfo(options);

        Assert.Equal("MonOutil", info.ProductName);
        Assert.Equal("MonOutil", Path.GetFileName(info.DataDirectory));
        Assert.True(Path.IsPathRooted(info.DataDirectory));
        Assert.Equal(Path.Combine(info.DataDirectory, "logs"), info.LogDirectory);
        Assert.Equal("2.3.9", info.WindowsAppSdkVersion);
        Assert.Contains(".NET", info.DotNetVersion, StringComparison.Ordinal);
        Assert.NotNull(info.Version);
    }

    [Fact]
    public void Explicit_directories_are_honoured_and_invalid_folder_names_fall_back()
    {
        using var temp = new TempDirectory();
        var custom = new InfrastructureOptions { DataDirectory = temp.Path, LogDirectory = temp.File("journaux") };
        var hostile = new InfrastructureOptions { Branding = new BrandingOptions { DataFolderName = @"..\..\Windows" } };

        Assert.Equal(temp.Path, new AppInfo(custom).DataDirectory);
        Assert.Equal(temp.File("journaux"), new AppInfo(custom).LogDirectory);
        Assert.Equal(Path.Combine(temp.Path, "updates"), custom.ResolveUpdatesDirectory());
        Assert.Equal(Path.Combine(temp.Path, InfrastructureOptions.DatabaseFileName), custom.ResolveDatabasePath());
        Assert.Equal("PCBoost", Path.GetFileName(hostile.ResolveDataDirectory()));
        Assert.Equal(string.Empty, new AppInfo(new InfrastructureOptions()).WindowsAppSdkVersion);
    }

    [Fact]
    public async Task Infrastructure_and_persistence_compose_into_a_working_container()
    {
        using var temp = new TempDirectory();
        var services = new ServiceCollection();
        services.AddSingleton<IStringResourceSource>(new DictionaryStringSource(new() { ["Common_NotAvailable"] = "Texte d'un autre module" }));
        services.AddLogging();
        services.AddPCBoostPersistence(Path.Combine(temp.Path, InfrastructureOptions.DatabaseFileName));
        services.AddPCBoostInfrastructure(o => o.DataDirectory = temp.Path);
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IDatabaseInitializer>().InitializeAsync();
        var sources = provider.GetServices<IStringResourceSource>().ToList();
        var localizer = provider.GetRequiredService<ILocalizer>();
        localizer.SetLanguage("fr");
        var settings = provider.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        var journal = provider.GetRequiredService<IActivityJournal>();
        await journal.LogAsync(ActivityKind.Info, TextRef.Of("Infra_Update_Verified"));
        var update = await provider.GetRequiredService<IUpdateService>().CheckForUpdatesAsync();

        Assert.IsType<ResourceManagerStringSource>(sources[0]);
        Assert.Equal(2, sources.Count);
        Assert.Equal("Non disponible", localizer.Get("Common_NotAvailable"));
        Assert.Same(provider.GetRequiredService<Localizer>(), localizer);
        Assert.Same(provider.GetRequiredService<SettingsService>(), settings);
        Assert.True(((SettingsService)settings).IsLoaded);
        Assert.Single(await journal.GetRecentAsync());
        Assert.Equal(UpdateCheckStatus.NotConfigured, update.Status);
        Assert.Collection(provider.GetServices<IUpdateProvider>(),
            p => Assert.IsType<LocalUpdateProvider>(p),
            p => Assert.IsType<PlaceholderRemoteUpdateProvider>(p));
        Assert.Equal(Path.Combine(temp.Path, "updates"), provider.GetRequiredService<UpdateService>().DownloadDirectory);
        Assert.Equal(temp.Path, provider.GetRequiredService<IAppInfo>().DataDirectory);
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.Same(provider.GetRequiredService<InfrastructureOptions>().Branding, provider.GetRequiredService<BrandingOptions>());
    }

    [Fact]
    public void Existing_clock_registration_is_kept()
    {
        var clock = new FakeClock();
        var services = new ServiceCollection().AddSingleton<IClock>(clock).AddLogging();
        services.AddPCBoostInfrastructure();
        using var provider = services.BuildServiceProvider();

        Assert.Same(clock, provider.GetRequiredService<IClock>());
    }

    [Fact]
    public void Logging_host_is_plugged_into_dependency_injection()
    {
        using var temp = new TempDirectory();
        using (var logging = PCBoostLogging.Create(temp.Path, verbose: false, new SensitiveDataRedactor(null, null, null)))
        {
            var services = new ServiceCollection().AddPCBoostLogging(logging);
            using var provider = services.BuildServiceProvider();

            Assert.Same(logging.LoggerFactory, provider.GetRequiredService<ILoggerFactory>());
            Assert.Same(logging, provider.GetRequiredService<PCBoostLoggingHost>());
            provider.GetRequiredService<ILogger<CompositionTests>>().LogWarning("message-injecte");
        }

        var content = File.ReadAllText(Assert.Single(Directory.GetFiles(temp.Path, "pcboost-*.log")));
        Assert.Contains("message-injecte", content);
        Assert.Contains(typeof(CompositionTests).FullName!, content);
    }

    [Fact]
    public async Task Settings_bindings_apply_language_and_log_level_until_disposed()
    {
        using var temp = new TempDirectory();
        var settings = new FakeSettingsService();
        var localizer = new Localizer([PCBoost.Infrastructure.ServiceCollectionExtensions.CreateCommonStringSource()], NullLogger<Localizer>.Instance,
            CultureInfo.GetCultureInfo("fr-FR"), CultureInfo.GetCultureInfo("fr-FR"));
        using var logging = PCBoostLogging.Create(temp.Path, verbose: false, new SensitiveDataRedactor(null, null, null));
        var initial = settings.Current.Clone();
        initial.Language = "en";
        initial.VerboseLogging = true;
        await settings.SaveAsync(initial);

        var binding = SettingsBindings.Bind(settings, localizer, logging);
        Assert.Equal("Not available", localizer.Get("Common_NotAvailable"));
        Assert.True(logging.IsVerbose);

        var french = settings.Current.Clone();
        french.Language = "fr";
        french.VerboseLogging = false;
        await settings.SaveAsync(french);
        Assert.Equal("Non disponible", localizer.Get("Common_NotAvailable"));
        Assert.False(logging.IsVerbose);

        binding.Dispose();
        var english = settings.Current.Clone();
        english.Language = "en";
        await settings.SaveAsync(english);
        Assert.Equal("Non disponible", localizer.Get("Common_NotAvailable"));
    }
}
