using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;

namespace PCBoost.Platform;

public static class ServiceCollectionExtensions
{
    /// <summary>Enregistre les fournisseurs Windows (singletons) et les ressources de texte « Sys_* » du module.</summary>
    public static IServiceCollection AddPCBoostPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IClock, SystemClock>();

        services.AddSingleton<IProcessProvider, ProcessProvider>();
        services.AddSingleton<IProcessControl, ProcessControl>();
        services.AddSingleton<ISystemInfoProvider, SystemInfoProvider>();
        services.AddSingleton<ISystemMetricsProvider, SystemMetricsProvider>();
        services.AddSingleton<IHardwareProvider, HardwareProvider>();
        services.AddSingleton<IRegistryProvider, RegistryProvider>();
        services.AddSingleton<IFileSystemProvider, FileSystemProvider>();
        services.AddSingleton<IFileMetadataProvider, FileMetadataProvider>();
        services.AddSingleton<ISignatureVerifier, SignatureVerifier>();
        services.AddSingleton<IShortcutResolver, ShortcutResolver>();
        services.AddSingleton<IRecycleBinProvider, RecycleBinProvider>();
        services.AddSingleton<IPowerProvider, PowerProvider>();
        services.AddSingleton<IVisualEffectsProvider, VisualEffectsProvider>();
        services.AddSingleton<IForegroundWindowProvider, ForegroundWindowProvider>();
        services.AddSingleton<IScheduledTaskProvider, ScheduledTaskProvider>();
        services.AddSingleton<IAutoStartRegistration, AutoStartRegistration>();
        services.AddSingleton<IShellService, ShellService>();
        services.AddSingleton<IUninstallerLauncher, Programs.UninstallerLauncher>();
        services.AddSingleton<ICommandRunner, CommandRunner>();
        services.AddSingleton<IElevationService, ElevationService>();
        services.AddSingleton<IFrameTimeSource, FrameTimeSource>();
        services.AddSingleton<IHardwareHealthProvider, Health.HardwareHealthProvider>();
        services.AddSingleton<IDeviceDriverProvider, Drivers.DeviceDriverProvider>();
        services.AddSingleton<IDriverUpdateSource, Drivers.WindowsUpdateDriverSource>();
        services.AddSingleton<INetworkCostProvider, Drivers.NetworkCostProvider>();

        services.AddSingleton<IStringResourceSource>(
            ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Platform.Resources.Strings"));
        return services;
    }
}
