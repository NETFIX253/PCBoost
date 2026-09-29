using PCBoost.Core.Abstractions.Platform;
using PCBoost.Core.Models.SystemInfo;
using PCBoost.Core.Optimization;

namespace PCBoost.Core.Tests.Domain;

public sealed class ChangeStateTests
{
    private static readonly RegistryLocation Location = new(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");

    public static TheoryData<RegistryValueData> RegistryValues => new()
    {
        new RegistryValueData(RegistryValueType.String, "C:\\Program Files\\App\\app.exe --tray"),
        new RegistryValueData(RegistryValueType.String, string.Empty),
        new RegistryValueData(RegistryValueType.ExpandString, "%LOCALAPPDATA%\\App\\app.exe"),
        new RegistryValueData(RegistryValueType.DWord, 0),
        new RegistryValueData(RegistryValueType.DWord, 1),
        new RegistryValueData(RegistryValueType.DWord, unchecked((int)0xFFFFFFFF)),
        new RegistryValueData(RegistryValueType.DWord, int.MaxValue),
        new RegistryValueData(RegistryValueType.QWord, long.MaxValue),
        new RegistryValueData(RegistryValueType.QWord, -5L),
        new RegistryValueData(RegistryValueType.Binary, new byte[] { 0x02, 0x00, 0x00, 0x00, 0xFF, 0x10 }),
        new RegistryValueData(RegistryValueType.Binary, Array.Empty<byte>()),
        new RegistryValueData(RegistryValueType.MultiString, new[] { "a", "b c", "" }),
    };

    [Theory]
    [MemberData(nameof(RegistryValues))]
    public void RegistryValueState_round_trips_every_type_through_json(RegistryValueData original)
    {
        var state = RegistryValueState.Capture(Location, "MyApp", original);
        var json = ChangeStateSerializer.Serialize(state);
        var restored = ChangeStateSerializer.Deserialize<RegistryValueState>(json)!;

        Assert.True(restored.Existed);
        Assert.Equal(Location, restored.Location);
        Assert.Equal("MyApp", restored.ValueName);
        var data = restored.ToData()!;
        Assert.Equal(original.Type, data.Type);
        switch (original.Value)
        {
            case byte[] bytes: Assert.Equal(bytes, (byte[])data.Value); break;
            case string[] strings: Assert.Equal(strings, (string[])data.Value); break;
            default: Assert.Equal(original.Value, data.Value); break;
        }
    }

    [Fact]
    public void Missing_value_restores_to_deletion()
    {
        var state = RegistryValueState.Capture(Location, "Absent", null);
        var restored = ChangeStateSerializer.Deserialize<RegistryValueState>(ChangeStateSerializer.Serialize(state))!;

        Assert.False(restored.Existed);
        Assert.Null(restored.ToData());
    }

    [Fact]
    public void Unknown_registry_type_is_restored_as_a_string()
    {
        var state = RegistryValueState.Capture(Location, "Odd", new RegistryValueData(RegistryValueType.Unknown, "raw"));

        var data = state.ToData()!;
        Assert.Equal(RegistryValueType.String, data.Type);
        Assert.Equal("raw", data.Value);
    }

    [Fact]
    public void Serializer_writes_enums_as_names_and_omits_nulls()
    {
        var json = ChangeStateSerializer.Serialize(RegistryValueState.Capture(
            new RegistryLocation(RegistryHiveKind.LocalMachine, @"SOFTWARE\X", RegistryViewKind.Registry64), "V", RegistryValueData.DWord(3)));

        Assert.Contains("\"Hive\":\"LocalMachine\"", json);
        Assert.Contains("\"View\":\"Registry64\"", json);
        Assert.Contains("\"Type\":\"DWord\"", json);
        Assert.DoesNotContain("StringValue", json);
        Assert.DoesNotContain("BinaryBase64", json);
    }

    [Fact]
    public void Serializer_round_trips_other_change_states()
    {
        var start = new DateTimeOffset(2026, 9, 28, 10, 15, 0, TimeSpan.FromHours(3));
        var power = new PowerSchemeState(PowerScheme.Balanced, "Utilisation normale");
        var priority = new ProcessPriorityState(4242, "game.exe", start, ProcessPriority.AboveNormal);
        var efficiency = new ProcessEfficiencyState(17, "helper.exe", null, true);
        var task = new ScheduledTaskState(@"\Vendor\Updater", true);
        var deletion = new FileDeletionRecord("user-temp", 12, 4096);

        Assert.Equal(power, ChangeStateSerializer.Deserialize<PowerSchemeState>(ChangeStateSerializer.Serialize(power)));
        Assert.Equal(priority, ChangeStateSerializer.Deserialize<ProcessPriorityState>(ChangeStateSerializer.Serialize(priority)));
        Assert.Equal(efficiency, ChangeStateSerializer.Deserialize<ProcessEfficiencyState>(ChangeStateSerializer.Serialize(efficiency)));
        Assert.Equal(task, ChangeStateSerializer.Deserialize<ScheduledTaskState>(ChangeStateSerializer.Serialize(task)));
        Assert.Equal(deletion, ChangeStateSerializer.Deserialize<FileDeletionRecord>(ChangeStateSerializer.Serialize(deletion)));
        Assert.Contains("\"Priority\":\"AboveNormal\"", ChangeStateSerializer.Serialize(priority));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ not json")]
    [InlineData("[1,2,3]")]
    public void Deserialize_returns_default_for_missing_or_invalid_json(string? json)
        => Assert.Null(ChangeStateSerializer.Deserialize<PowerSchemeState>(json));
}
