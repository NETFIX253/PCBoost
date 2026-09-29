using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>
/// Effets visuels de l'utilisateur courant : SystemParametersInfo (persistés avec SPIF_UPDATEINIFILE | SPIF_SENDCHANGE)
/// et transparence (HKCU\…\Themes\Personalize\EnableTransparency).
/// </summary>
public sealed unsafe class VisualEffectsProvider : IVisualEffectsProvider
{
    internal const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string TransparencyValue = "EnableTransparency";

    private readonly ILogger<VisualEffectsProvider> _logger;

    public VisualEffectsProvider(ILogger<VisualEffectsProvider>? logger = null)
    {
        _logger = logger ?? NullLogger<VisualEffectsProvider>.Instance;
    }

    public VisualEffectsState? GetState()
    {
        try
        {
            var clientArea = GetBool(User32.SPI_GETCLIENTAREAANIMATION);
            var menu = GetBool(User32.SPI_GETMENUANIMATION);
            var combo = GetBool(User32.SPI_GETCOMBOBOXANIMATION);
            var listBox = GetBool(User32.SPI_GETLISTBOXSMOOTHSCROLLING);
            var tooltip = GetBool(User32.SPI_GETTOOLTIPANIMATION);
            var minMax = GetMinMaxAnimation();
            var cursorShadow = GetBool(User32.SPI_GETCURSORSHADOW);
            var dragFull = GetBool(User32.SPI_GETDRAGFULLWINDOWS);
            if (clientArea is null || menu is null || combo is null || listBox is null || tooltip is null
                || minMax is null || cursorShadow is null || dragFull is null)
                return null;

            return new VisualEffectsState(clientArea.Value, menu.Value, combo.Value, listBox.Value, tooltip.Value,
                minMax.Value, cursorShadow.Value, dragFull.Value, GetTransparency());
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _logger.LogDebug(ex, "SystemParametersInfo indisponible");
            return null;
        }
    }

    public OperationResult SetState(VisualEffectsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var current = GetState();
        var failures = new List<string>();

        void Apply(string name, bool target, bool? now, Func<bool, bool> setter)
        {
            if (now == target) return;
            if (!setter(target)) failures.Add($"{name} (Win32 {Marshal.GetLastPInvokeError()})");
        }

        Apply("ClientAreaAnimation", state.ClientAreaAnimation, current?.ClientAreaAnimation, v => SetBoolByValue(User32.SPI_SETCLIENTAREAANIMATION, v));
        Apply("MenuAnimation", state.MenuAnimation, current?.MenuAnimation, v => SetBoolByValue(User32.SPI_SETMENUANIMATION, v));
        Apply("ComboBoxAnimation", state.ComboBoxAnimation, current?.ComboBoxAnimation, v => SetBoolByValue(User32.SPI_SETCOMBOBOXANIMATION, v));
        Apply("ListBoxSmoothScrolling", state.ListBoxSmoothScrolling, current?.ListBoxSmoothScrolling, v => SetBoolByValue(User32.SPI_SETLISTBOXSMOOTHSCROLLING, v));
        Apply("TooltipAnimation", state.TooltipAnimation, current?.TooltipAnimation, v => SetBoolByValue(User32.SPI_SETTOOLTIPANIMATION, v));
        Apply("WindowMinMaxAnimation", state.WindowMinMaxAnimation, current?.WindowMinMaxAnimation, SetMinMaxAnimation);
        Apply("CursorShadow", state.CursorShadow, current?.CursorShadow, v => SetBoolByValue(User32.SPI_SETCURSORSHADOW, v));
        // SPI_SETDRAGFULLWINDOWS prend la valeur dans uiParam (et non pvParam).
        Apply("DragFullWindows", state.DragFullWindows, current?.DragFullWindows,
            v => User32.SystemParametersInfo(User32.SPI_SETDRAGFULLWINDOWS, v ? 1u : 0u, null, User32.SPIF_UPDATEINIFILE | User32.SPIF_SENDCHANGE));
        if (current?.Transparency != state.Transparency && !SetTransparency(state.Transparency))
            failures.Add("Transparency");

        if (failures.Count == 0)
        {
            _logger.LogInformation("Effets visuels appliqués");
            return OperationResult.Ok();
        }
        _logger.LogWarning("Effets visuels partiellement appliqués : {Failures}", string.Join(", ", failures));
        return OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_VisualEffectsPartial"), string.Join(", ", failures));
    }

    private static bool? GetBool(uint action)
    {
        int value = 0;
        return User32.SystemParametersInfo(action, 0, &value, 0) ? value != 0 : null;
    }

    /// <summary>Pour ces actions SPI_SETxxx, la valeur BOOL est passée directement comme valeur de pvParam.</summary>
    private static bool SetBoolByValue(uint action, bool value)
        => User32.SystemParametersInfo(action, 0, (void*)(value ? 1 : 0), User32.SPIF_UPDATEINIFILE | User32.SPIF_SENDCHANGE);

    private static bool? GetMinMaxAnimation()
    {
        var info = new User32.ANIMATIONINFO { cbSize = (uint)sizeof(User32.ANIMATIONINFO) };
        return User32.SystemParametersInfo(User32.SPI_GETANIMATION, info.cbSize, &info, 0) ? info.iMinAnimate != 0 : null;
    }

    private static bool SetMinMaxAnimation(bool enabled)
    {
        var info = new User32.ANIMATIONINFO { cbSize = (uint)sizeof(User32.ANIMATIONINFO), iMinAnimate = enabled ? 1 : 0 };
        return User32.SystemParametersInfo(User32.SPI_SETANIMATION, info.cbSize, &info, User32.SPIF_UPDATEINIFILE | User32.SPIF_SENDCHANGE);
    }

    /// <summary>Valeur absente : Windows applique la transparence par défaut.</summary>
    private bool GetTransparency()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue(TransparencyValue) is not int value || value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Transparence illisible");
            return true;
        }
    }

    private bool SetTransparency(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey, writable: true);
            key.SetValue(TransparencyValue, enabled ? 1 : 0, RegistryValueKind.DWord);
            // Notifie l'interpréteur de commandes (prise en compte immédiate par l'Explorateur).
            User32.SendMessageTimeout(User32.HWND_BROADCAST, User32.WM_SETTINGCHANGE, 0, "ImmersiveColorSet", User32.SMTO_ABORTIFHUNG, 1000, out _);
            return true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogWarning(ex, "Transparence non modifiable");
            return false;
        }
    }
}
