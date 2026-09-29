using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Updates;

public enum UpdateCheckStatus { UpToDate = 0, UpdateAvailable, NotConfigured, Failed }

public sealed record UpdateInfo(Version Version, string PackageLocation, string Sha256, string? ReleaseNotes, DateTimeOffset? PublishedAt);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update, TextRef? Message);

public sealed record UpdateDownloadResult(OperationResult Outcome, string? LocalPath);
