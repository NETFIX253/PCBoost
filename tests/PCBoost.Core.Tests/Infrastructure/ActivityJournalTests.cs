using Microsoft.Extensions.Logging.Abstractions;
using PCBoost.Core.Abstractions.Persistence;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Infrastructure.Activity;
using PCBoost.TestUtilities;

namespace PCBoost.Core.Tests.CrossCutting;

public sealed class ActivityJournalTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryActivityLogRepository _repository = new();
    private readonly FakeSettingsService _settings = new();

    private ActivityJournal Create(IActivityLogRepository? repository = null)
        => new(repository ?? _repository, _clock, _settings, NullLogger<ActivityJournal>.Instance);

    [Fact]
    public async Task Log_persists_the_entry_with_the_clock_time_and_raises_the_event()
    {
        var journal = Create();
        ActivityLogEntry? raised = null;
        journal.EntryAdded += (_, e) => raised = e;

        await journal.LogAsync(ActivityKind.Cleanup, TextRef.Of("Opt_Cleanup_Done", 3), "détail");

        var entry = Assert.Single(_repository.Entries);
        Assert.Equal(_clock.UtcNow, entry.Timestamp);
        Assert.Equal(ActivityKind.Cleanup, entry.Kind);
        Assert.Equal("Opt_Cleanup_Done", entry.Message.Key);
        Assert.Equal("détail", entry.Detail);
        Assert.Same(entry, raised);
        Assert.Same(entry, Assert.Single(await journal.GetRecentAsync()));
    }

    [Fact]
    public async Task Entries_older_than_the_retention_are_purged()
    {
        var settings = _settings.Current.Clone();
        settings.HistoryRetentionDays = 30;
        await _settings.SaveAsync(settings);
        await _repository.AddAsync(new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow.AddDays(-45), ActivityKind.Info, TextRef.Of("Old"), null));
        await _repository.AddAsync(new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow.AddDays(-10), ActivityKind.Info, TextRef.Of("Recent"), null));
        var journal = Create();

        await journal.LogAsync(ActivityKind.Info, TextRef.Of("New"));

        Assert.Equal(new[] { "New", "Recent" }, _repository.Entries.OrderByDescending(e => e.Timestamp).Select(e => e.Message.Key));
    }

    [Fact]
    public async Task Purge_runs_at_most_once_a_day()
    {
        var journal = Create();
        await journal.LogAsync(ActivityKind.Info, TextRef.Of("First"));
        await _repository.AddAsync(new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow.AddDays(-400), ActivityKind.Info, TextRef.Of("Old"), null));

        await journal.LogAsync(ActivityKind.Info, TextRef.Of("Second"));
        Assert.Contains(_repository.Entries, e => e.Message.Key == "Old");

        _clock.Advance(TimeSpan.FromHours(25));
        await journal.LogAsync(ActivityKind.Info, TextRef.Of("Third"));
        Assert.DoesNotContain(_repository.Entries, e => e.Message.Key == "Old");
    }

    [Fact]
    public async Task Explicit_purge_uses_a_bounded_retention()
    {
        var settings = _settings.Current.Clone();
        settings.HistoryRetentionDays = 1;
        await _settings.SaveAsync(settings);
        await _repository.AddAsync(new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow.AddDays(-3), ActivityKind.Info, TextRef.Of("ThreeDays"), null));
        await _repository.AddAsync(new ActivityLogEntry(Guid.NewGuid(), _clock.UtcNow.AddDays(-8), ActivityKind.Info, TextRef.Of("EightDays"), null));

        var removed = await Create().PurgeExpiredAsync();

        Assert.Equal(1, removed);
        Assert.Equal("ThreeDays", Assert.Single(_repository.Entries).Message.Key);
    }

    [Fact]
    public async Task Storage_failure_does_not_interrupt_the_caller()
    {
        var journal = Create(new FailingRepository());
        var raised = 0;
        journal.EntryAdded += (_, _) => raised++;
        journal.EntryAdded += (_, _) => throw new InvalidOperationException("abonné défaillant");

        await journal.LogAsync(ActivityKind.Error, TextRef.Of("X"));

        Assert.Equal(1, raised);
        Assert.Empty(await journal.GetRecentAsync());
    }

    private sealed class FailingRepository : IActivityLogRepository
    {
        public Task AddAsync(ActivityLogEntry entry, CancellationToken cancellationToken = default) => throw new IOException("base verrouillée");
        public Task<IReadOnlyList<ActivityLogEntry>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) => throw new IOException("base verrouillée");
        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => throw new IOException("base verrouillée");
    }
}
