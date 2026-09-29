using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Updates;
using PCBoost.Core.Settings;

namespace PCBoost.Core.Services;

public interface ISettingsService
{
    AppSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    event EventHandler<AppSettings>? SettingsChanged;
}

public sealed record NotificationRequest(TextRef Title, TextRef Body, string? ActionId = null, TextRef? ActionLabel = null, string? Tag = null);

/// <summary>Notifications Windows (§28). Respecte le réglage utilisateur.</summary>
public interface INotificationService
{
    void Show(NotificationRequest request);

    event EventHandler<string>? ActionInvoked;
}

public interface IActivityJournal
{
    Task LogAsync(ActivityKind kind, TextRef message, string? detail = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default);

    event EventHandler<ActivityLogEntry>? EntryAdded;
}

public interface IAppInfo
{
    string ProductName { get; }

    Version Version { get; }

    string DotNetVersion { get; }

    string WindowsAppSdkVersion { get; }

    string DataDirectory { get; }

    string LogDirectory { get; }
}

public interface IUpdateProvider
{
    string Name { get; }

    Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>Architecture de mise à jour (§51) : vérification d'intégrité SHA-256 obligatoire avant installation.</summary>
public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadAndVerifyAsync(UpdateInfo update, IProgress<double>? progress = null, CancellationToken cancellationToken = default);

    OperationResult LaunchInstaller(string verifiedPackagePath);
}
