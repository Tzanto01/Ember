using Npgsql;

namespace Ember.Ops.TestHost;

/// <summary>
/// Synthetic data for the dashboard, inserted as superuser with plain SQL so it works on both the production and the v2 schema:
/// required columns not set here get a neutral value, and v2-only tables and columns are filled only when they exist.
/// </summary>
public static class OpsSeed
{
    public const int Users = 12;

    /// <summary>Fills emberdb with a dozen users and their habits, sign-in sessions, reminders and devices.</summary>
    public static async Task SeedEmberAsync(string superuserEmberConnection, DateTime utcNow, CancellationToken ct = default)
    {
        var random = new Random(42);
        var today = DateOnly.FromDateTime(utcNow);
        await using var connection = new NpgsqlConnection(superuserEmberConnection);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var rows = new RowWriter(connection, transaction);

        var v2 = await rows.HasColumnAsync("Habits", "Kind", ct);
        var users = new List<Guid>();
        for (var i = 0; i < Users; i++)
        {
            var id = Guid.CreateVersion7();
            users.Add(id);
            var user = new Dictionary<string, object?>
            {
                ["Id"] = id,
                ["UserName"] = $"seed-user-{i}",
                ["ShowStreaks"] = i % 3 != 0,
                ["GraceDaysPerWeek"] = i % 3,
                ["SecondReminderMinutes"] = new[] { 0, 15, 30, 60, 120 }[i % 5],
                ["DmFailureCount"] = i == 4 ? 2 : 0,
                ["DmRemindersPausedAt"] = i == 5 ? utcNow.AddDays(-2) : null,
                ["DiscordReminders"] = i % 2 == 0 ? true : i % 3 == 0 ? false : null,
                ["Timezone"] = "Europe/Berlin",
                ["Language"] = "en",
            };
            await rows.InsertAsync("AspNetUsers", user, ct);
            if (i % 2 == 0)
                await rows.InsertAsync("AspNetUserLogins", new()
                {
                    ["LoginProvider"] = "Discord", ["ProviderKey"] = $"{900_000 + i}", ["UserId"] = id,
                }, ct, returning: null);
        }

        var routines = new List<(int Id, Guid User)>();
        for (var i = 0; i < 4; i++)
            routines.Add(((int)(await rows.InsertAsync("Routines", new()
            {
                ["UserId"] = users[i],
                ["Name"] = $"Seed routine {i}",
                ["CreatedAt"] = utcNow.AddDays(-60),
                ["NextReminderUtc"] = utcNow.AddHours(1 + i),
                ["SecondReminderUtc"] = i == 0 ? utcNow.AddMinutes(20) : null,
                ["Position"] = 0,
            }, ct))!, users[i]));

        var buildHabits = new List<(int Id, Guid User)>();
        for (var i = 0; i < Users; i++)
        {
            var firstDay = 5 + i * 6;
            var count = 1 + i % 3;
            for (var h = 0; h < count; h++)
            {
                var letGo = v2 && h == 0 && i < 5;
                var habit = new Dictionary<string, object?>
                {
                    ["UserId"] = users[i],
                    ["Name"] = $"Seed habit {i}.{h}",
                    ["CreatedAt"] = utcNow.AddDays(-firstDay + h),
                    ["RoutineId"] = h == 1 && i < 4 ? routines[i].Id : null,
                    ["PausedUntil"] = i == 7 && h == 0 ? today.AddDays(5) : null,
                    ["NextReminderUtc"] = h == 2 ? null : utcNow.AddHours(2 + h),
                    ["ReminderTimes"] = h == 2 ? Array.Empty<TimeOnly>() : new[] { TimeOnly.FromDateTime(utcNow.AddHours(2 + h)) },
                    ["SecondReminderUtc"] = i == 1 && h == 0 ? utcNow.AddMinutes(10) : null,
                    ["TimesPerDay"] = 1,
                };
                if (v2) habit["Kind"] = letGo ? 1 : 0;
                var id = (int)(await rows.InsertAsync("Habits", habit, ct))!;
                if (!letGo) buildHabits.Add((id, users[i]));
            }
        }

        for (var i = 0; i < Users; i++)
        {
            var seen = utcNow.AddDays(-i * 2).AddHours(-1);
            await rows.InsertAsync("AuthSessions", new()
            {
                ["Id"] = Guid.CreateVersion7(), ["UserId"] = users[i], ["CreatedUtc"] = utcNow.AddDays(-40), ["ExpiresUtc"] = utcNow.AddDays(20), ["LastSeenUtc"] = i % 4 == 3 ? null : seen,
            }, ct, returning: null);
            await rows.InsertAsync("AuthSessions", new()
            {
                ["Id"] = Guid.CreateVersion7(), ["UserId"] = users[i], ["CreatedUtc"] = seen, ["ExpiresUtc"] = utcNow.AddDays(60),
            }, ct, returning: null);
        }

        var messageId = 1_000_000m;
        for (var d = 0; d < 14; d++)
            foreach (var (habit, user) in buildHabits.Where((_, n) => n % 3 == d % 3))
            {
                await rows.InsertAsync("ReminderMessages", new()
                {
                    ["UserId"] = user, ["HabitId"] = habit, ["Date"] = today.AddDays(-d), ["ChannelId"] = 42m, ["MessageId"] = messageId++,
                }, ct, returning: null);
                if (random.NextDouble() < 0.5)
                    await rows.InsertAsync("PushReminders", new() { ["UserId"] = user, ["HabitId"] = habit, ["Date"] = today.AddDays(-d) }, ct, returning: null);
            }

        for (var i = 0; i < 5; i++)
            await rows.InsertAsync("PushSubscriptions", new()
            {
                ["UserId"] = users[i], ["Endpoint"] = $"https://push.example.test/{i}", ["P256dh"] = "synthetic", ["Auth"] = "synthetic",
                ["Enabled"] = i != 3, ["CreatedAt"] = utcNow.AddDays(-10),
            }, ct, returning: null);

        for (var i = 0; i < 3; i++)
            await rows.InsertAsync("ReminderPhones", new()
            {
                ["UserId"] = users[i + 5], ["InstallId"] = $"seed-install-{i}", ["Enabled"] = i != 2, ["CreatedAt"] = utcNow.AddDays(-7),
            }, ct, returning: null);

        await transaction.CommitAsync(ct);
    }

    private sealed record EmptyArray;

    private sealed class RowWriter(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        private readonly Dictionary<string, List<(string Name, string Type)>> _required = [];

        public async Task<bool> HasColumnAsync(string table, string column, CancellationToken ct)
        {
            await using var command = new NpgsqlCommand(
                "SELECT EXISTS (SELECT FROM information_schema.columns WHERE table_schema = 'public' AND table_name = $1 AND column_name = $2)",
                connection, transaction)
            { Parameters = { new() { Value = table }, new() { Value = column } } };
            return (bool)(await command.ExecuteScalarAsync(ct))!;
        }

        public async Task<object?> InsertAsync(string table, Dictionary<string, object?> values, CancellationToken ct, string? returning = "Id")
        {
            foreach (var (name, type) in await RequiredAsync(table, ct))
                values.TryAdd(name, Neutral(type));

            var columns = values.Keys.ToList();
            var parameters = columns.Where(c => values[c] is not EmptyArray).ToList();
            var sql = $"INSERT INTO \"{table}\" ({string.Join(", ", columns.Select(c => $"\"{c}\""))}) VALUES ({string.Join(", ", columns.Select(c => values[c] is EmptyArray ? "'{}'" : $"${parameters.IndexOf(c) + 1}"))})"
                + (returning is null ? "" : $" RETURNING \"{returning}\"");
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            foreach (var column in parameters) command.Parameters.Add(new NpgsqlParameter { Value = values[column] ?? DBNull.Value });
            return await command.ExecuteScalarAsync(ct);
        }

        private async Task<List<(string Name, string Type)>> RequiredAsync(string table, CancellationToken ct)
        {
            if (_required.TryGetValue(table, out var known)) return known;
            await using var command = new NpgsqlCommand("""
                SELECT column_name, data_type FROM information_schema.columns
                WHERE table_schema = 'public' AND table_name = $1 AND is_nullable = 'NO' AND column_default IS NULL AND is_identity = 'NO'
                """, connection, transaction) { Parameters = { new() { Value = table } } };
            var required = new List<(string, string)>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) required.Add((reader.GetString(0), reader.GetString(1)));
            return _required[table] = required;
        }

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
            "time without time zone" => TimeOnly.MinValue,
            "uuid" => Guid.NewGuid(),
            "bytea" => Array.Empty<byte>(),
            "ARRAY" => new EmptyArray(),
            _ => throw new NotSupportedException($"No neutral value for {type}"),
        };
    }
}
