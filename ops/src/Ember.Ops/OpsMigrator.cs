using Npgsql;

namespace Ember.Ops;

/// <summary>Applies the embedded <c>Migrations/*.sql</c> to emberops in name order, each once, tracked in <c>schema_migrations</c>.</summary>
public static class OpsMigrator
{
    private const long LockKey = 0x656D6265726F7073;

    /// <summary>Applies the pending migrations in one transaction under an advisory lock; returns their versions.</summary>
    public static async Task<IReadOnlyList<string>> ApplyAsync(NpgsqlDataSource ops, CancellationToken ct = default)
    {
        var assembly = typeof(OpsMigrator).Assembly;
        var scripts = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("Migrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        await using var connection = await ops.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await Execute(connection, transaction, $"SELECT pg_advisory_xact_lock({LockKey})", ct);
        await Execute(connection, transaction,
            "CREATE TABLE IF NOT EXISTS schema_migrations (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())", ct);

        var applied = new HashSet<string>();
        await using (var command = new NpgsqlCommand("SELECT version FROM schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) applied.Add(reader.GetString(0));

        var done = new List<string>();
        foreach (var name in scripts)
        {
            var version = name["Migrations.".Length..^".sql".Length];
            if (applied.Contains(version)) continue;

            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var text = new StreamReader(stream);
            await Execute(connection, transaction, await text.ReadToEndAsync(ct), ct);
            await using var record = new NpgsqlCommand("INSERT INTO schema_migrations (version) VALUES ($1)", connection, transaction);
            record.Parameters.Add(new NpgsqlParameter { Value = version });
            await record.ExecuteNonQueryAsync(ct);
            done.Add(version);
        }

        await transaction.CommitAsync(ct);
        return done;
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(ct);
    }
}
