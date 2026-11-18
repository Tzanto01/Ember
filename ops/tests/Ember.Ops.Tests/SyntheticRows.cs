using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>Adds synthetic rows through a superuser connection; required columns the test does not care about get a neutral value.</summary>
public sealed class SyntheticRows(NpgsqlConnection connection)
{
    private readonly Dictionary<string, List<(string Name, string Type)>> _required = [];

    public Task AddAsync(string table, Dictionary<string, object?> values) => RunAsync(table, values, "1");

    public async Task<int> AddReturningIdAsync(string table, Dictionary<string, object?> values) =>
        (int)(await RunAsync(table, values, "\"Id\""))!;

    private async Task<object?> RunAsync(string table, Dictionary<string, object?> values, string returning)
    {
        foreach (var (name, type) in await RequiredAsync(table))
            values.TryAdd(name, Neutral(type));

        var columns = values.Keys.ToList();
        var parameters = columns.Where(c => values[c] is not EmptyArray).ToList();
        var placeholders = columns.Select(c => values[c] is EmptyArray ? "'{}'" : $"${parameters.IndexOf(c) + 1}");
        var sql = $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) " +
                  $"VALUES ({string.Join(", ", placeholders)}) RETURNING {returning}";
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var column in parameters) command.Parameters.Add(new NpgsqlParameter { Value = values[column] ?? DBNull.Value });
        return await command.ExecuteScalarAsync();
    }

    private async Task<List<(string Name, string Type)>> RequiredAsync(string table)
    {
        if (_required.TryGetValue(table, out var known)) return known;
        await using var command = new NpgsqlCommand("""
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = $1 AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO'
            """, connection) { Parameters = { new() { Value = table } } };
        var required = new List<(string, string)>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) required.Add((reader.GetString(0), reader.GetString(1)));
        return _required[table] = required;
    }

    private sealed record EmptyArray;

    private static object Neutral(string type) => type switch
    {
        "boolean" => false,
        "integer" => 0,
        "smallint" => (short)0,
        "bigint" => 0L,
        "numeric" => 0m,
        "double precision" => 0d,
        "text" or "character varying" => string.Empty,
        "timestamp with time zone" => DateTime.UtcNow,
        "date" => DateOnly.FromDateTime(DateTime.UtcNow),
        "uuid" => Guid.NewGuid(),
        "bytea" => Array.Empty<byte>(),
        "ARRAY" => new EmptyArray(),
        _ => throw new NotSupportedException($"No neutral value for {type}"),
    };
}
