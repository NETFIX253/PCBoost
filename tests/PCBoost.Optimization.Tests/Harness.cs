using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Processes;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

/// <summary>Graphe complet du module construit par <c>AddPCBoostOptimization</c> sur des faux déterministes.</summary>
internal sealed class Harness : IDisposable
{
    private ServiceProvider? _provider;

    public FakeClock Clock { get; } = new();
    public InMemoryRegistryProvider Registry { get; } = new();
    public InMemoryFileSystemProvider FileSystem { get; } = new();
    public FakeProcessProvider Processes { get; } = new();
    public FakePowerProvider Power { get; } = new();
    public FakeVisualEffectsProvider Visual { get; } = new();
    public FakeRecycleBinProvider RecycleBin { get; } = new();
    public IScheduledTaskProvider Tasks { get; set; } = new FakeScheduledTaskProvider();
    public FakeElevationService Elevation { get; } = new();
    public FakeSignatureVerifier Signatures { get; } = new();
    public FakeFileMetadataProvider Metadata { get; } = new();
    public FakeShortcutResolver Shortcuts { get; } = new();
    public FakeSystemInfoProvider SystemInfo { get; } = new();
    public FakeSettingsService Settings { get; } = new();
    public FakeActivityJournal Journal { get; } = new();
    public FakeNotificationService Notifications { get; } = new();
    public InMemoryOptimizationHistoryRepository History { get; } = new();
    public InMemoryKeyValueStore Store { get; } = new();
    public FakeForegroundWindowProvider Foreground { get; } = new();
    public FakePerformanceMonitor Monitor { get; } = new();
    public FakeGamingService Gaming { get; } = new();
    public FakeSystemAnalyzer Analyzer { get; } = new();

    /// <summary>Appelé pendant le délai entre les deux premiers échantillons de processus (le temps avance de 500 ms).</summary>
    public Action? OnProcessSampleDelay { get; set; }

    public List<IOptimization> ExtraOptimizations { get; } = [];
    public List<IChangeHandler> ExtraHandlers { get; } = [];

    public FakeScheduledTaskProvider FakeTasks => (FakeScheduledTaskProvider)Tasks;

    public IServiceProvider Services => _provider ??= Build();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<IRegistryProvider>(Registry);
        services.AddSingleton<IFileSystemProvider>(FileSystem);
        services.AddSingleton<IProcessProvider>(Processes);
        services.AddSingleton<IProcessControl>(Processes);
        services.AddSingleton<IPowerProvider>(Power);
        services.AddSingleton<IVisualEffectsProvider>(Visual);
        services.AddSingleton<IRecycleBinProvider>(RecycleBin);
        services.AddSingleton(Tasks);
        services.AddSingleton<IElevationService>(Elevation);
        services.AddSingleton<ISignatureVerifier>(Signatures);
        services.AddSingleton<IFileMetadataProvider>(Metadata);
        services.AddSingleton<IShortcutResolver>(Shortcuts);
        services.AddSingleton<ISystemInfoProvider>(SystemInfo);
        services.AddSingleton<ISettingsService>(Settings);
        services.AddSingleton<IActivityJournal>(Journal);
        services.AddSingleton<INotificationService>(Notifications);
        services.AddSingleton<IOptimizationHistoryRepository>(History);
        services.AddSingleton<IKeyValueStore>(Store);
        services.AddSingleton<IForegroundWindowProvider>(Foreground);
        services.AddSingleton<IPerformanceMonitor>(Monitor);
        services.AddSingleton<IGamingService>(Gaming);
        services.AddSingleton<ISystemAnalyzer>(Analyzer);
        foreach (var optimization in ExtraOptimizations) services.AddSingleton(optimization);
        foreach (var handler in ExtraHandlers) services.AddSingleton(handler);

        // Échantillonnage des processus sans attente réelle : le temps simulé avance de 500 ms.
        services.AddSingleton<IProcessService>(sp => new ProcessService(
            Processes, Processes, sp.GetRequiredService<ISecurityService>(), sp.GetRequiredService<ICriticalProcessProtection>(),
            SystemInfo, Journal, Clock, null, (delay, _) =>
            {
                Clock.Advance(delay);
                OnProcessSampleDelay?.Invoke();
                return Task.CompletedTask;
            }));

        services.AddPCBoostOptimization();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    /// <summary>Crée un fichier exécutable signé (éditeur connu) dans le système de fichiers simulé.</summary>
    public string AddSignedExe(string path, string signer, string? description = null)
    {
        FileSystem.AddFile(path, 1024);
        Signatures.Signatures[path] = new Core.Models.Processes.SignatureInfo(Core.Models.Processes.SignatureStatus.Signed, signer);
        if (description is not null)
            Metadata.Files[path] = new Core.Models.Processes.FileVersionMetadata(signer, description, description, "1.0");
        return path;
    }

    public void Dispose() => _provider?.Dispose();
}
