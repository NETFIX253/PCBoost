using System.Globalization;
using Microsoft.Data.Sqlite;

namespace PCBoost.Persistence.Repositories;

/// <summary>Conversions et paramètres communs aux dépôts SQLite.</summary>
internal static class Sql
{
    public static SqliteCommand Command(SqliteConnection connection, string text, SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.CommandText = text;
        command.Transaction = transaction;
        return command;
    }

    public static void Add(SqliteCommand command, string name, object? value)
        => command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    /// <summary>Dates : ticks UTC (tri et filtrage exacts, fuseau neutre).</summary>
    public static long ToTicks(DateTimeOffset value) => value.UtcTicks;

    public static object ToTicks(DateTimeOffset? value) => value.HasValue ? value.Value.UtcTicks : DBNull.Value;

    public static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    public static string ToText(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    public static object ToText(Guid? value) => value.HasValue ? value.Value.ToString("D", CultureInfo.InvariantCulture) : DBNull.Value;

    public static string ToText<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString();

    public static TEnum ParseEnum<TEnum>(string? text, TEnum fallback) where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(text, ignoreCase: true, out var value) ? value : fallback;

    public static Guid GetGuid(SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));

    public static Guid? GetNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.TryParse(reader.GetString(ordinal), out var g) ? g : null;

    public static string? GetNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    public static DateTimeOffset GetDate(SqliteDataReader reader, int ordinal) => FromTicks(reader.GetInt64(ordinal));

    public static DateTimeOffset? GetNullableDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : FromTicks(reader.GetInt64(ordinal));

    public static double? GetNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    public static int? GetNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    /// <summary>Clause « IN » paramétrée ($p0, $p1…).</summary>
    public static string InClause(SqliteCommand command, string prefix, IEnumerable<string> values)
    {
        var names = new List<string>();
        foreach (var value in values)
        {
            var name = "$" + prefix + names.Count.ToString(CultureInfo.InvariantCulture);
            command.Parameters.AddWithValue(name, value);
            names.Add(name);
        }
        return "(" + string.Join(", ", names) + ")";
    }
}
