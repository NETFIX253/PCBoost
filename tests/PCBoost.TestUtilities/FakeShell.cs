using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Models.Gaming;

namespace PCBoost.TestUtilities.Composition;

public sealed class FakeForegroundWindowProvider : IForegroundWindowProvider
{
    public int? ForegroundProcessId { get; set; }
    public bool Fullscreen { get; set; }
    public int? GetForegroundProcessId() => ForegroundProcessId;
    public bool IsForegroundFullscreen() => Fullscreen;
}

public sealed class FakeCommandRunner : ICommandRunner
{
    public List<CommandSpec> Commands { get; } = [];
    public Task<OperationResult<CommandResult>> RunAsync(CommandSpec spec, CancellationToken cancellationToken = default)
    {
        Commands.Add(spec);
        return Task.FromResult(OperationResult<CommandResult>.Ok(new CommandResult(0, string.Empty, string.Empty, false)));
    }
}

public sealed class FakeShellService : IShellService
{
    public List<string> Calls { get; } = [];
    public OperationResult OpenFolder(string path) { Calls.Add("folder:" + path); return OperationResult.Ok(); }
    public OperationResult RevealInExplorer(string filePath) { Calls.Add("reveal:" + filePath); return OperationResult.Ok(); }
    public OperationResult ShowFileProperties(string filePath) { Calls.Add("props:" + filePath); return OperationResult.Ok(); }
    public OperationResult OpenUri(Uri uri) { Calls.Add("uri:" + uri); return OperationResult.Ok(); }
    public OperationResult SearchOnline(string term) { Calls.Add("search:" + term); return OperationResult.Ok(); }
}

public sealed class FakeFrameTimeSource : IFrameTimeSource
{
    public FrameCaptureAvailability Availability { get; set; } = FrameCaptureAvailability.RequiresElevation;
    public TextRef? LastError { get; set; }
    public FrameCaptureAvailability GetAvailability() => Availability;
    public Task<IFrameCaptureSession?> StartAsync(int processId, CancellationToken cancellationToken = default)
        => Task.FromResult<IFrameCaptureSession?>(null);
}

public sealed class FakeAutoStartRegistration : IAutoStartRegistration
{
    public bool Enabled { get; set; }
    public bool IsEnabled() => Enabled;
    public OperationResult SetEnabled(bool enabled) { Enabled = enabled; return OperationResult.Ok(); }
}
