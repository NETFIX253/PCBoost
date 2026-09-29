using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Security;

namespace PCBoost.Core.Tests.Domain;

public sealed class ForbiddenTargetPolicyTests
{
    [Theory]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(@"SOFTWARE\Microsoft\Windows Defender\Real-Time Protection")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\StandardProfile")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\WindowsFirewall")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SysMain")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\wuauserv")]
    [InlineData(@"BCD00000000\Objects")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\game.exe")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Lsa")]
    [InlineData(@"software\policies\microsoft\windows defender")]
    [InlineData(@"\SOFTWARE\Policies\Microsoft\Windows Defender\")]
    [InlineData("SOFTWARE/Policies/Microsoft/Windows Defender")]
    public void Security_and_system_registry_paths_are_forbidden(string keyPath)
    {
        Assert.True(ForbiddenTargetPolicy.IsForbiddenRegistryPath(keyPath));
        Assert.True(ForbiddenTargetPolicy.IsForbiddenRegistryLocation(new RegistryLocation(RegistryHiveKind.LocalMachine, keyPath)));
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")]
    [InlineData(@"Control Panel\Desktop")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects")]
    public void Ordinary_user_registry_paths_are_not_forbidden(string keyPath)
        => Assert.False(ForbiddenTargetPolicy.IsForbiddenRegistryPath(keyPath));

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder")]
    [InlineData(@"\software\microsoft\windows\currentversion\explorer\startupapproved\run\")]
    public void StartupApproved_keys_are_allowed_for_elevation(string keyPath)
        => Assert.True(ForbiddenTargetPolicy.IsAllowedElevatedRegistryPath(keyPath));

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\Sub")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\SysMain")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData("")]
    public void Other_keys_are_not_allowed_for_elevation(string keyPath)
        => Assert.False(ForbiddenTargetPolicy.IsAllowedElevatedRegistryPath(keyPath));

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows\SysWOW64\kernel32.dll")]
    [InlineData(@"C:\Windows\WinSxS\manifest")]
    [InlineData(@"C:\Windows\Boot\EFI\bootmgfw.efi")]
    [InlineData(@"C:\ProgramData\Microsoft\Windows Defender\Scans\mpcache.bin")]
    [InlineData(@"C:\Windows\SoftwareDistribution\DataStore\DataStore.edb")]
    [InlineData(@"C:\System Volume Information\tracking.log")]
    [InlineData(@"c:/windows/system32/config/SAM")]
    [InlineData("")]
    [InlineData("   ")]
    public void System_files_are_forbidden(string path)
        => Assert.True(ForbiddenTargetPolicy.IsForbiddenFilePath(path));

    [Theory]
    [InlineData(@"C:\Users\Test\AppData\Local\Temp\file.tmp")]
    [InlineData(@"C:\Windows\Temp\setup.log")]
    [InlineData(@"C:\Windows\Minidump\012026-01.dmp")]
    public void Cleanable_locations_are_not_forbidden(string path)
        => Assert.False(ForbiddenTargetPolicy.IsForbiddenFilePath(path));

    [Theory]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Services\WinDefend", true)]
    [InlineData(@"HKCU\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", true)]
    [InlineData(@"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData(@"C:\Users\Test\AppData\Local\Temp\x.tmp", false)]
    [InlineData("service:SysMain", true)]
    [InlineData("SERVICE:wuauserv", true)]
    [InlineData("power:381b4222-f694-41f0-9685-ff5bb260df2e", false)]
    [InlineData("", false)]
    public void Textual_targets_are_classified(string target, bool forbidden)
        => Assert.Equal(forbidden, ForbiddenTargetPolicy.IsForbiddenTarget(target));
}
