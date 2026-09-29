using PCBoost.Core.Privacy;
using PCBoost.Infrastructure.Logging;

namespace PCBoost.Core.Tests.Infrastructure;

public sealed class DiagnosticLogSourceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pcboost-logs-" + Guid.NewGuid().ToString("N"));

    public DiagnosticLogSourceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private DiagnosticLogSource Source() => new(_dir, new SensitiveDataRedactor("/home/amin", "amin", "BUREAU-AMIN"));

    [Fact]
    public async Task Returns_latest_lines_in_order_masked_and_bounded()
    {
        var older = Path.Combine(_dir, "pcboost-20260928.log");
        var newer = Path.Combine(_dir, "pcboost-20260929.log");
        await File.WriteAllLinesAsync(older, ["ancien 1", "ancien 2"]);
        await File.WriteAllLinesAsync(newer, ["récent sur BUREAU-AMIN", "fichier /home/amin/doc.txt", "utilisateur amin", new string('x', 1000)]);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));

        var lines = await Source().ReadRecentLinesAsync(5);

        Assert.Equal(5, lines.Count);
        Assert.Equal("ancien 2", lines[0]);
        Assert.Equal("récent sur <machine>", lines[1]);
        Assert.Equal("fichier %USERPROFILE%/doc.txt", lines[2]);
        Assert.Equal("utilisateur <user>", lines[3]);
        Assert.Equal(DiagnosticLogSource.MaxLineLength + 1, lines[4].Length);
        Assert.EndsWith("…", lines[4]);
    }

    [Fact]
    public async Task Missing_directory_or_zero_lines_returns_nothing()
    {
        Assert.Empty(await new DiagnosticLogSource(Path.Combine(_dir, "absent"), new SensitiveDataRedactor(null, null, null)).ReadRecentLinesAsync(10));
        Assert.Empty(await Source().ReadRecentLinesAsync(0));
    }

    [Fact]
    public async Task File_being_written_can_still_be_read()
    {
        var path = Path.Combine(_dir, "pcboost-live.log");
        await using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        var bytes = System.Text.Encoding.UTF8.GetBytes("ligne en cours\n");
        await writer.WriteAsync(bytes);
        await writer.FlushAsync();
        Assert.Equal(["ligne en cours"], await Source().ReadRecentLinesAsync(10));
    }
}
