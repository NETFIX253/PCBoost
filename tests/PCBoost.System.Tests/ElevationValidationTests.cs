using System.Text;
using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Cleanup;
using PCBoost.Core.Common;
using PCBoost.Platform.Elevation;

namespace PCBoost.System.Tests;

public sealed class ElevationValidationTests
{
    private const string StartupApprovedRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private static readonly string ValidResultPath = @"C:\Users\Test\AppData\Local\PCBoost\elevation\" + Guid.NewGuid().ToString("D") + ".json";

    private static ElevatedRequest Request(string operation, params (string Key, string Value)[] parameters)
        => new(operation, parameters.ToDictionary(p => p.Key, p => p.Value));

    // ---- Opérations inconnues -------------------------------------------------------------------------------

    [Theory]
    [InlineData("shell.execute")]
    [InlineData("CLEANUP.CATEGORY")]
    [InlineData("registry.set ")]
    [InlineData("file.delete")]
    public void UnknownOperation_IsBlocked(string operation)
    {
        var result = ElevatedRequestValidator.Validate(Request(operation, ("path", @"C:\Windows")));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
        Assert.Equal("Sys_ElevatedOperationRefused", result.Message?.Key);
    }

    [Fact]
    public void NullOrEmptyRequest_IsInvalid()
    {
        Assert.Equal(OperationErrorKind.InvalidInput, ElevatedRequestValidator.Validate(null).Error);
        Assert.Equal(OperationErrorKind.InvalidInput, ElevatedRequestValidator.Validate(new ElevatedRequest("", new Dictionary<string, string>())).Error);
    }

    // ---- Nettoyage ------------------------------------------------------------------------------------------

    [Fact]
    public void Cleanup_ElevatedCatalogCategories_AreAccepted()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.CleanupCategory,
            ("categories", $"{CleanupCatalog.WindowsTemp}, {CleanupCatalog.SystemErrorReports},{CleanupCatalog.WindowsTemp}")));

        Assert.True(result.Success);
        var cleanup = Assert.IsType<ValidatedCleanup>(result.Value);
        Assert.Equal([CleanupCatalog.WindowsTemp, CleanupCatalog.SystemErrorReports], cleanup.Categories.Select(c => c.Id));
    }

    [Theory]
    [InlineData(CleanupCatalog.UserTemp)]      // Catégorie sans élévation : jamais exécutée en administrateur.
    [InlineData(CleanupCatalog.RecycleBin)]
    [InlineData("windows-temp;system32")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData("WINDOWS-TEMP")]
    public void Cleanup_NonElevatedOrUnknownCategory_IsBlocked(string categories)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.CleanupCategory, ("categories", categories)));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Fact]
    public void Cleanup_UnexpectedParameter_IsInvalid()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.CleanupCategory,
            ("categories", CleanupCatalog.WindowsTemp), ("path", @"C:\")));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Fact]
    public void Cleanup_EmptyCategoryList_IsInvalid()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.CleanupCategory, ("categories", " , ")));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    // ---- Registre -------------------------------------------------------------------------------------------

    [Fact]
    public void RegistrySet_AllowListedStartupApprovedValue_IsAccepted()
    {
        var value = new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistrySetValue,
            ("path", StartupApprovedRun.ToLowerInvariant()), ("name", "SomeApp"), ("valueBase64", Convert.ToBase64String(value)), ("view", "Registry64")));

        Assert.True(result.Success);
        var change = Assert.IsType<ValidatedRegistryChange>(result.Value);
        Assert.Equal(StartupApprovedRun, change.KeyPath); // Forme canonique de la liste blanche.
        Assert.Equal(RegistryHiveKind.LocalMachine, change.Location.Hive);
        Assert.Equal(RegistryViewKind.Registry64, change.View);
        Assert.Equal(value, change.Value);
    }

    [Fact]
    public void RegistryDelete_WithoutValue_IsAccepted()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistryDeleteValue,
            ("path", StartupApprovedRun + "32"), ("name", "SomeApp")));

        var change = Assert.IsType<ValidatedRegistryChange>(result.Value);
        Assert.Null(change.Value);
        Assert.Equal(ElevatedOperations.RegistryDeleteValue, change.Operation);
        Assert.Equal(RegistryViewKind.Default, change.View);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\..\..\..\Run")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved")]
    public void Registry_PathOutsideAllowList_IsBlocked(string path)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistrySetValue,
            ("path", path), ("name", "x"), ("valueBase64", Convert.ToBase64String(new byte[12]))));

        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(64)]
    public void RegistrySet_ValueLongerThan16Bytes_IsInvalid(int length)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistrySetValue,
            ("path", StartupApprovedRun), ("name", "x"), ("valueBase64", Convert.ToBase64String(new byte[length]))));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("")]
    public void RegistrySet_InvalidBase64_IsInvalid(string value)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistrySetValue,
            ("path", StartupApprovedRun), ("name", "x"), ("valueBase64", value)));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("registry64")]
    [InlineData("Registry128")]
    public void Registry_InvalidView_IsInvalid(string view)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistryDeleteValue,
            ("path", StartupApprovedRun), ("name", "x"), ("view", view)));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad\nname")]
    public void Registry_InvalidValueName_IsInvalid(string name)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistryDeleteValue,
            ("path", StartupApprovedRun), ("name", name)));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    [Fact]
    public void Registry_HiveParameter_IsRejected()
    {
        // La ruche n'est pas un paramètre : l'Elevator n'écrit que sous HKLM.
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.RegistryDeleteValue,
            ("path", StartupApprovedRun), ("name", "x"), ("hive", "CurrentUser")));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    // ---- Tâches planifiées ----------------------------------------------------------------------------------

    [Fact]
    public void Task_NonMicrosoftTask_IsAccepted()
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.ScheduledTaskSetEnabled,
            ("path", @"\Vendor\Updater Logon"), ("enabled", "false")));

        var toggle = Assert.IsType<ValidatedTaskToggle>(result.Value);
        Assert.False(toggle.Enabled);
    }

    [Theory]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"\MICROSOFT\Windows\WindowsUpdate\Scheduled Start")]
    public void Task_MicrosoftFolder_IsBlocked(string path)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.ScheduledTaskSetEnabled, ("path", path), ("enabled", "false")));

        Assert.Equal(OperationErrorKind.Blocked, result.Error);
    }

    [Theory]
    [InlineData("Vendor\\Task", "true")]      // Chemin relatif.
    [InlineData(@"\Vendor\..\Microsoft\X", "true")]
    [InlineData(@"\Vendor\Task", "yes")]
    [InlineData(@"\Vendor\Task", "1")]
    public void Task_InvalidParameters_AreInvalid(string path, string enabled)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.ScheduledTaskSetEnabled, ("path", path), ("enabled", enabled)));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    // ---- Capture d'images ------------------------------------------------------------------------------------

    [Fact]
    public void FrameCapture_ValidParameters_AreAccepted()
    {
        var pipe = ElevatedRequestValidator.NewFramePipeName();
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.FrameCapture, ("pid", "4242"), ("pipe", pipe), ("parentPid", "1000")));

        var capture = Assert.IsType<ValidatedFrameCapture>(result.Value);
        Assert.Equal(4242, capture.ProcessId);
        Assert.Equal(pipe, capture.PipeName);
        Assert.Equal(1000, capture.ParentProcessId);
    }

    [Theory]
    [InlineData("4", "PCBoost.Frames.0123456789abcdef0123456789abcdef", "1000")]
    [InlineData("-5", "PCBoost.Frames.0123456789abcdef0123456789abcdef", "1000")]
    [InlineData("+42", "PCBoost.Frames.0123456789abcdef0123456789abcdef", "1000")]
    [InlineData("42", "PCBoost.Frames.not-a-guid", "1000")]
    [InlineData("42", @"..\..\pipe\lsass", "1000")]
    [InlineData("42", "Other.Frames.0123456789abcdef0123456789abcdef", "1000")]
    [InlineData("42", "PCBoost.Frames.0123456789abcdef0123456789abcdef", "0")]
    public void FrameCapture_InvalidParameters_AreInvalid(string pid, string pipe, string parentPid)
    {
        var result = ElevatedRequestValidator.Validate(Request(ElevatedOperations.FrameCapture, ("pid", pid), ("pipe", pipe), ("parentPid", parentPid)));

        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }

    // ---- Codec base64url ------------------------------------------------------------------------------------

    [Fact]
    public void Codec_RoundTrip_UsesBase64UrlAlphabet()
    {
        var request = Request(ElevatedOperations.RegistrySetValue, ("path", StartupApprovedRun), ("name", "Café ü?/+"), ("valueBase64", "AwAAAA=="));

        var encoded = ElevatedRequestCodec.Encode(request);

        Assert.DoesNotContain('+', encoded);
        Assert.DoesNotContain('/', encoded);
        Assert.DoesNotContain('=', encoded);
        Assert.All(encoded, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
        Assert.True(ElevatedRequestCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(request.Operation, decoded!.Operation);
        Assert.Equal(request.Parameters.OrderBy(p => p.Key), decoded.Parameters.OrderBy(p => p.Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc+def")]
    [InlineData("abc/def")]
    [InlineData("abc=")]
    [InlineData("a b")]
    public void Codec_InvalidCharacters_AreRejected(string encoded)
        => Assert.False(ElevatedRequestCodec.TryDecode(encoded, out _));

    [Fact]
    public void Codec_OversizedOrNonJsonPayload_IsRejected()
    {
        Assert.False(ElevatedRequestCodec.TryDecode(new string('A', ElevatedRequestCodec.MaxEncodedLength + 1), out _));
        var notJson = global::System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes("not json"));
        Assert.False(ElevatedRequestCodec.TryDecode(notJson, out _));
        var nullParameters = global::System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"Operation\":\"x\",\"Parameters\":null}"));
        Assert.False(ElevatedRequestCodec.TryDecode(nullParameters, out _));
        var nullValue = global::System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes("{\"Operation\":\"x\",\"Parameters\":{\"a\":null}}"));
        Assert.False(ElevatedRequestCodec.TryDecode(nullValue, out _));
    }

    [Fact]
    public void ResponseJson_RoundTrip_NormalizesMessageArguments()
    {
        var response = new ElevatedResponse(
            OperationResult.Fail(OperationErrorKind.Failed, TextRef.Of("Sys_ElevationNoResult", 3, "x"), "détail"),
            new Dictionary<string, string> { ["windows-temp.bytes"] = "1024" });

        var decoded = ElevationJson.DeserializeResponse(ElevationJson.SerializeResponse(response));

        Assert.NotNull(decoded);
        Assert.False(decoded.Outcome.Success);
        Assert.Equal(OperationErrorKind.Failed, decoded.Outcome.Error);
        Assert.Equal("Sys_ElevationNoResult", decoded.Outcome.Message!.Key);
        Assert.Equal(new object[] { 3L, "x" }, decoded.Outcome.Message.Args);
        Assert.Equal("1024", decoded.Data["windows-temp.bytes"]);
    }

    [Fact]
    public void ResponseJson_Garbage_ReturnsNull()
        => Assert.Null(ElevationJson.DeserializeResponse("{ not json"u8));

    // ---- Arguments et chemin de résultat -------------------------------------------------------------------

    [Fact]
    public void Arguments_ExactSwitches_AreParsedInAnyOrder()
    {
        Assert.True(ElevatorArguments.TryParse(["--request", "abc", "--result", ValidResultPath], out var request, out var result));
        Assert.Equal("abc", request);
        Assert.Equal(ValidResultPath, result);

        Assert.True(ElevatorArguments.TryParse(["--result", ValidResultPath, "--request", "abc"], out request, out result));
        Assert.Equal("abc", request);
        Assert.Equal(ValidResultPath, result);
    }

    [Fact]
    public void Arguments_MissingDuplicatedOrExtra_AreRejected()
    {
        Assert.False(ElevatorArguments.TryParse([], out _, out _));
        Assert.False(ElevatorArguments.TryParse(["--request", "abc"], out _, out _));
        Assert.False(ElevatorArguments.TryParse(["--request", "abc", "--request", "def"], out _, out _));
        Assert.False(ElevatorArguments.TryParse(["--request", "abc", "--result", ValidResultPath, "--run", "cmd.exe"], out _, out _));
        Assert.False(ElevatorArguments.TryParse(["--REQUEST", "abc", "--result", ValidResultPath], out _, out _));
        Assert.False(ElevatorArguments.TryParse(["--request", "--result", "--result", ValidResultPath], out _, out _));
    }

    [Fact]
    public void Arguments_Build_QuotesResultPathAndRejectsInvalidPath()
    {
        Assert.Equal($"--request abc --result \"{ValidResultPath}\"", ElevatorArguments.Build("abc", ValidResultPath));
        Assert.Throws<ArgumentException>(() => ElevatorArguments.Build("abc", @"C:\Windows\System32\x.json"));
    }

    [Fact]
    public void ResultPath_ExpectedShape_IsAccepted()
    {
        Assert.True(ElevationPaths.IsValidResultPath(ValidResultPath));
        Assert.True(ElevationPaths.IsValidResultPath(@"D:\Profiles\jean.dupont\appdata\local\pcboost\ELEVATION\" + Guid.NewGuid().ToString("D") + ".JSON"));
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\elevation\result.json")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.txt")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\elevation\0f8fad5bd9cb469fa16570867728950e.json")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json:ads")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\elevation\..\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\x\..\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"\\server\share\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"\\?\C:\Users\Test\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"Users\Test\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"C:/Users/Test/AppData/Local/PCBoost/elevation/0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"C:\Users\Test\\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"C:\AppData\Local\PCBoost\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData(@"C:\Users\Test\AppData\Local\Other\elevation\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData("C:\\Users\\Te\"st\\AppData\\Local\\PCBoost\\elevation\\0f8fad5b-d9cb-469f-a165-70867728950e.json")]
    [InlineData("")]
    public void ResultPath_InvalidShapes_AreRejected(string path)
        => Assert.False(ElevationPaths.IsValidResultPath(path));

    [Fact]
    public void ResolvedResultPath_MustKeepSameFileNameAndDirectory()
    {
        var other = @"C:\Users\Test\AppData\Local\PCBoost\elevation\" + Guid.NewGuid().ToString("D") + ".json";
        Assert.True(ElevationPaths.IsAcceptableResolvedResultPath(ValidResultPath, ValidResultPath));
        Assert.False(ElevationPaths.IsAcceptableResolvedResultPath(other, ValidResultPath));
        Assert.False(ElevationPaths.IsAcceptableResolvedResultPath(@"C:\Windows\System32\" + ElevationPaths.WindowsFileName(ValidResultPath), ValidResultPath));
    }

    [Theory]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\Logs\elevator.log", true)]
    [InlineData(@"C:\Windows\System32\elevator.log", false)]
    [InlineData(@"C:\Users\Test\AppData\Local\PCBoost\Logs\other.log", false)]
    public void ResolvedLogPath_IsChecked(string path, bool expected)
        => Assert.Equal(expected, ElevationPaths.IsAcceptableResolvedLogPath(path));

    // ---- Diagnostics de santé (lecture seule, sauf point de restauration) -----------------------------------

    [Theory]
    [InlineData(ElevatedHealthOperations.DiskReliability)]
    [InlineData(ElevatedHealthOperations.BootPerformance)]
    [InlineData(ElevatedHealthOperations.RestorePointCreate)]
    [InlineData(ElevatedHealthOperations.AppsLastRun)]
    public void Health_operations_without_parameters_are_accepted(string operation)
    {
        var result = ElevatedRequestValidator.Validate(Request(operation));
        Assert.True(result.Success);
        Assert.Equal(operation, Assert.IsType<ValidatedHealthOperation>(result.Value).Name);
    }

    [Theory]
    [InlineData(ElevatedHealthOperations.DiskReliability)]
    [InlineData(ElevatedHealthOperations.RestorePointCreate)]
    [InlineData(ElevatedHealthOperations.AppsLastRun)]
    public void Health_operations_with_any_parameter_are_refused(string operation)
    {
        var result = ElevatedRequestValidator.Validate(Request(operation, ("path", @"C:\Windows\Prefetch")));
        Assert.False(result.Success);
        Assert.Equal(OperationErrorKind.InvalidInput, result.Error);
    }
}
