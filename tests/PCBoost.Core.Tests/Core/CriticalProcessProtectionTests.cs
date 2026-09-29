using PCBoost.Core.Models.Processes;
using PCBoost.Core.Security;

namespace PCBoost.Core.Tests.Domain;

public sealed class CriticalProcessProtectionTests
{
    private static readonly string[] MandatoryNames =
        ["explorer.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "smss.exe", "dwm.exe"];

    public static TheoryData<string> MandatoryCriticalProcesses => new(MandatoryNames);

    [Theory]
    [MemberData(nameof(MandatoryCriticalProcesses))]
    public void Windows_core_processes_are_critical(string name)
    {
        var protection = new CriticalProcessProtection();

        Assert.True(protection.IsCritical(name));
        Assert.Contains(name, protection.CriticalProcessNames);
        var info = protection.GetProtection(name, null);
        Assert.Equal(ProtectionLevel.Critical, info.Level);
        Assert.True(info.IsCritical);
        Assert.Equal("Protection_Critical", info.Reason?.Key);
    }

    [Theory]
    [InlineData("Explorer", "explorer.exe")]
    [InlineData("  LSASS.EXE ", "lsass.exe")]
    [InlineData(@"C:\Windows\explorer.EXE", "explorer.exe")]
    [InlineData("C:/Windows/System32/csrss.exe", "csrss.exe")]
    [InlineData("System", "system")]
    [InlineData("Memory Compression", "memory compression")]
    [InlineData("", "")]
    public void Normalize_lowercases_strips_directory_and_adds_exe(string input, string expected)
        => Assert.Equal(expected, CriticalProcessProtection.Normalize(input));

    [Theory]
    [InlineData("EXPLORER")]
    [InlineData("Explorer.exe")]
    [InlineData(@"C:\Windows\explorer.exe")]
    public void IsCritical_uses_normalized_names(string name)
        => Assert.True(new CriticalProcessProtection().IsCritical(name));

    [Fact]
    public void Unknown_process_is_not_protected()
    {
        var protection = new CriticalProcessProtection();

        Assert.False(protection.IsCritical("notepad.exe"));
        Assert.Equal(ProtectionInfo.None, protection.GetProtection("notepad.exe", @"C:\Program Files\Notepad\notepad.exe"));
    }

    [Fact]
    public void Executables_in_system_directories_are_sensitive()
    {
        var info = new CriticalProcessProtection().GetProtection("someservicehost.exe", @"C:\Windows\System32\someservicehost.exe");

        Assert.Equal(ProtectionLevel.Sensitive, info.Level);
        Assert.Equal("Protection_SystemDirectory", info.Reason?.Key);
    }

    [Fact]
    public void Known_sensitive_process_is_sensitive_not_critical()
    {
        var info = new CriticalProcessProtection().GetProtection("RuntimeBroker.exe", null);

        Assert.Equal(ProtectionLevel.Sensitive, info.Level);
        Assert.Equal("Protection_Sensitive", info.Reason?.Key);
    }

    [Fact]
    public void User_extension_adds_critical_and_sensitive_entries()
    {
        var protection = new CriticalProcessProtection(additionalCritical: ["MyBackupAgent"], additionalSensitive: ["mytool.exe"]);

        Assert.True(protection.IsCritical("mybackupagent.exe"));
        Assert.Equal(ProtectionLevel.Sensitive, protection.GetProtection("MYTOOL.EXE", null).Level);
        Assert.True(protection.IsCritical("explorer.exe"));
    }

    [Fact]
    public void User_extension_cannot_downgrade_a_critical_process()
    {
        var protection = new CriticalProcessProtection(additionalSensitive: ["explorer.exe", "lsass.exe"]);

        Assert.Equal(ProtectionLevel.Critical, protection.GetProtection("explorer.exe", null).Level);
        Assert.Equal(ProtectionLevel.Critical, protection.GetProtection("lsass.exe", null).Level);
    }

    [Fact]
    public void User_file_only_adds_entries_and_empty_lists_remove_nothing()
    {
        using var temp = new TempDirectory();
        var file = temp.File("protected-processes.user.json");
        File.WriteAllText(file, """
            { "critical": [ "corpagent.exe" ], "sensitive": [ "dwm.exe", "explorer.exe" ] }
            """);

        var protection = CriticalProcessProtection.CreateWithUserExtensions(file);

        Assert.True(protection.IsCritical("corpagent.exe"));
        foreach (var name in MandatoryNames)
            Assert.True(protection.IsCritical(name), name);
    }

    [Fact]
    public void Invalid_or_missing_user_file_falls_back_to_the_embedded_list()
    {
        using var temp = new TempDirectory();
        var invalid = temp.File("invalid.json");
        File.WriteAllText(invalid, "{ not json");

        var fromInvalid = CriticalProcessProtection.CreateWithUserExtensions(invalid);
        var fromMissing = CriticalProcessProtection.CreateWithUserExtensions(temp.File("missing.json"));
        var fromNull = CriticalProcessProtection.CreateWithUserExtensions(null);

        foreach (var protection in new[] { fromInvalid, fromMissing, fromNull })
        {
            Assert.True(protection.IsCritical("lsass.exe"));
            Assert.True(protection.IsCritical("explorer.exe"));
        }
    }
}
