using PCBoost.Core.Models.Programs;
using PCBoost.Core.Programs;

namespace PCBoost.Core.Tests.Programs;

public sealed class UninstallCommandParserTests
{
    [Theory]
    [InlineData("MsiExec.exe /I{3B2A1C4D-1111-2222-3333-444455556666}")]
    [InlineData("MsiExec.exe /X{3b2a1c4d-1111-2222-3333-444455556666}")]
    [InlineData(@"""C:\Windows\System32\msiexec.exe"" /qn /x {3B2A1C4D-1111-2222-3333-444455556666}")]
    public void Windows_installer_is_always_interactive_uninstall(string text)
    {
        var command = UninstallCommandParser.Parse(text)!;
        Assert.True(command.IsWindowsInstaller);
        Assert.Equal("{3B2A1C4D-1111-2222-3333-444455556666}", command.ProductCode);
        Assert.Equal("/x {3B2A1C4D-1111-2222-3333-444455556666}", command.Arguments);
        Assert.DoesNotContain("/q", command.Arguments);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\VideoLAN\VLC\uninstall.exe""", @"C:\Program Files\VideoLAN\VLC\uninstall.exe", "")]
    [InlineData(@"""C:\Program Files (x86)\App\unins000.exe"" /SILENT", @"C:\Program Files (x86)\App\unins000.exe", "/SILENT")]
    [InlineData(@"C:\Program Files\7-Zip\Uninstall.exe", @"C:\Program Files\7-Zip\Uninstall.exe", "")]
    [InlineData(@"C:\Users\x\AppData\Local\Discord\Update.exe --uninstall", @"C:\Users\x\AppData\Local\Discord\Update.exe", "--uninstall")]
    public void Publisher_uninstaller_is_accepted(string text, string file, string args)
    {
        var command = UninstallCommandParser.Parse(text)!;
        Assert.False(command.IsWindowsInstaller);
        Assert.Equal(file, command.FileName);
        Assert.Equal(args, command.Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("uninstall.exe")]
    [InlineData(@"cmd.exe /c del C:\*")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c rd /s /q C:\App")]
    [InlineData(@"""C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" -File x.ps1")]
    [InlineData(@"RunDll32 C:\PROGRA~1\App\setup.dll,Uninstall")]
    [InlineData(@"C:\Windows\System32\rundll32.exe C:\x.dll,Remove")]
    [InlineData(@"""C:\App\..\Windows\evil.exe""")]
    [InlineData(@"C:\App\setup.exenope")]
    [InlineData("MsiExec.exe /X{not-a-guid}")]
    [InlineData(@"\\server\share\uninstall.exe")]
    [InlineData(@"""C:\App\uninstall.bat""")]
    public void Unsafe_or_unknown_commands_are_refused(string? text) => Assert.Null(UninstallCommandParser.Parse(text));

    [Fact]
    public void Rarely_used_needs_a_known_old_date()
    {
        var now = new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
        InstalledProgram P(DateTimeOffset? last) => new("id", "App", null, null, null, null, false, null, ProgramScope.Machine, null, last, LastUseSource.FileAccess, null);
        Assert.True(P(now.AddDays(-120)).IsRarelyUsed(now));
        Assert.False(P(now.AddDays(-30)).IsRarelyUsed(now));
        Assert.False(P(null).IsRarelyUsed(now));
        Assert.False(P(null).CanUninstall);
    }
}
