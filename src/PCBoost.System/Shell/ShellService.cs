using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Platform.Interop;

namespace PCBoost.Platform;

/// <summary>Actions explicites de l'utilisateur vers l'Explorateur et le navigateur. Aucune commande n'est construite par concaténation.</summary>
public sealed class ShellService : IShellService
{
    internal const string SearchBaseUri = "https://www.bing.com/search?q=";
    private const int MaxSearchTermLength = 200;

    private readonly ILogger<ShellService> _logger;

    public ShellService(ILogger<ShellService>? logger = null)
    {
        _logger = logger ?? NullLogger<ShellService>.Instance;
    }

    public OperationResult OpenFolder(string path)
    {
        if (!IsLocalAbsolutePath(path) || !Directory.Exists(path))
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_PathNotFound"));
        return StartExplorer(Path.GetFullPath(path));
    }

    public OperationResult RevealInExplorer(string filePath)
    {
        if (!IsLocalAbsolutePath(filePath)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        var full = Path.GetFullPath(filePath);
        if (File.Exists(full) || Directory.Exists(full)) return StartExplorer("/select,", full);

        var parent = Path.GetDirectoryName(full);
        return parent is not null && Directory.Exists(parent)
            ? StartExplorer(parent)
            : OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_PathNotFound"));
    }

    public OperationResult ShowFileProperties(string filePath)
    {
        if (!IsLocalAbsolutePath(filePath)) return OperationResult.Fail(OperationErrorKind.InvalidInput);
        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full) && !Directory.Exists(full))
            return OperationResult.Fail(OperationErrorKind.NotFound, TextRef.Of("Sys_PathNotFound"));
        try
        {
            return Shell32.SHObjectProperties(0, Shell32.SHOP_FILEPATH, full, null)
                ? OperationResult.Ok()
                : OperationResult.Fail(OperationErrorKind.Failed);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return OperationResult.FromException(ex);
        }
    }

    public OperationResult OpenUri(Uri uri)
    {
        if (!IsAllowedWebUri(uri))
            return OperationResult.Fail(OperationErrorKind.Blocked, TextRef.Of("Sys_UriNotAllowed"));
        try
        {
            using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Ouverture du lien impossible");
            return OperationResult.FromException(ex);
        }
    }

    public OperationResult SearchOnline(string term)
    {
        var uri = BuildSearchUri(term);
        return uri is null ? OperationResult.Fail(OperationErrorKind.InvalidInput) : OpenUri(uri);
    }

    internal static bool IsAllowedWebUri(Uri? uri)
        => uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    internal static Uri? BuildSearchUri(string? term)
    {
        var cleaned = new string((term ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        if (cleaned.Length > MaxSearchTermLength) cleaned = cleaned[..MaxSearchTermLength];
        return new Uri(SearchBaseUri + Uri.EscapeDataString(cleaned));
    }

    /// <summary>Chemin local absolu (lecteur + racine), sans chemin UNC ni guillemet.</summary>
    internal static bool IsLocalAbsolutePath(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.Length >= 3
           && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/')
           && !path.Contains('"')
           && !path.Any(char.IsControl);

    private OperationResult StartExplorer(params string[] arguments)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var explorer = Path.Combine(windows, "explorer.exe");
        if (!File.Exists(explorer)) return OperationResult.Fail(OperationErrorKind.NotSupported);
        try
        {
            var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = Process.Start(info);
            return OperationResult.Ok();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Ouverture de l'Explorateur impossible");
            return OperationResult.FromException(ex);
        }
    }
}
