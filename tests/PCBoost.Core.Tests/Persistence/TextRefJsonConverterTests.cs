using System.Text.Json;
using PCBoost.Core.Common;
using PCBoost.Persistence.Serialization;

namespace PCBoost.Core.Tests.Storage;

public sealed class TextRefJsonConverterTests
{
    [Fact]
    public void Writes_compact_json_with_type_tags_only_where_needed()
    {
        var json = PersistenceJson.Serialize(TextRef.Of("K", 1, "a", true, 2.0, 3L));

        Assert.Equal("{\"key\":\"K\",\"args\":[1,\"a\",true,{\"$type\":\"double\",\"value\":2},{\"$type\":\"long\",\"value\":3}]}", json);
    }

    [Fact]
    public void Untagged_numbers_from_foreign_json_are_read_as_int_long_or_double()
    {
        var text = PersistenceJson.Deserialize<TextRef>("{\"Key\":\"K\",\"Args\":[5, 6000000000, 1.25, null, \"s\"]}")!;

        Assert.IsType<int>(text.Args[0]);
        Assert.IsType<long>(text.Args[1]);
        Assert.IsType<double>(text.Args[2]);
        Assert.Null(text.Args[3]);
        Assert.Equal("s", text.Args[4]);
    }

    [Fact]
    public void Dates_guids_and_special_doubles_round_trip()
    {
        var date = new DateTimeOffset(2026, 9, 28, 8, 0, 0, TimeSpan.FromHours(2));
        var guid = Guid.NewGuid();
        var original = TextRef.Of("K", date, guid, double.NaN, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), ulong.MaxValue);

        var restored = PersistenceJson.Deserialize<TextRef>(PersistenceJson.Serialize(original))!;

        Assert.Equal(date, restored.Args[0]);
        Assert.Equal(date.Offset, ((DateTimeOffset)restored.Args[0]).Offset);
        Assert.Equal(guid, restored.Args[1]);
        Assert.True(double.IsNaN((double)restored.Args[2]));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), restored.Args[3]);
        Assert.Equal(ulong.MaxValue, restored.Args[4]);
    }

    [Fact]
    public void Enums_are_stored_as_their_display_name()
    {
        var restored = PersistenceJson.Deserialize<TextRef>(PersistenceJson.Serialize(TextRef.Of("K", DayOfWeek.Monday)))!;

        Assert.Equal("Monday", restored.Args[0]);
    }

    [Theory]
    [InlineData("{\"args\":[]}")]
    [InlineData("[1]")]
    [InlineData("{\"key\":\"K\",\"args\":[{\"$type\":\"guid\",\"value\":\"not-a-guid\"}]}")]
    public void Invalid_json_is_rejected(string json)
        => Assert.Throws<JsonException>(() => PersistenceJson.Deserialize<TextRef>(json));
}
