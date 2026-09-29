using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Gaming.Common;

namespace PCBoost.Gaming.Optimizations;

/// <summary>
/// Préférence graphique « Hautes performances » pour l'exécutable du jeu (réglage documenté de Windows :
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c>, valeur = chemin de l'exécutable, donnée <c>GpuPreference=2;</c>),
/// comme dans Paramètres › Affichage › Graphiques. Utile uniquement avec deux processeurs graphiques dont un dédié.
/// Persistant : prend effet au prochain lancement du jeu. Jamais sélectionné par défaut.
/// </summary>
public sealed class GamingGpuPreferenceOptimization : GamingOptimizationBase
{
    internal static readonly RegistryLocation PreferencesKey = new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences");
    internal const string GpuPreferenceName = "GpuPreference";
    internal const string HighPerformanceValue = "2";

    private readonly IRegistryProvider _registry;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly ILogger<GamingGpuPreferenceOptimization> _logger;

    public GamingGpuPreferenceOptimization(IRegistryProvider registry, ISystemInfoProvider systemInfo, IServiceProvider? services = null, ILogger<GamingGpuPreferenceOptimization>? logger = null)
        : base(services)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _logger = logger ?? NullLogger<GamingGpuPreferenceOptimization>.Instance;
    }

    public override string Id => GamingOptimizationIds.GpuPreference;

    public override TextRef Name => TextRef.Of("Game_Opt_Gpu_Name");

    public override TextRef Description => TextRef.Of("Game_Opt_Gpu_Description");

    public override OptimizationCategory Category => OptimizationCategory.Gpu;

    public override RiskLevel RiskLevel => RiskLevel.Low;

    public override ImpactLevel ImpactLevel => ImpactLevel.Medium;

    public override Task<OptimizationPreview> PreviewAsync(OptimizationContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(BuildPreview(GetStatus(GetGameExecutablePath(context))));
    }

    public override async Task<OptimizationResult> ApplyAsync(OptimizationContext context, IChangeRecorder recorder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(recorder);
        var status = GetStatus(GetGameExecutablePath(context));
        var preview = BuildPreview(status);
        if (!preview.Applicable || status.ExecutablePath is null)
            return OptimizationResult.Skipped(Id, preview.NotApplicableReason ?? TextRef.Of("Game_Gpu_NoGamePath"));

        var change = preview.Changes[0];
        if (!context.IsSelected(change)) return OptimizationResult.Skipped(Id, TextRef.Of("Game_Gpu_NotSelected"));
        if (ForbiddenTargetPolicy.IsForbiddenRegistryLocation(PreferencesKey))
            return Result(OperationResult.Fail(OperationErrorKind.Blocked), 0, 1, TextRef.Of("Game_Gpu_Failed"));

        var exe = status.ExecutablePath;
        var current = _registry.GetValue(PreferencesKey, exe);
        var settings = DirectXSettingsString.Parse(current?.Value as string);
        settings.Set(GpuPreferenceName, HighPerformanceValue);
        var newValue = settings.ToString();

        var before = ChangeStateSerializer.Serialize(RegistryValueState.Capture(PreferencesKey, exe, current));
        var pending = new PendingChange(ChangeKinds.RegistryValue, Id, change.Target, change.Description, before, Reversible: true);
        var outcome = await recorder.ApplyAsync(pending, _ => Task.FromResult(_registry.SetValue(PreferencesKey, exe, RegistryValueData.String(newValue))), cancellationToken).ConfigureAwait(false);
        if (!outcome.Success) return Result(outcome, 0, 1, outcome.Message ?? TextRef.Of("Game_Gpu_Failed"));

        _logger.LogInformation("Préférence graphique « Hautes performances » enregistrée pour {Exe}", WinPath.GetFileName(exe));
        return Result(outcome, 1, 0, TextRef.Of("Game_Gpu_Applied", WinPath.GetFileNameWithoutExtension(exe)));
    }

    /// <summary>État de la préférence graphique pour cet exécutable.</summary>
    internal GpuPreferenceStatus GetStatus(string? executablePath)
    {
        var exe = string.IsNullOrWhiteSpace(executablePath) ? null : WinPath.Normalize(executablePath);
        if (exe is null) return new GpuPreferenceStatus(GpuPreferenceState.NoGamePath, null, null);

        var gpus = _systemInfo.GetGpus().Where(g => !g.IsSoftwareAdapter).ToList();
        if (gpus.Count < 2 || !gpus.Any(g => !g.IsLikelyIntegrated))
            return new GpuPreferenceStatus(GpuPreferenceState.SingleGpu, exe, null);

        var current = _registry.GetValue(PreferencesKey, exe)?.Value as string;
        var value = DirectXSettingsString.Parse(current).Get(GpuPreferenceName);
        return new GpuPreferenceStatus(value == HighPerformanceValue ? GpuPreferenceState.AlreadyHighPerformance : GpuPreferenceState.NotSet, exe, current);
    }

    internal PlannedChange? BuildChange(GpuPreferenceStatus status)
        => status.State != GpuPreferenceState.NotSet || status.ExecutablePath is null ? null : new PlannedChange(
            $"{Id}:{WinPath.Key(status.ExecutablePath)}",
            TextRef.Of("Game_Gpu_Change", WinPath.GetFileNameWithoutExtension(status.ExecutablePath)),
            $@"HKCU\{PreferencesKey.KeyPath} : {WinPath.GetFileName(status.ExecutablePath)}",
            SelectedByDefault: false,
            Reversible: true,
            RiskLevel.Low);

    private OptimizationPreview BuildPreview(GpuPreferenceStatus status) => status.State switch
    {
        GpuPreferenceState.NoGamePath => NotApplicable(TextRef.Of("Game_Gpu_NoGamePath")),
        GpuPreferenceState.SingleGpu => NotApplicable(TextRef.Of("Game_Gpu_SingleGpu")),
        GpuPreferenceState.AlreadyHighPerformance => NotApplicable(TextRef.Of("Game_Gpu_AlreadySet")),
        _ => Applicable([BuildChange(status)!]),
    };

    internal enum GpuPreferenceState { NoGamePath, SingleGpu, AlreadyHighPerformance, NotSet }

    internal sealed record GpuPreferenceStatus(GpuPreferenceState State, string? ExecutablePath, string? CurrentValue);
}
