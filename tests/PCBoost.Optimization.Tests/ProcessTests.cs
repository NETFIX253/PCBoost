using PCBoost.Core.Common;
using PCBoost.Core.Models.Optimization;
using PCBoost.Core.Models.Processes;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;
using PCBoost.Core.Services;
using PCBoost.Optimization.Processes;

namespace PCBoost.Optimization.Tests;

public sealed class ProcessServiceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    public static readonly TheoryData<string> CriticalProcesses =
        ["explorer.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "smss.exe", "dwm.exe"];

    [Fact]
    public void ComputeCpuPercent_UsesDeltaOverElapsedTimesLogicalProcessors()
    {
        // 1 s de temps processeur sur 2 s écoulées avec 4 processeurs logiques = 12,5 %.
        Assert.Equal(12.5, ProcessService.ComputeCpuPercent(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(11), TimeSpan.FromSeconds(2), 4), 3);
        Assert.Equal(0, ProcessService.ComputeCpuPercent(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(1), 4));
        Assert.Equal(100, ProcessService.ComputeCpuPercent(TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(1), 2));
    }

    [Fact]
    public async Task GetProcesses_FirstCall_TakesTwoSamples500MsApart_AndComputesCpuAndDisk()
    {
        // FakeSystemInfoProvider : 8 processeurs logiques.
        _h.Processes.Add(10, "worker.exe", @"C:\Apps\worker.exe", cpu: TimeSpan.FromSeconds(100));
        _h.Processes.Update(10, p => p with { IoReadBytes = 1_000_000, IoWriteBytes = 0 });
        _h.OnProcessSampleDelay = () => _h.Processes.Update(10, p => p with
        {
            TotalProcessorTime = p.TotalProcessorTime + TimeSpan.FromMilliseconds(2000),
            IoReadBytes = p.IoReadBytes + 500_000,
        });

        var processes = await _h.Get<IProcessService>().GetProcessesAsync();

        var worker = processes.Single(p => p.ProcessId == 10);
        // 2000 ms / (500 ms × 8) = 50 %.
        Assert.Equal(50, worker.CpuPercent, 3);
        // 500 000 octets en 0,5 s = 1 000 000 octets/s.
        Assert.Equal(1_000_000, worker.DiskBytesPerSec!.Value, 3);
    }

    [Fact]
    public async Task GetProcesses_SubsequentCall_UsesPreviousSnapshot()
    {
        _h.Processes.Add(10, "worker.exe", cpu: TimeSpan.FromSeconds(100));
        var service = _h.Get<IProcessService>();
        await service.GetProcessesAsync();

        _h.Clock.Advance(TimeSpan.FromSeconds(2));
        _h.Processes.Update(10, p => p with { TotalProcessorTime = p.TotalProcessorTime + TimeSpan.FromSeconds(4) });
        var processes = await service.GetProcessesAsync();

        // 4 s / (2 s × 8) = 25 %.
        Assert.Equal(25, processes.Single().CpuPercent, 3);
    }

    [Theory]
    [MemberData(nameof(CriticalProcesses))]
    public void TerminateAndClose_RefuseCriticalProcesses(string name)
    {
        _h.Processes.Add(700, name, @"C:\Windows\" + name);
        var service = _h.Get<IProcessService>();

        var terminate = service.TerminateProcess(700);
        var close = service.CloseApplication(700);

        Assert.Equal(OperationErrorKind.Blocked, terminate.Error);
        Assert.Equal(OperationErrorKind.Blocked, close.Error);
        Assert.Empty(_h.Processes.TerminatedProcessIds);
        Assert.Empty(_h.Processes.CloseRequestedProcessIds);
    }

    [Fact]
    public void Terminate_RefusesPCBoostItself()
    {
        _h.Processes.Add(_h.Processes.CurrentProcessId, "RenamedBrand.exe", @"C:\Program Files\Brand\RenamedBrand.exe");

        Assert.Equal(OperationErrorKind.Blocked, _h.Get<IProcessService>().TerminateProcess(_h.Processes.CurrentProcessId).Error);
        Assert.Empty(_h.Processes.TerminatedProcessIds);
    }

    [Fact]
    public void Terminate_OrdinaryApplication_IsAllowedAndJournaled()
    {
        _h.Processes.Add(800, "notepad++.exe", @"C:\Program Files\Notepad++\notepad++.exe");

        var result = _h.Get<IProcessService>().TerminateProcess(800);

        Assert.True(result.Success);
        Assert.Contains(800, _h.Processes.TerminatedProcessIds);
        Assert.Contains(_h.Journal.Entries, e => e.Message.Key == "Opt_Journal_ProcessTerminated");
    }

    [Fact]
    public async Task GetProcesses_ReportsProtectionAndTrust()
    {
        _h.Processes.Add(20, "lsass.exe", @"C:\Windows\System32\lsass.exe");
        var signed = _h.AddSignedExe(@"C:\Program Files\App\app.exe", "App Inc.", "My App");
        _h.Processes.Add(21, "app.exe", signed);

        var processes = await _h.Get<IProcessService>().GetProcessesAsync();

        Assert.Equal(ProtectionLevel.Critical, processes.Single(p => p.ProcessId == 20).Protection.Level);
        var app = processes.Single(p => p.ProcessId == 21);
        Assert.Equal(ProtectionLevel.None, app.Protection.Level);
        Assert.Equal(TrustLevel.SignedPublisher, app.Trust.Level);
        Assert.Equal("App Inc.", app.Trust.Publisher);
    }
}

public sealed class SecurityServiceTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void WindowsComponent_RequiresMicrosoftSignatureAndWindowsDirectory()
    {
        var inWindows = _h.AddSignedExe(@"C:\Windows\System32\svchost.exe", "Microsoft Windows");
        var elsewhere = _h.AddSignedExe(@"C:\Users\Test\AppData\Local\Fake\svchost.exe", "Microsoft Windows");
        var security = _h.Get<ISecurityService>();

        Assert.Equal(TrustLevel.WindowsComponent, security.Assess(inWindows).Level);
        Assert.Equal(TrustLevel.SignedPublisher, security.Assess(elsewhere).Level);
    }

    [Fact]
    public void UnsignedFile_IsUnsigned_AndItsDeclaredCompanyIsNotShownAsPublisher()
    {
        const string path = @"C:\Apps\tool.exe";
        _h.FileSystem.AddFile(path, 10);
        _h.Metadata.Files[path] = new FileVersionMetadata("Microsoft Corporation", "Tool", "Tool", "1.0");

        var trust = _h.Get<ISecurityService>().Assess(path);

        Assert.Equal(TrustLevel.Unsigned, trust.Level);
        Assert.Null(trust.Publisher);
        Assert.Equal("Tool", trust.Description);
    }

    [Fact]
    public void Assess_IsCachedByPath()
    {
        var path = _h.AddSignedExe(@"C:\Program Files\App\app.exe", "App Inc.");
        var security = _h.Get<ISecurityService>();
        var first = security.Assess(path);
        _h.Signatures.Signatures[path] = new SignatureInfo(SignatureStatus.Invalid, null);

        Assert.Same(first, security.Assess(path));
        _h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(TrustLevel.InvalidSignature, security.Assess(path).Level);
    }

    [Fact]
    public void MissingOrNullPath_IsUnknown()
    {
        var security = _h.Get<ISecurityService>();
        Assert.Equal(TrustLevel.Unknown, security.Assess(null).Level);
        Assert.Equal(TrustLevel.Unknown, security.Assess(@"C:\nowhere\x.exe").Level);
    }
}

public sealed class BackgroundAppsTests : IDisposable
{
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    private IOptimization Module => _h.Get<IEnumerable<IOptimization>>().Single(o => o.Id == OptimizationIds.BackgroundApps);

    /// <summary>Fait consommer <paramref name="cpuMsPerSample"/> ms de processeur pendant l'échantillonnage (500 ms, 8 processeurs).</summary>
    private void Consume(int pid, int cpuMsPerSample)
    {
        var previous = _h.OnProcessSampleDelay;
        _h.OnProcessSampleDelay = () =>
        {
            previous?.Invoke();
            _h.Processes.Update(pid, p => p with { TotalProcessorTime = p.TotalProcessorTime + TimeSpan.FromMilliseconds(cpuMsPerSample) });
        };
    }

    [Fact]
    public async Task NeverTouchesCriticalWindowsProcesses_EvenWhenTheyConsume()
    {
        var names = new[] { "explorer.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "smss.exe", "dwm.exe" };
        for (var i = 0; i < names.Length; i++)
        {
            _h.Processes.Add(1000 + i, names[i], @"C:\Windows\" + names[i]);
            Consume(1000 + i, 4000);
        }

        var preview = await Module.PreviewAsync(new OptimizationContext(null));
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));
        // Même en forçant une sélection explicite de ces PID, rien n'est modifié.
        var forced = names.Select((_, i) => $"{OptimizationIds.BackgroundApps}:{1000 + i}:{_h.Processes.GetProcess(1000 + i)!.StartTime!.Value.UtcTicks}").ToHashSet();
        var result = await Module.ApplyAsync(new OptimizationContext(null, forced), recorder);

        Assert.False(preview.Applicable);
        Assert.Equal(0, result.ChangesApplied);
        Assert.Empty(_h.History.AllChanges);
        for (var i = 0; i < names.Length; i++)
        {
            Assert.Equal(ProcessPriority.Normal, _h.Processes.GetProcess(1000 + i)!.Priority);
            Assert.False(_h.Processes.GetEfficiencyMode(1000 + i).Value);
        }
        Assert.Empty(_h.Processes.TerminatedProcessIds);
        Assert.Empty(_h.Processes.CloseRequestedProcessIds);
    }

    [Fact]
    public async Task ThrottlesKnownHeavyAndMeasuredBusyWindowlessApps_ButNotExclusionsForegroundOrSecurity()
    {
        _h.Processes.Add(10, "OneDrive.exe", @"C:\Users\Test\AppData\Local\Microsoft\OneDrive\OneDrive.exe");
        _h.Processes.Add(11, "busyhelper.exe", @"C:\Apps\busyhelper.exe");
        Consume(11, 400); // 400 / (500 × 8) = 10 %
        _h.Processes.Add(12, "idlehelper.exe", @"C:\Apps\idlehelper.exe");
        Consume(12, 40); // 1 %
        _h.Processes.Add(13, "discord.exe", @"C:\Apps\discord.exe");
        Consume(13, 800);
        _h.Processes.Add(14, "Teams.exe", @"C:\Apps\Teams.exe", hasWindow: true);
        _h.Foreground.ForegroundProcessId = 14;
        _h.Processes.Add(15, "busywindow.exe", @"C:\Apps\busywindow.exe", hasWindow: true);
        Consume(15, 800);
        _h.Processes.Add(16, "MsMpEng.exe", @"C:\ProgramData\Microsoft\Windows Defender\Platform\MsMpEng.exe", currentUser: false);
        Consume(16, 800);
        _h.Processes.Add(17, "AvastUI.exe", @"C:\Program Files\Avast Software\Avast\AvastUI.exe");
        Consume(17, 800);
        _h.Processes.Add(18, "otheruser.exe", @"C:\Apps\otheruser.exe", currentUser: false);
        Consume(18, 800);

        var preview = await Module.PreviewAsync(new OptimizationContext(null));

        Assert.True(preview.Applicable);
        var targets = preview.Changes.Select(c => c.Target).ToList();
        Assert.Contains(targets, t => t.Contains("OneDrive.exe"));
        Assert.Contains(targets, t => t.Contains("busyhelper.exe"));
        Assert.Equal(2, preview.Changes.Count);
        Assert.All(preview.Changes, c => Assert.True(c.Reversible));
        Assert.Equal(RiskLevel.Medium, preview.Risk);
    }

    [Fact]
    public async Task Apply_UsesEfficiencyMode_RecordsBeforeState_AndRestores()
    {
        _h.Processes.Add(10, "OneDrive.exe", @"C:\Apps\OneDrive.exe");
        var rollback = _h.Get<IRollbackManager>();
        var recorder = await rollback.BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));

        var result = await Module.ApplyAsync(new OptimizationContext(null), recorder);
        await rollback.CompleteSessionAsync(recorder.SessionId);

        Assert.Equal(1, result.ChangesApplied);
        Assert.True(_h.Processes.GetEfficiencyMode(10).Value);
        var change = _h.History.AllChanges.Single();
        Assert.Equal(ChangeKinds.ProcessEfficiency, change.Kind);
        Assert.False(ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(change.BeforeState)!.Enabled);

        await rollback.RestoreSessionAsync(recorder.SessionId);
        Assert.False(_h.Processes.GetEfficiencyMode(10).Value);
    }

    [Fact]
    public async Task Apply_WithoutEfficiencyMode_LowersPriority()
    {
        _h.Processes.IsEfficiencyModeSupported = false;
        _h.Processes.Add(10, "Dropbox.exe", @"C:\Apps\Dropbox.exe");
        var recorder = await _h.Get<IRollbackManager>().BeginSessionAsync(SessionType.Manual, TextRef.Literal("t"));

        await Module.ApplyAsync(new OptimizationContext(null), recorder);

        Assert.Equal(ProcessPriority.BelowNormal, _h.Processes.GetProcess(10)!.Priority);
        Assert.Equal(ChangeKinds.ProcessPriority, _h.History.AllChanges.Single().Kind);
    }

    [Fact]
    public async Task Apply_LimitsToFifteenProcesses()
    {
        for (var i = 0; i < 20; i++)
        {
            _h.Processes.Add(100 + i, $"helper{i}.exe", $@"C:\Apps\helper{i}.exe");
            Consume(100 + i, 400 + i);
        }

        var preview = await Module.PreviewAsync(new OptimizationContext(null));

        Assert.Equal(15, preview.Changes.Count);
    }
}
