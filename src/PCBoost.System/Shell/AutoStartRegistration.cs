using System.Reflection;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;

namespace PCBoost.Platform;

/// <summary>
/// Lancement avec Windows pour l'utilisateur courant : valeur HKCU\…\Run nommée d'après le produit,
/// donnée « "&lt;chemin exe&gt;" --background ». L'état « désactivé » du Gestionnaire des tâches (StartupApproved) est respecté.
/// </summary>
public sealed class AutoStartRegistration : IAutoStartRegistration
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    internal const string BackgroundArgument = "--background";

    private readonly ILogger<AutoStartRegistration> _logger;
    private readonly string _valueName;
    private readonly Func<string?> _executablePath;

    public AutoStartRegistration(ILogger<AutoStartRegistration>? logger = null)
        : this(ProductName(), () => Environment.ProcessPath, logger)
    {
    }

    internal AutoStartRegistration(string valueName, Func<string?> executablePath, ILogger<AutoStartRegistration>? logger)
    {
        _valueName = valueName;
        _executablePath = executablePath;
        _logger = logger ?? NullLogger<AutoStartRegistration>.Instance;
    }

    public bool IsEnabled()
    {
        var exe = _executablePath();
        if (string.IsNullOrEmpty(exe)) return false;
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(_valueName) is not string command || !PointsTo(command, exe)) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey);
            return !IsDisabledByUser(approved?.GetValue(_valueName) as byte[]);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _logger.LogDebug(ex, "Lecture du lancement automatique impossible");
            return false;
        }
    }

    public OperationResult SetEnabled(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                run?.DeleteValue(_valueName, throwOnMissingValue: false);
                _logger.LogInformation("Lancement avec Windows désactivé");
                return OperationResult.Ok();
            }

            var exe = _executablePath();
            if (string.IsNullOrEmpty(exe) || !Path.IsPathFullyQualified(exe))
                return OperationResult.Fail(OperationErrorKind.NotSupported, TextRef.Of("Sys_AutoStartUnavailable"));

            using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
                run.SetValue(_valueName, BuildCommand(exe), RegistryValueKind.String);

            // L'utilisateur active explicitement le lancement : on lève un éventuel « désactivé » du Gestionnaire des tâches.
            using (var approved = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: true))
            {
                if (approved?.GetValue(_valueName) is byte[] state && IsDisabledByUser(state))
                    approved.SetValue(_valueName, EnabledApprovalState(), RegistryValueKind.Binary);
            }
            _logger.LogInformation("Lancement avec Windows activé");
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return OperationResult.FromException(ex);
        }
    }

    internal static string BuildCommand(string executablePath) => $"\"{executablePath}\" {BackgroundArgument}";

    /// <summary>La commande enregistrée désigne-t-elle cet exécutable (guillemets facultatifs, casse ignorée) ?</summary>
    internal static bool PointsTo(string command, string executablePath)
    {
        var trimmed = command.Trim();
        string target;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            if (end < 0) return false;
            target = trimmed[1..end];
        }
        else
        {
            var space = trimmed.IndexOf(" --", StringComparison.Ordinal);
            target = space < 0 ? trimmed : trimmed[..space];
        }
        return string.Equals(target.Trim(), executablePath.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>StartupApproved : premier octet impair (0x01, 0x03) = désactivé par l'utilisateur ; 0x02, 0x06 = activé.</summary>
    internal static bool IsDisabledByUser(byte[]? approvalState)
        => approvalState is { Length: > 0 } && (approvalState[0] & 0x01) != 0;

    internal static byte[] EnabledApprovalState() => [0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

    private static string ProductName()
    {
        var product = typeof(AutoStartRegistration).Assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
        return string.IsNullOrWhiteSpace(product) ? "PCBoost" : product.Trim();
    }
}
