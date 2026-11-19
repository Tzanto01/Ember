using Npgsql;

namespace Ember.Ops;

internal static class Db
{
    public static NpgsqlBatchCommand Command(string sql, params object[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var parameter in parameters) command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return command;
    }

    public static async Task<NpgsqlDataReader> RunAsync(NpgsqlConnection connection, IEnumerable<NpgsqlBatchCommand> commands, CancellationToken ct)
    {
        var batch = new NpgsqlBatch(connection);
        foreach (var command in commands) batch.BatchCommands.Add(command);
        return await batch.ExecuteReaderAsync(ct);
    }

    public static async Task<Dictionary<DateOnly, int>> DaysAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var days = new Dictionary<DateOnly, int>();
        while (await reader.ReadAsync(ct)) days[reader.GetFieldValue<DateOnly>(0)] = reader.GetInt32(1);
        await reader.NextResultAsync(ct);
        return days;
    }

    public static async Task<Dictionary<int, int>> CountsAsync(NpgsqlDataReader reader, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        while (await reader.ReadAsync(ct)) counts[reader.GetInt32(0)] = reader.GetInt32(1);
        await reader.NextResultAsync(ct);
        return counts;
    }

    public static async Task<NpgsqlDataReader> RowAsync(this NpgsqlDataReader reader, CancellationToken ct)
    {
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Expected a row.");
        return reader;
    }
}
