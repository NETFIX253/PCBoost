using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Services;
using PCBoost.Core.Settings;

namespace PCBoost.TestUtilities;

public sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();
    public event EventHandler<AppSettings>? SettingsChanged;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, settings);
        return Task.CompletedTask;
    }
}

public sealed class FakeNotificationService : INotificationService
{
    public List<NotificationRequest> Shown { get; } = [];
    public event EventHandler<string>? ActionInvoked;
    public void Show(NotificationRequest request) => Shown.Add(request);
    public void Invoke(string actionId) => ActionInvoked?.Invoke(this, actionId);
}

public sealed class FakeActivityJournal : IActivityJournal
{
    public List<ActivityLogEntry> Entries { get; } = [];
    public event EventHandler<ActivityLogEntry>? EntryAdded;
    public Task LogAsync(ActivityKind kind, TextRef message, string? detail = null, CancellationToken cancellationToken = default)
    {
        var e = new ActivityLogEntry(Guid.NewGuid(), DateTimeOffset.UtcNow, kind, message, detail);
        Entries.Add(e);
        EntryAdded?.Invoke(this, e);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit = 200, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ActivityLogEntry>>(Entries.TakeLast(limit).Reverse().ToList());
}
