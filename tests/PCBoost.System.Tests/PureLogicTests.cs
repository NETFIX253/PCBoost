using PCBoost.Core.Common;
using PCBoost.Core.Models.Health;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Platform;
using PCBoost.Platform.Gaming;
using PCBoost.Platform.Interop;

namespace PCBoost.System.Tests;

/// <summary>Logique pure isolée des appels Win32 (exécutée sur toutes les plateformes).</summary>
public sealed class PureLogicTests
{
    // ---- Températures -----------------------------------------------------------------------------------------

    [Theory]
    [InlineData(273.15, 0)]
    [InlineData(323.15, 50)]
    [InlineData(300, 26.85)]
    public void Kelvin_IsConvertedToCelsius(double kelvin, double celsius)
        => Assert.Equal(celsius, TemperatureMath.KelvinToCelsius(kelvin), 3);

    [Fact]
    public void TenthsOfKelvin_AreConvertedToCelsius()
        => Assert.Equal(45.0, TemperatureMath.TenthsKelvinToCelsius(3181.5), 3);

    [Theory]
    [InlineData(0, false)]
    [InlineData(-10, false)]
    [InlineData(0.1, true)]
    [InlineData(95, true)]
    [InlineData(120, true)]
    [InlineData(120.1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void Temperature_PlausibilityBounds(double celsius, bool plausible)
        => Assert.Equal(plausible, TemperatureMath.IsPlausible(celsius));

    [Fact]
    public void MaxPlausible_IgnoresOutOfRangeReadings()
    {
        Assert.Equal(61.5, TemperatureMath.MaxPlausible([-273.15, 45, 61.54, 400]));
        Assert.Null(TemperatureMath.MaxPlausible([-273.15, 0, 150]));
        Assert.Null(TemperatureMath.MaxPlausible([]));
    }

    // ---- Métriques -------------------------------------------------------------------------------------------

    [Fact]
    public void CpuPercent_FromSystemTimeDeltas()
    {
        // Noyau (inactivité incluse) + utilisateur = 1000 ; inactivité = 250 → 75 %.
        Assert.Equal(75, MetricMath.CpuPercent(0, 0, 0, 250, 600, 400), 3);
        Assert.Equal(0, MetricMath.CpuPercent(100, 100, 100, 100, 100, 100));
        Assert.Equal(0, MetricMath.CpuPercent(500, 0, 0, 100, 1000, 0)); // Compteurs incohérents.
    }

    [Theory]
    [InlineData(100.0, 0.0)]
    [InlineData(37.5, 62.5)]
    [InlineData(-5.0, 100.0)]
    [InlineData(250.0, 0.0)]
    public void DiskActive_IsInverseOfIdleAndClamped(double idle, double active)
        => Assert.Equal(active, MetricMath.DiskActivePercent(idle));

    [Fact]
    public void DiskActive_NullWhenUnmeasured()
        => Assert.Null(MetricMath.DiskActivePercent(null));

    [Fact]
    public void GpuSum_IsClampedTo100()
    {
        Assert.Equal(100, MetricMath.SumClamped([60, 70]));
        Assert.Equal(12.5, MetricMath.SumClamped([10, 2.5]));
        Assert.Null(MetricMath.SumClamped(null));
        Assert.Equal(3000, MetricMath.Sum([1000, 2000]));
    }

    // ---- Matériel --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0x10DEu, GpuVendor.Nvidia)]
    [InlineData(0x1002u, GpuVendor.Amd)]
    [InlineData(0x8086u, GpuVendor.Intel)]
    [InlineData(0x1414u, GpuVendor.Microsoft)]
    [InlineData(0x5143u, GpuVendor.Qualcomm)]
    [InlineData(0x1234u, GpuVendor.Other)]
    [InlineData(0u, GpuVendor.Unknown)]
    public void GpuVendor_FromPciVendorId(uint vendorId, GpuVendor expected)
        => Assert.Equal(expected, HardwareClassification.VendorFromPciId(vendorId));

    [Fact]
    public void GpuIntegration_Heuristic()
    {
        Assert.True(HardwareClassification.IsLikelyIntegrated(GpuVendor.Intel, 128 * ByteSize.MiB));
        Assert.True(HardwareClassification.IsLikelyIntegrated(GpuVendor.Qualcomm, null));
        Assert.False(HardwareClassification.IsLikelyIntegrated(GpuVendor.Intel, 8 * ByteSize.GiB)); // Intel Arc
        Assert.True(HardwareClassification.IsLikelyIntegrated(GpuVendor.Amd, 256 * ByteSize.MiB));
        Assert.False(HardwareClassification.IsLikelyIntegrated(GpuVendor.Amd, 512 * ByteSize.MiB));
        Assert.False(HardwareClassification.IsLikelyIntegrated(GpuVendor.Nvidia, 4 * ByteSize.GiB));
        Assert.False(HardwareClassification.IsLikelyIntegrated(GpuVendor.Nvidia, null));
    }

    [Theory]
    [InlineData("Windows 10 Pro", 22631, "Windows 11 Pro")]
    [InlineData("Windows 10 Home", 19045, "Windows 10 Home")]
    [InlineData("Windows 11 Pro", 26100, "Windows 11 Pro")]
    [InlineData(null, 19045, "Windows")]
    public void ProductName_IsCorrectedForWindows11(string? productName, int build, string expected)
        => Assert.Equal(expected, HardwareClassification.CorrectProductName(productName, build));

    [Theory]
    [InlineData(3, StorageMediaType.Hdd)]
    [InlineData(4, StorageMediaType.Ssd)]
    [InlineData(5, StorageMediaType.Scm)]
    [InlineData(0, StorageMediaType.Unknown)]
    [InlineData(null, StorageMediaType.Unknown)]
    public void MediaType_FromMsftPhysicalDisk(int? value, StorageMediaType expected)
        => Assert.Equal(expected, HardwareClassification.MediaTypeFromMsft(value));

    [Theory]
    [InlineData(17, StorageBusType.Nvme)]
    [InlineData(11, StorageBusType.Sata)]
    [InlineData(7, StorageBusType.Usb)]
    [InlineData(10, StorageBusType.Sas)]
    [InlineData(8, StorageBusType.Raid)]
    [InlineData(15, StorageBusType.Virtual)]
    [InlineData(3, StorageBusType.Other)]
    [InlineData(null, StorageBusType.Unknown)]
    public void BusType_FromMsftPhysicalDisk(int? value, StorageBusType expected)
        => Assert.Equal(expected, HardwareClassification.BusTypeFromMsft(value));

    [Fact]
    public void PciIds_AreParsedFromPnpDeviceId()
    {
        Assert.Equal((0x10DEu, 0x1C82u), HardwareClassification.ParsePciIds(@"PCI\VEN_10DE&DEV_1C82&SUBSYS_11BF1043&REV_A1\4&1A2B"));
        Assert.Null(HardwareClassification.ParsePciIds(@"ROOT\BasicDisplay\0000"));
        Assert.Null(HardwareClassification.ParsePciIds(null));
    }

    [Fact]
    public void InstalledPrograms_AreFilteredAndDeduplicated()
    {
        var entries = new[]
        {
            new UninstallEntry("7-Zip 23.01 (x64)", null, null, null),
            new UninstallEntry("7-zip 23.01 (x64)", null, null, null),          // Doublon (vue 32/64 bits).
            new UninstallEntry("Microsoft Visual C++ Runtime", 1, null, null),   // Composant système.
            new UninstallEntry("Security Update for Office", null, "Office16", null),
            new UninstallEntry("Hotfix KB123", null, null, "Hotfix"),
            new UninstallEntry("Update for Foo", null, null, "Update"),
            new UninstallEntry("   ", null, null, null),
            new UninstallEntry(null, null, null, null),
            new UninstallEntry("VLC media player", 0, null, null),
        };

        Assert.Equal(2, InstalledProgramFilter.CountDistinct(entries));
    }

    // ---- Processus -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(ProcessPriority.Idle)]
    [InlineData(ProcessPriority.BelowNormal)]
    [InlineData(ProcessPriority.Normal)]
    [InlineData(ProcessPriority.AboveNormal)]
    [InlineData(ProcessPriority.High)]
    [InlineData(ProcessPriority.RealTime)]
    public void PriorityClass_RoundTrips(ProcessPriority priority)
        => Assert.Equal(priority, PriorityMapping.FromPriorityClass(PriorityMapping.ToPriorityClass(priority)!.Value));

    [Fact]
    public void PriorityClass_KnownWin32Values()
    {
        Assert.Equal(0x40u, PriorityMapping.ToPriorityClass(ProcessPriority.Idle));
        Assert.Equal(0x20u, PriorityMapping.ToPriorityClass(ProcessPriority.Normal));
        Assert.Equal(0x100u, PriorityMapping.ToPriorityClass(ProcessPriority.RealTime));
        Assert.Null(PriorityMapping.ToPriorityClass(ProcessPriority.Unknown));
        Assert.Equal(ProcessPriority.Unknown, PriorityMapping.FromPriorityClass(0x12345));
    }

    [Theory]
    [InlineData(4, ProcessPriority.Idle)]
    [InlineData(8, ProcessPriority.Normal)]
    [InlineData(13, ProcessPriority.High)]
    [InlineData(24, ProcessPriority.RealTime)]
    [InlineData(7, ProcessPriority.Unknown)]
    public void BasePriority_MapsToClass(int basePriority, ProcessPriority expected)
        => Assert.Equal(expected, PriorityMapping.FromBasePriority(basePriority));

    [Fact]
    public void StartTimes_MatchWithinOneSecond()
    {
        var start = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.Zero);
        Assert.True(ProcessProvider.StartTimesMatch(start, start.AddMilliseconds(999)));
        Assert.False(ProcessProvider.StartTimesMatch(start, start.AddSeconds(2)));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    public void ProcessControl_PseudoProcessesAreProtected(int pid, bool forbidden)
        => Assert.Equal(forbidden, ProcessControl.IsForbiddenTarget(pid));

    [Fact]
    public void ProcessControl_CurrentProcessIsProtected()
        => Assert.True(ProcessControl.IsForbiddenTarget(Environment.ProcessId));

    [Fact]
    public void Win32Errors_AreClassified()
    {
        Assert.Equal(OperationErrorKind.AccessDenied, Win32Errors.Classify(5));
        Assert.Equal(OperationErrorKind.InUse, Win32Errors.Classify(32));
        Assert.Equal(OperationErrorKind.ElevationCancelled, Win32Errors.Classify(1223));
        Assert.Equal(OperationErrorKind.NotFound, Win32Errors.Classify(2));
        Assert.Equal(5, Win32Errors.FromHResult(unchecked((int)0x80070005)));
    }

    // ---- Capture d'images ------------------------------------------------------------------------------------

    [Fact]
    public void FrameRecords_RoundTripAndKeepPartialRecordPending()
    {
        var bytes = FrameRecordCodec.Encode([(42, 16.6), (7, 20.0), (42, 33.3), (0, 1000.0)]);
        Assert.Equal(4 * FrameRecordCodec.RecordSize, bytes.Length);

        var timestamps = new List<double>();
        var partial = bytes.AsSpan(0, bytes.Length - 5);
        var consumed = FrameRecordCodec.Decode(partial, 42, timestamps);

        Assert.Equal(3 * FrameRecordCodec.RecordSize, consumed);
        Assert.Equal([16.6, 33.3], timestamps);
    }

    [Fact]
    public void FrameRecords_InvalidTimestampsAreIgnored()
    {
        var bytes = FrameRecordCodec.Encode([(42, double.NaN), (42, -1), (42, 5)]);
        var timestamps = new List<double>();
        FrameRecordCodec.Decode(bytes, 42, timestamps);
        Assert.Equal([5.0], timestamps);
    }

    [Fact]
    public void PresentEvents_OnlyPresentStartIdsAreCounted()
    {
        Assert.True(EtwPresentEvents.IsPresentStart(EtwPresentEvents.DxgiProvider, 42));
        Assert.True(EtwPresentEvents.IsPresentStart(EtwPresentEvents.DxgiProvider, 55));
        Assert.True(EtwPresentEvents.IsPresentStart(EtwPresentEvents.D3D9Provider, 1));
        Assert.False(EtwPresentEvents.IsPresentStart(EtwPresentEvents.DxgiProvider, 43)); // Present_Stop
        Assert.False(EtwPresentEvents.IsPresentStart(EtwPresentEvents.D3D9Provider, 42));
        Assert.False(EtwPresentEvents.IsPresentStart(Guid.NewGuid(), 42));
    }

    [Fact]
    public void PresentFilter_DoesNotDoubleCountTwoProviders()
    {
        var filter = new PresentEventFilter();
        Assert.True(filter.Accept(EtwPresentEvents.D3D9Provider, 0));
        Assert.False(filter.Accept(EtwPresentEvents.DxgiProvider, 1));   // Même image vue par DXGI : ignorée.
        Assert.True(filter.Accept(EtwPresentEvents.D3D9Provider, 16));
        Assert.False(filter.Accept(EtwPresentEvents.DxgiProvider, 17));
        Assert.True(filter.Accept(EtwPresentEvents.DxgiProvider, 2100)); // Premier fournisseur silencieux > 2 s : relais.
        Assert.False(filter.Accept(EtwPresentEvents.D3D9Provider, 2110));
    }

    // ---- Lancement automatique, Explorateur, commandes -----------------------------------------------------

    [Fact]
    public void AutoStart_CommandIsQuotedWithBackgroundSwitch()
    {
        var exe = @"C:\Program Files\PCBoost\PCBoost.exe";
        var command = AutoStartRegistration.BuildCommand(exe);

        Assert.Equal("\"C:\\Program Files\\PCBoost\\PCBoost.exe\" --background", command);
        Assert.True(AutoStartRegistration.PointsTo(command, exe));
        Assert.True(AutoStartRegistration.PointsTo(@"c:\program files\pcboost\pcboost.exe --background", exe));
        Assert.False(AutoStartRegistration.PointsTo("\"C:\\Old\\PCBoost.exe\" --background", exe));
        Assert.False(AutoStartRegistration.PointsTo("\"C:\\Program Files\\PCBoost\\PCBoost.exe", exe));
    }

    [Fact]
    public void AutoStart_StartupApprovedDisabledState()
    {
        Assert.True(AutoStartRegistration.IsDisabledByUser([0x03, 0, 0, 0]));
        Assert.True(AutoStartRegistration.IsDisabledByUser([0x01]));
        Assert.False(AutoStartRegistration.IsDisabledByUser([0x02, 0, 0, 0]));
        Assert.False(AutoStartRegistration.IsDisabledByUser([0x06]));
        Assert.False(AutoStartRegistration.IsDisabledByUser(null));
        Assert.False(AutoStartRegistration.IsDisabledByUser(AutoStartRegistration.EnabledApprovalState()));
    }

    [Fact]
    public void Shell_OnlyWebUrisAreAllowed()
    {
        Assert.True(ShellService.IsAllowedWebUri(new Uri("https://example.com/")));
        Assert.True(ShellService.IsAllowedWebUri(new Uri("http://example.com/")));
        Assert.False(ShellService.IsAllowedWebUri(new Uri("file:///C:/Windows/System32/cmd.exe")));
        Assert.False(ShellService.IsAllowedWebUri(new Uri("ms-settings:display")));
        Assert.False(ShellService.IsAllowedWebUri(new Uri("/relative", UriKind.Relative)));
        Assert.False(ShellService.IsAllowedWebUri(null));
    }

    [Fact]
    public void Shell_SearchUriIsEscaped()
    {
        var uri = ShellService.BuildSearchUri("svchost.exe & calc");

        Assert.NotNull(uri);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("www.bing.com", uri.Host);
        Assert.Equal("https://www.bing.com/search?q=svchost.exe%20%26%20calc", uri.AbsoluteUri);
        Assert.Null(ShellService.BuildSearchUri("  \r\n "));
        Assert.Equal(ShellService.SearchBaseUri.Length + 200, ShellService.BuildSearchUri(new string('a', 500))!.AbsoluteUri.Length);
    }

    [Theory]
    [InlineData(@"C:\Users\Test\file.txt", true)]
    [InlineData(@"\\server\share\file.txt", false)]
    [InlineData(@"file.txt", false)]
    [InlineData("C:\\a\"b", false)]
    public void Shell_LocalAbsolutePathCheck(string path, bool expected)
        => Assert.Equal(expected, ShellService.IsLocalAbsolutePath(path));

    [Theory]
    [InlineData(@"C:\Windows\System32\powercfg.exe", true)]
    [InlineData(@"c:\windows\system32\POWERCFG.EXE", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"C:\Windows\SysWOW64\powercfg.exe", false)]
    [InlineData(@"C:\Windows\System32\drivers\powercfg.exe", false)]
    [InlineData(@"C:\Windows\System32\..\Temp\powercfg.exe", false)]
    [InlineData(@"C:\Users\Test\Downloads\powercfg.exe", false)]
    [InlineData(@"powercfg.exe", false)]
    [InlineData(@"\\server\c$\Windows\System32\powercfg.exe", false)]
    [InlineData("C:\\Windows\\System32\\powercfg.exe\" /x", false)]
    public void CommandRunner_OnlyWhitelistedSystem32Executables(string path, bool expected)
        => Assert.Equal(expected, CommandRunner.IsAllowedExecutable(path, @"C:\Windows"));

    [Fact]
    public void CommandRunner_ArgumentsAreValidated()
    {
        Assert.True(CommandRunner.AreArgumentsValid(["/setactive", "381b4222-f694-41f0-9685-ff5bb260df2e"]));
        Assert.False(CommandRunner.AreArgumentsValid(["/list\r\nshutdown"]));
        Assert.False(CommandRunner.AreArgumentsValid(Enumerable.Repeat("x", 33).ToList()));
        Assert.False(CommandRunner.AreArgumentsValid(null));
    }

    [Fact]
    public void Fullscreen_RequiresWindowCoveringMonitor()
    {
        var monitor = new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        Assert.True(ForegroundWindowProvider.Covers(new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1080 }, monitor));
        Assert.True(ForegroundWindowProvider.Covers(new User32.RECT { Left = -8, Top = -8, Right = 1928, Bottom = 1088 }, monitor));
        Assert.False(ForegroundWindowProvider.Covers(new User32.RECT { Left = 0, Top = 0, Right = 1920, Bottom = 1040 }, monitor));
    }

    [Theory]
    [InlineData(@"\Vendor\Task", true)]
    [InlineData(@"Vendor\Task", false)]
    [InlineData(@"\Vendor\..\Task", false)]
    [InlineData("\\Vendor\\Ta\tsk", false)]
    [InlineData("", false)]
    public void ScheduledTask_PathValidation(string path, bool expected)
        => Assert.Equal(expected, ScheduledTaskProvider.IsValidTaskPath(path));

    [Theory]
    [InlineData("Microsoft Corporation", true)]
    [InlineData("$(@%SystemRoot%\\system32\\wininet.dll,-16000)", true)]
    [InlineData("Vendor Inc.", false)]
    [InlineData(null, false)]
    public void ScheduledTask_MicrosoftAuthor(string? author, bool expected)
        => Assert.Equal(expected, ScheduledTaskProvider.IsMicrosoftAuthor(author));
}

public sealed class HealthParserTests
{
    private const string Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private static string Event(int id, string time, params (string Name, string Value)[] data)
        => $"<Event xmlns='{Ns}'><System><EventID>{id}</EventID><TimeCreated SystemTime='{time}'/></System><EventData>"
           + string.Concat(data.Select(d => $"<Data Name='{d.Name}'>{d.Value}</Data>")) + "</EventData></Event>";

    [Fact]
    public void Boot_record_reads_windows_durations()
    {
        var record = Platform.Health.HealthEventParsers.BootRecord(Event(100, "2026-09-27T07:30:00.000Z",
            ("BootTime", "42000"), ("MainPathBootTime", "30000"), ("BootPostBootTime", "12000"), ("BootNumStartupApps", "14")));
        Assert.NotNull(record);
        Assert.Equal(TimeSpan.FromSeconds(42), record!.BootTime);
        Assert.Equal(TimeSpan.FromSeconds(30), record.MainPathBootTime);
        Assert.Equal(14, record.StartupAppCount);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 7, 30, 0, TimeSpan.Zero), record.Timestamp);
    }

    [Theory]
    [InlineData(100, "0")]
    [InlineData(100, "99999999999")]
    [InlineData(200, "42000")]
    public void Invalid_boot_records_are_ignored(int id, string bootTime)
        => Assert.Null(Platform.Health.HealthEventParsers.BootRecord(Event(id, "2026-09-27T07:30:00Z", ("BootTime", bootTime))));

    [Fact]
    public void Malformed_xml_is_ignored()
    {
        Assert.Null(Platform.Health.HealthEventParsers.BootRecord("<Event><oops"));
        Assert.Null(Platform.Health.HealthEventParsers.Degradation("not xml"));
    }

    [Theory]
    [InlineData(101, BootDegradationKind.Application)]
    [InlineData(102, BootDegradationKind.Driver)]
    [InlineData(103, BootDegradationKind.Service)]
    [InlineData(106, BootDegradationKind.Other)]
    public void Degradation_reads_kind_name_and_delay(int id, BootDegradationKind kind)
    {
        var d = Platform.Health.HealthEventParsers.Degradation(Event(id, "2026-09-27T07:30:00Z",
            ("Name", "OneDrive.exe"), ("FriendlyName", "Microsoft OneDrive"), ("TotalTime", "9000"), ("DegradationTime", "4000")));
        Assert.NotNull(d);
        Assert.Equal(kind, d!.Kind);
        Assert.Equal("Microsoft OneDrive", d.Name);
        Assert.Equal("OneDrive.exe", d.FileName);
        Assert.Equal(TimeSpan.FromSeconds(4), d.DegradationTime);
    }

    [Fact]
    public void Degradation_without_delay_or_name_is_ignored()
    {
        Assert.Null(Platform.Health.HealthEventParsers.Degradation(Event(101, "2026-09-27T07:30:00Z", ("Name", "a.exe"), ("DegradationTime", "0"))));
        Assert.Null(Platform.Health.HealthEventParsers.Degradation(Event(101, "2026-09-27T07:30:00Z", ("DegradationTime", "500"))));
    }

    [Fact]
    public void Clean_removes_control_characters_and_bounds_length()
    {
        Assert.Equal("ab", Platform.Health.HealthEventParsers.Clean("a\u0000b\n"));
        Assert.Null(Platform.Health.HealthEventParsers.Clean(" \t "));
        Assert.Equal(Platform.Health.HealthEventParsers.MaxNameLength, Platform.Health.HealthEventParsers.Clean(new string('x', 500))!.Length);
    }

    [Fact]
    public void Boot_sessions_pair_each_boot_with_the_next_logon()
    {
        var boots = new[]
        {
            Event(27, "2026-09-27T07:00:00Z", ("BootType", "1")),
            Event(27, "2026-09-26T07:00:00Z", ("BootType", "0")),
            Event(27, "2026-09-25T07:00:00Z", ("BootType", "2")),
        };
        var logons = new[] { Event(7001, "2026-09-27T07:01:10Z"), Event(7001, "2026-09-26T07:02:00Z"), Event(7001, "2026-09-26T12:00:00Z") };

        var sessions = Platform.Health.HealthEventParsers.BootSessions(boots, logons, 10);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(BootKind.FastStartup, sessions[0].Kind);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 7, 1, 10, TimeSpan.Zero), sessions[0].UserLogonAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 7, 2, 0, TimeSpan.Zero), sessions[1].UserLogonAt);
        Assert.Equal(BootKind.Resume, sessions[2].Kind);
        Assert.Null(sessions[2].UserLogonAt);
        Assert.Equal(2, Platform.Health.HealthEventParsers.BootSessions(boots, logons, 2).Count);
    }

    [Theory]
    [InlineData("VLC.EXE-2B3C4D5E.pf", "vlc.exe")]
    [InlineData("MY-APP.EXE-00112233.pf", "my-app.exe")]
    [InlineData("NTOSBOOT-B00DFAAD.pf", null)]
    [InlineData("Layout.ini", null)]
    [InlineData("-1234.pf", null)]
    public void Prefetch_file_name_gives_executable(string fileName, string? expected)
        => Assert.Equal(expected, Platform.Health.ElevatedHealthReaders.PrefetchExecutable(fileName));

    [Theory]
    [InlineData(0, DiskHealthStatus.Healthy)]
    [InlineData(1, DiskHealthStatus.Warning)]
    [InlineData(2, DiskHealthStatus.Unhealthy)]
    [InlineData(5, DiskHealthStatus.Unknown)]
    [InlineData(null, DiskHealthStatus.Unknown)]
    public void Disk_health_status_mapping(int? value, DiskHealthStatus expected)
        => Assert.Equal(expected, Platform.Health.HealthClassification.DiskStatus(value));
}
