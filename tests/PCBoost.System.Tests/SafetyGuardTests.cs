using Microsoft.Extensions.DependencyInjection;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Common;
using PCBoost.Core.Localization;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform;
using PCBoost.TestUtilities;

namespace PCBoost.System.Tests;

/// <summary>
/// Garde-fous appliqués avant tout appel système : ils sont vérifiés sur toutes les plateformes, sans rien modifier.
/// </summary>
public sealed class SafetyGuardTests
{
    [Theory]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\wuauserv")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update")]
    [InlineData(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\AppHost\SmartScreen")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Lsa")]
    public void Registry_WriteAndDeleteOnForbiddenKeys_AreBlocked(RegistryHiveKind hive, string keyPath)
    {
        var registry = new RegistryProvider();
        var location = new RegistryLocation(hive, keyPath);

        var set = registry.SetValue(location, "x", RegistryValueData.DWord(0));
        var delete = registry.DeleteValue(location, "x");

        Assert.Equal(OperationErrorKind.Blocked, set.Error);
        Assert.Equal(OperationErrorKind.Blocked, delete.Error);
        Assert.Equal("Sys_ProtectedTarget", set.Message?.Key);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll")]
    [InlineData(@"C:\Windows\WinSxS\manifest.xml")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Definitions\mpavbase.vdm")]
    [InlineData(@"C:\System Volume Information\tracking.log")]
    public void FileSystem_DeleteForbiddenPath_IsBlocked(string path)
    {
        var result = new FileSystemProvider().DeleteFile(path);

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Fact]
    public void FileSystem_WriteForbiddenPath_IsBlocked()
        => Assert.Equal(OperationErrorKind.Blocked, new FileSystemProvider().WriteAllText(@"C:\Windows\System32\drivers\etc\hosts", "x").Error);

    [Fact]
    public void FileSystem_DeleteMissingFile_IsNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), "pcboost-missing-" + Guid.NewGuid().ToString("N") + ".tmp");
        Assert.Equal(OperationErrorKind.NotFound, new FileSystemProvider().DeleteFile(path).Error);
    }

    [Fact]
    public void FileSystem_EnumerationOfMissingDirectory_IsEmpty()
    {
        var provider = new FileSystemProvider();
        var missing = Path.Combine(Path.GetTempPath(), "pcboost-missing-" + Guid.NewGuid().ToString("N"));

        Assert.Empty(provider.EnumerateFiles(missing, recursive: true));
        Assert.Equal(new DirectorySizeResult(0, 0, 0), provider.GetDirectorySize(missing));
        Assert.Equal(0, provider.DeleteEmptySubdirectories(missing));
    }

    [Fact]
    public void FileSystem_EmptySubdirectoriesAreRemovedButNeverTheRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcboost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "a", "b"));
        Directory.CreateDirectory(Path.Combine(root, "keep"));
        File.WriteAllText(Path.Combine(root, "keep", "file.txt"), "x");
        try
        {
            var provider = new FileSystemProvider();
            var files = provider.EnumerateFiles(root, recursive: true).ToList();

            Assert.Single(files);
            Assert.Equal(2, provider.DeleteEmptySubdirectories(root));
            Assert.True(Directory.Exists(root));
            Assert.True(Directory.Exists(Path.Combine(root, "keep")));
            Assert.False(Directory.Exists(Path.Combine(root, "a")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FileSystem_EnumerationHonorsCancellation()
    {
        var root = Path.Combine(Path.GetTempPath(), "pcboost-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "f.txt"), "x");
        try
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => new FileSystemProvider().EnumerateFiles(root, false, cts.Token).ToList());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"\microsoft\windows\UpdateOrchestrator\Schedule Scan")]
    public void ScheduledTask_MicrosoftTasks_AreBlocked(string taskPath)
    {
        var result = new ScheduledTaskProvider().SetEnabled(taskPath, false);

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_MicrosoftTaskBlocked", result.Message?.Key);
    }

    [Fact]
    public void ScheduledTask_InvalidPath_IsRejected()
        => Assert.Equal(OperationErrorKind.InvalidInput, new ScheduledTaskProvider().SetEnabled(@"..\Task", true).Error);

    [Fact]
    public void Process_RealTimePriority_IsBlocked()
    {
        var result = new ProcessControl().SetPriority(12345, ProcessPriority.RealTime);

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_RealTimePriorityBlocked", result.Message?.Key);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Process_PseudoProcesses_AreNeverTouched(int pid)
    {
        var control = new ProcessControl();

        Assert.Equal(OperationErrorKind.Blocked, control.Terminate(pid).Error);
        Assert.Equal(OperationErrorKind.Blocked, control.RequestClose(pid).Error);
        Assert.Equal(OperationErrorKind.Blocked, control.SetPriority(pid, ProcessPriority.Idle).Error);
    }

    [Fact]
    public void Process_CurrentProcess_IsNeverTerminated()
        => Assert.Equal(OperationErrorKind.Blocked, new ProcessControl().Terminate(Environment.ProcessId).Error);

    [Fact]
    public void Process_UnknownPriority_IsInvalid()
        => Assert.Equal(OperationErrorKind.InvalidInput, new ProcessControl().SetPriority(12345, ProcessPriority.Unknown).Error);

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Users\Test\Downloads\powercfg.exe")]
    [InlineData("powercfg")]
    public async Task CommandRunner_NonWhitelistedExecutable_IsBlocked(string fileName)
    {
        var result = await new CommandRunner().RunAsync(new CommandSpec(fileName, ["/c", "echo"], TimeSpan.FromSeconds(5)));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_CommandNotAllowed", result.Message?.Key);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("javascript:alert(1)")]
    public void Shell_NonWebUri_IsBlocked(string uri)
    {
        var result = new ShellService().OpenUri(new Uri(uri));

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_UriNotAllowed", result.Message?.Key);
    }

    [Fact]
    public void Shell_UncOrRelativePaths_AreRejected()
    {
        var shell = new ShellService();
        Assert.Equal(OperationErrorKind.InvalidInput, shell.RevealInExplorer(@"\\server\share\file.exe").Error);
        Assert.Equal(OperationErrorKind.InvalidInput, shell.ShowFileProperties("relative.exe").Error);
        Assert.Equal(OperationErrorKind.NotFound, shell.OpenFolder(@"\\server\share").Error);
    }

    [Fact]
    public async Task Elevation_UnknownOperation_IsRefusedBeforeAnyPrompt()
    {
        var response = await new ElevationService().RunAsync(new ElevatedRequest("shell.execute", new Dictionary<string, string> { ["command"] = "cmd.exe" }));

        Assert.False(response.Outcome.Success);
        Assert.Equal(OperationErrorKind.Blocked, response.Outcome.Error);
        Assert.Empty(response.Data);
    }

    [Fact]
    public async Task Elevation_RegistryOutsideAllowList_IsRefusedBeforeAnyPrompt()
    {
        var response = await new ElevationService().RunAsync(new ElevatedRequest(ElevatedOperations.RegistrySetValue, new Dictionary<string, string>
        {
            ["path"] = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
            ["name"] = "x",
            ["valueBase64"] = "AQ==",
        }));

        Assert.Equal(OperationErrorKind.Blocked, response.Outcome.Error);
    }

    [Fact]
    public async Task Elevation_FrameCaptureThroughRunAsync_IsRejected()
    {
        var response = await new ElevationService().RunAsync(new ElevatedRequest(ElevatedOperations.FrameCapture, new Dictionary<string, string>
        {
            ["pid"] = "4242",
            ["pipe"] = "PCBoost.Frames." + Guid.NewGuid().ToString("N"),
            ["parentPid"] = "1000",
        }));

        Assert.Equal(OperationErrorKind.InvalidInput, response.Outcome.Error);
    }

    [Fact]
    public void Registration_RegistersEveryPlatformService()
    {
        var services = new ServiceCollection();
        services.AddPCBoostPlatform();

        Type[] expected =
        [
            typeof(IClock), typeof(IProcessProvider), typeof(IProcessControl), typeof(ISystemInfoProvider), typeof(ISystemMetricsProvider),
            typeof(IHardwareProvider), typeof(IRegistryProvider), typeof(IFileSystemProvider), typeof(IFileMetadataProvider),
            typeof(ISignatureVerifier), typeof(IShortcutResolver), typeof(IRecycleBinProvider), typeof(IPowerProvider),
            typeof(IVisualEffectsProvider), typeof(IForegroundWindowProvider), typeof(IScheduledTaskProvider),
            typeof(IAutoStartRegistration), typeof(IShellService), typeof(ICommandRunner), typeof(IElevationService),
            typeof(IFrameTimeSource), typeof(IStringResourceSource),
        ];
        foreach (var type in expected)
        {
            var descriptor = Assert.Single(services, d => d.ServiceType == type);
            Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        }
    }

    [Fact]
    public void Registration_KeepsAnExistingClock()
    {
        var services = new ServiceCollection();
        var clock = new FakeClock();
        services.AddSingleton<IClock>(clock);
        services.AddPCBoostPlatform();

        using var provider = services.BuildServiceProvider();
        Assert.Same(clock, provider.GetRequiredService<IClock>());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("en")]
    public void Resources_AllKeysAreTranslated(string culture)
    {
        var source = ResourceManagerStringSource.ForAssembly(typeof(ServiceCollectionExtensions).Assembly, "PCBoost.Platform.Resources.Strings");
        var cultureInfo = global::System.Globalization.CultureInfo.GetCultureInfo(culture);
        string[] keys =
        [
            "Sys_ElevationCancelled", "Sys_ElevatorMissing", "Sys_ElevationFailed", "Sys_ElevationNoResult", "Sys_ElevatedOperationRefused",
            "Sys_ElevatedInvalidParameters", "Sys_ElevatedCleanupPartial", "Sys_FrameCaptureUnavailable", "Sys_FrameCaptureCancelled",
            "Sys_FrameCaptureTimeout", "Sys_FrameCaptureStopped", "Sys_FrameCaptureSessionFailed", "Sys_FrameCaptureTargetExited",
            "Sys_ProcessNotFound", "Sys_ProcessAccessDenied", "Sys_ProcessNoWindow", "Sys_RealTimePriorityBlocked", "Sys_ProtectedTarget",
            "Sys_MicrosoftTaskBlocked", "Sys_CommandNotAllowed", "Sys_CommandTimedOut", "Sys_FileInUse", "Sys_FileReadOnly",
            "Sys_UriNotAllowed", "Sys_PathNotFound", "Sys_EfficiencyModeNotSupported", "Sys_PowerSchemeNotFound",
            "Sys_VisualEffectsPartial", "Sys_AutoStartUnavailable",
        ];
        foreach (var key in keys)
            Assert.False(string.IsNullOrWhiteSpace(source.GetString(key, cultureInfo)), $"{key} ({culture})");

        Assert.NotEqual(
            source.GetString("Sys_ElevationCancelled", global::System.Globalization.CultureInfo.GetCultureInfo("fr")),
            source.GetString("Sys_ElevationCancelled", global::System.Globalization.CultureInfo.GetCultureInfo("en")));
    }
}
