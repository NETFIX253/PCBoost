using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Activity;
using PCBoost.Core.Models.Health;
using PCBoost.Optimization.Safety;
using PCBoost.TestUtilities;

namespace PCBoost.Optimization.Tests;

public sealed class RestorePointTests
{
    private static readonly DateTimeOffset Created = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RestorePointStatus.Created, "Opt_RestorePoint_Created", ActivityKind.Optimization, true)]
    [InlineData(RestorePointStatus.RecentExists, "Opt_RestorePoint_Recent", ActivityKind.Optimization, true)]
    [InlineData(RestorePointStatus.Disabled, "Opt_RestorePoint_Disabled", ActivityKind.Warning, false)]
    [InlineData(RestorePointStatus.Failed, "Opt_RestorePoint_Failed", ActivityKind.Warning, false)]
    public async Task Result_is_decoded_and_logged(RestorePointStatus status, string key, ActivityKind kind, bool available)
    {
        var elevation = new FakeElevationService { Handler = _ => new ElevatedResponse(OperationResult.Ok(), HealthElevatedData.EncodeRestorePoint(status, available ? Created : null)) };
        var journal = new FakeActivityJournal();

        var result = await new RestorePointService(elevation, journal).CreateAsync();

        Assert.Equal(status, result.Status);
        Assert.Equal(available, result.IsAvailable);
        Assert.Equal(available ? Created : null, result.PointCreatedAt);
        Assert.Equal(key, result.Message!.Key);
        var request = Assert.Single(elevation.Requests);
        Assert.Equal(ElevatedHealthOperations.RestorePointCreate, request.Operation);
        Assert.Empty(request.Parameters);
        var entry = Assert.Single(journal.Entries);
        Assert.Equal(kind, entry.Kind);
        Assert.Equal(key, entry.Message.Key);
    }

    [Fact]
    public async Task Declined_permission_is_a_failure_without_journal_entry()
    {
        var journal = new FakeActivityJournal();
        var result = await new RestorePointService(new FakeElevationService { UserCancels = true }, journal).CreateAsync();
        Assert.Equal(RestorePointStatus.Failed, result.Status);
        Assert.Equal("Opt_RestorePoint_Cancelled", result.Message!.Key);
        Assert.Empty(journal.Entries);
    }

    [Fact]
    public async Task Helper_failure_is_reported_as_failed()
    {
        var elevation = new FakeElevationService { Handler = _ => new ElevatedResponse(OperationResult.Fail(OperationErrorKind.Failed), new Dictionary<string, string>()) };
        var result = await new RestorePointService(elevation, new FakeActivityJournal()).CreateAsync();
        Assert.Equal(RestorePointStatus.Failed, result.Status);
        Assert.Equal("Opt_RestorePoint_Failed", result.Message!.Key);
    }
}
