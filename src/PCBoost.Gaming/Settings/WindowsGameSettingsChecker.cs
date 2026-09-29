using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Optimization;
using PCBoost.Core.Security;
using PCBoost.Core.Services;
using PCBoost.Gaming.Common;
using PCBoost.Gaming.Optimizations;

namespace PCBoost.Gaming.Settings;

/// <summary>
/// Vérifie les paramètres de jeu de Windows (lecture seule) et corrige, à la demande explicite de l'utilisateur, ceux qui
/// peuvent l'être de façon réversible (session « Manual » du journal de restauration) :
/// <list type="bullet">
/// <item>Mode Jeu (<c>HKCU\Software\Microsoft\GameBar</c> AutoGameModeEnabled : absent ou 1 = activé) — corrigeable ;</item>
/// <item>optimisations pour les jeux fenêtrés (Windows 11, <c>DirectXUserGlobalSettings</c> contenant <c>SwapEffectUpgradeEnable=1</c>) — corrigeable ;</item>
/// <item>planification GPU à accélération matérielle (<c>HwSchMode</c> = 2) — lecture seule, jamais modifiée : conseil via Paramètres ;</item>
/// <item>préférence graphique du jeu (deux GPU dont un dédié) — corrigeable.</item>
/// </list>
/// </summary>
public sealed class WindowsGameSettingsChecker
{
    internal static readonly RegistryLocation GameBarKey = new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar");
    internal const string AutoGameModeValue = "AutoGameModeEnabled";
    internal static readonly RegistryLocation GraphicsDriversKey = new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
    internal const string HwSchModeValue = "HwSchMode";
    internal const string GlobalSettingsValue = "DirectXUserGlobalSettings";
    internal const string SwapEffectUpgradeName = "SwapEffectUpgradeEnable";

    /// <summary>Identifiant d'optimisation consigné pour les corrections de paramètres Windows.</summary>
    public const string FixOptimizationId = "gaming-windows-settings";

    private const int Windows11Build = 22000;

    private readonly IRegistryProvider _registry;
    private readonly ISystemInfoProvider _systemInfo;
    private readonly IRollbackManager _rollback;
    private readonly GamingGpuPreferenceOptimization _gpuPreference;
    private readonly ILogger<WindowsGameSettingsChecker> _logger;

    public WindowsGameSettingsChecker(
        IRegistryProvider registry,
        ISystemInfoProvider systemInfo,
        IRollbackManager rollback,
        GamingGpuPreferenceOptimization gpuPreference,
        ILogger<WindowsGameSettingsChecker>? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _systemInfo = systemInfo ?? throw new ArgumentNullException(nameof(systemInfo));
        _rollback = rollback ?? throw new ArgumentNullException(nameof(rollback));
        _gpuPreference = gpuPreference ?? throw new ArgumentNullException(nameof(gpuPreference));
        _logger = logger ?? NullLogger<WindowsGameSettingsChecker>.Instance;
    }

    public IReadOnlyList<GameSettingCheck> Check(DetectedGameProcess? game)
    {
        var checks = new List<GameSettingCheck> { CheckGameMode() };
        if (IsWindows11()) checks.Add(CheckWindowedOptimizations());
        checks.Add(CheckHardwareScheduling());
        var gpu = CheckGpuPreference(game);
        if (gpu is not null) checks.Add(gpu);
        return checks;
    }

    /// <summary>
    /// Applique la correction d'un élément <see cref="GameSettingCheck.CanFixAutomatically"/>, consignée dans une session
    /// « Manual » annulable depuis l'historique. La planification GPU n'est jamais modifiée.
    /// </summary>
    public async Task<OperationResult> FixAsync(string checkId, DetectedGameProcess? game, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkId);
        var check = Check(game).FirstOrDefault(c => string.Equals(c.Id, checkId, StringComparison.Ordinal));
        if (check is null) return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Game_Setting_NotFound"));
        if (check.IsRecommendedState) return OperationResult.Ok(TextRef.Of("Game_Setting_AlreadyOk"));
        if (!check.CanFixAutomatically) return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Game_Setting_ManualOnly"));

        try
        {
            var recorder = await _rollback.BeginSessionAsync(SessionType.Manual, TextRef.Of("Game_Setting_SessionTitle"), null, cancellationToken).ConfigureAwait(false);
            OperationResult outcome;
            try
            {
                outcome = checkId switch
                {
                    GameSettingCheckIds.GameMode => await SetRegistryAsync(recorder, GameBarKey, AutoGameModeValue, RegistryValueData.DWord(1), check.Label, cancellationToken).ConfigureAwait(false),
                    GameSettingCheckIds.WindowedOptimizations => await EnableWindowedOptimizationsAsync(recorder, check.Label, cancellationToken).ConfigureAwait(false),
                    GameSettingCheckIds.GpuPreference => await ApplyGpuPreferenceAsync(recorder, game, cancellationToken).ConfigureAwait(false),
                    _ => OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Game_Setting_ManualOnly")),
                };
            }
            finally
            {
                await _rollback.CompleteSessionAsync(recorder.SessionId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            _logger.LogInformation("Paramètre de jeu Windows {Check} corrigé : {Success}", checkId, outcome.Success);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            return OperationResult.Fail(OperationErrorKind.Cancelled);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Correction du paramètre de jeu {Check} impossible", checkId);
            return OperationResult.FromException(ex);
        }
    }

    private GameSettingCheck CheckGameMode()
    {
        var value = ScannerNumber(GameBarKey, AutoGameModeValue);
        var enabled = value is null || value == 1;
        return new GameSettingCheck(
            GameSettingCheckIds.GameMode,
            TextRef.Of("Game_Setting_GameMode"),
            TextRef.Of(enabled ? "Game_Setting_GameMode_On" : "Game_Setting_GameMode_Off"),
            IsRecommendedState: enabled,
            CanFixAutomatically: !enabled);
    }

    private GameSettingCheck CheckWindowedOptimizations()
    {
        var current = _registry.GetValue(GamingGpuPreferenceOptimization.PreferencesKey, GlobalSettingsValue)?.Value as string;
        var enabled = DirectXSettingsString.Parse(current).Get(SwapEffectUpgradeName) == "1";
        return new GameSettingCheck(
            GameSettingCheckIds.WindowedOptimizations,
            TextRef.Of("Game_Setting_Windowed"),
            TextRef.Of(enabled ? "Game_Setting_Windowed_On" : "Game_Setting_Windowed_Off"),
            IsRecommendedState: enabled,
            CanFixAutomatically: !enabled);
    }

    private GameSettingCheck CheckHardwareScheduling()
    {
        var value = ScannerNumber(GraphicsDriversKey, HwSchModeValue);
        var (detail, recommended) = value switch
        {
            2 => ("Game_Setting_Hags_On", true),
            1 => ("Game_Setting_Hags_Off", false),
            // Valeur absente : non prise en charge par le pilote ou jamais configurée — aucune action proposée.
            _ => ("Game_Setting_Hags_Unknown", true),
        };
        return new GameSettingCheck(
            GameSettingCheckIds.HardwareGpuScheduling,
            TextRef.Of("Game_Setting_Hags"),
            TextRef.Of(detail),
            IsRecommendedState: recommended,
            CanFixAutomatically: false);
    }

    private GameSettingCheck? CheckGpuPreference(DetectedGameProcess? game)
    {
        if (game is null) return null;
        var status = _gpuPreference.GetStatus(game.ExecutablePath ?? game.Game.ExecutablePath);
        var name = game.Game.Name;
        return status.State switch
        {
            GamingGpuPreferenceOptimization.GpuPreferenceState.NoGamePath => new GameSettingCheck(GameSettingCheckIds.GpuPreference,
                TextRef.Of("Game_Setting_Gpu"), TextRef.Of("Game_Setting_Gpu_NoPath", name), IsRecommendedState: true, CanFixAutomatically: false),
            GamingGpuPreferenceOptimization.GpuPreferenceState.SingleGpu => new GameSettingCheck(GameSettingCheckIds.GpuPreference,
                TextRef.Of("Game_Setting_Gpu"), TextRef.Of("Game_Setting_Gpu_Single"), IsRecommendedState: true, CanFixAutomatically: false),
            GamingGpuPreferenceOptimization.GpuPreferenceState.AlreadyHighPerformance => new GameSettingCheck(GameSettingCheckIds.GpuPreference,
                TextRef.Of("Game_Setting_Gpu"), TextRef.Of("Game_Setting_Gpu_On", name), IsRecommendedState: true, CanFixAutomatically: false),
            _ => new GameSettingCheck(GameSettingCheckIds.GpuPreference,
                TextRef.Of("Game_Setting_Gpu"), TextRef.Of("Game_Setting_Gpu_Off", name), IsRecommendedState: false, CanFixAutomatically: true),
        };
    }

    private async Task<OperationResult> EnableWindowedOptimizationsAsync(IChangeRecorder recorder, TextRef label, CancellationToken cancellationToken)
    {
        var location = GamingGpuPreferenceOptimization.PreferencesKey;
        var current = _registry.GetValue(location, GlobalSettingsValue);
        var settings = DirectXSettingsString.Parse(current?.Value as string);
        settings.Set(SwapEffectUpgradeName, "1"); // les autres paires « clé=valeur; » sont conservées
        return await SetRegistryAsync(recorder, location, GlobalSettingsValue, RegistryValueData.String(settings.ToString()), label, cancellationToken, current).ConfigureAwait(false);
    }

    private async Task<OperationResult> ApplyGpuPreferenceAsync(IChangeRecorder recorder, DetectedGameProcess? game, CancellationToken cancellationToken)
    {
        if (game is null) return OperationResult.Fail(OperationErrorKind.InvalidInput, TextRef.Of("Game_Gpu_NoGamePath"));
        var path = game.ExecutablePath ?? game.Game.ExecutablePath;
        var change = _gpuPreference.BuildChange(_gpuPreference.GetStatus(path));
        if (change is null || path is null) return OperationResult.Ok(TextRef.Of("Game_Setting_AlreadyOk"));
        // Sélection explicite : ce module n'est jamais sélectionné par défaut.
        var context = new OptimizationContext(null, new HashSet<string>(StringComparer.Ordinal) { change.Id });
        context.Items[GamingContextKeys.GameExecutablePath] = path;
        var result = await _gpuPreference.ApplyAsync(context, recorder, cancellationToken).ConfigureAwait(false);
        return result.Outcome;
    }

    private async Task<OperationResult> SetRegistryAsync(IChangeRecorder recorder, RegistryLocation location, string valueName, RegistryValueData data,
        TextRef label, CancellationToken cancellationToken, RegistryValueData? current = null)
    {
        if (ForbiddenTargetPolicy.IsForbiddenRegistryLocation(location))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Game_Setting_Blocked"));
        current ??= _registry.GetValue(location, valueName);
        var before = ChangeStateSerializer.Serialize(RegistryValueState.Capture(location, valueName, current));
        var pending = new PendingChange(ChangeKinds.RegistryValue, FixOptimizationId, $"{location} : {valueName}", label, before, Reversible: true);
        return await recorder.ApplyAsync(pending, _ => Task.FromResult(_registry.SetValue(location, valueName, data)), cancellationToken).ConfigureAwait(false);
    }

    private long? ScannerNumber(RegistryLocation location, string valueName)
        => Detection.Scanners.ScannerSupport.GetNumber(_registry, location, valueName);

    private bool IsWindows11()
    {
        try
        {
            var os = _systemInfo.GetOsInfo();
            return os.IsWindows11 || os.BuildNumber >= Windows11Build;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Version de Windows illisible");
            return false;
        }
    }
}
