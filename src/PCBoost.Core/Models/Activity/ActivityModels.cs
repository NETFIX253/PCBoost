using PCBoost.Core.Common;

namespace PCBoost.Core.Models.Activity;

public enum ActivityKind { Info = 0, Analysis, Optimization, Cleanup, Startup, Rollback, Gaming, Warning, Error }

/// <summary>Entrée du journal des modifications visible par l'utilisateur (§78).</summary>
public sealed record ActivityLogEntry(Guid Id, DateTimeOffset Timestamp, ActivityKind Kind, TextRef Message, string? Detail);
