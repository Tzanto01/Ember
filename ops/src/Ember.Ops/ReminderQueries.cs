using Npgsql;

namespace Ember.Ops;

public sealed record ChannelDay(DateOnly Date, int Discord, int Push);
public sealed record ReminderHistory(DateTime? Since, IReadOnlyList<ChannelDay> PerDay);
public sealed record DiscordCounts(int RemindersOn, int Failing, int Paused);
public sealed record MinutesCount(int Minutes, int Count);
public sealed record SecondReminderCounts(int UsersOn, IReadOnlyList<MinutesCount> ByMinutes, int PendingNow);
public sealed record PushCounts(int Subscriptions, int Enabled);
public sealed record PhoneCounts(int Total, int Enabled);
public sealed record OverdueCounts(int Habits, int Routines);

/// <summary>The current reminder state read from Ember, refreshed often.</summary>
public sealed record ReminderCounters(
    DateTime GeneratedAt, IReadOnlyList<ChannelDay> OpenPerDay, DiscordCounts Discord, SecondReminderCounts SecondReminders,
    PushCounts Push, PhoneCounts Phones, OverdueCounts Overdue);

/// <summary>The reminder stats.</summary>
public sealed record Reminders(
    DateTime GeneratedAt, IReadOnlyList<ChannelDay> OpenPerDay, ReminderHistory History, DiscordCounts Discord,
    SecondReminderCounts SecondReminders, PushCounts Push, PhoneCounts Phones, OverdueCounts Overdue)
{
    public static Reminders From(ReminderCounters c, ReminderHistory history) =>
        new(c.GeneratedAt, c.OpenPerDay, history, c.Discord, c.SecondReminders, c.Push, c.Phones, c.Overdue);
}

/// <summary>Aggregate reads for the reminders page, plus the snapshot of Ember's reminder id sequences.</summary>
public static class ReminderQueries
{
    public const int OpenDays = 14;
    public const int HistoryDays = 90;

    /// <summary>Overdue = a next reminder more than this far in the past, which means the bot stopped advancing them.</summary>
    public const string OverdueAfter = "15 minutes";

    public static async Task<ReminderCounters> CountersAsync(NpgsqlDataSource ember, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var from = today.AddDays(-(OpenDays - 1));

        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("""SELECT "Date", count(*)::int FROM "ReminderMessages" WHERE "Date" BETWEEN $1 AND $2 GROUP BY 1""", from, today),
            Db.Command("""SELECT "Date", count(*)::int FROM "PushReminders" WHERE "Date" BETWEEN $1 AND $2 GROUP BY 1""", from, today),
            Db.Command("""
                SELECT count(*) FILTER (WHERE "DiscordReminders" IS TRUE AND "DmRemindersPausedAt" IS NULL
                                         AND EXISTS (SELECT FROM "AspNetUserLogins" l WHERE l."UserId" = u."Id" AND l."LoginProvider" = 'Discord'))::int,
                       count(*) FILTER (WHERE "DmFailureCount" > 0)::int,
                       count(*) FILTER (WHERE "DmRemindersPausedAt" IS NOT NULL)::int, count(*) FILTER (WHERE "SecondReminderMinutes" > 0)::int
                FROM "AspNetUsers" u
                """),
            Db.Command("""SELECT "SecondReminderMinutes", count(*)::int FROM "AspNetUsers" WHERE "SecondReminderMinutes" > 0 GROUP BY 1 ORDER BY 1"""),
            Db.Command($"""
                SELECT (SELECT count(*) FROM "Habits" WHERE "SecondReminderUtc" IS NOT NULL)::int
                     + (SELECT count(*) FROM "Routines" WHERE "SecondReminderUtc" IS NOT NULL)::int,
                       (SELECT count(*) FROM "Habits" WHERE "NextReminderUtc" < now() - interval '{OverdueAfter}')::int,
                       (SELECT count(*) FROM "Routines" WHERE "NextReminderUtc" < now() - interval '{OverdueAfter}')::int
                """),
            Db.Command("""SELECT count(*)::int, count(*) FILTER (WHERE "Enabled")::int FROM "PushSubscriptions" """),
            Db.Command("""SELECT count(*)::int, count(*) FILTER (WHERE "Enabled")::int FROM "ReminderPhones" """),
        ], ct);

        var discordDays = await Db.DaysAsync(reader, ct);
        var pushDays = await Db.DaysAsync(reader, ct);
        var open = OpsCalendar.Days(from, today, discordDays)
            .Select(d => new ChannelDay(d.Date, d.Count, pushDays.GetValueOrDefault(d.Date))).ToList();

        await reader.RowAsync(ct);
        var discord = new DiscordCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        var secondOn = reader.GetInt32(3);
        await reader.NextResultAsync(ct);

        var byMinutes = new List<MinutesCount>();
        while (await reader.ReadAsync(ct)) byMinutes.Add(new(reader.GetInt32(0), reader.GetInt32(1)));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var pending = reader.GetInt32(0);
        var overdue = new OverdueCounts(reader.GetInt32(1), reader.GetInt32(2));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var push = new PushCounts(reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var phones = new PhoneCounts(reader.GetInt32(0), reader.GetInt32(1));

        return new(generatedAt, open, discord, new(secondOn, byMinutes, pending), push, phones, overdue);
    }

    /// <summary>
    /// Reminders sent per Europe/Berlin day, from the positive growth of Ember's reminder id sequences between snapshots
    /// (includes second reminders and resends). <see cref="ReminderHistory.Since"/> is the first snapshot.
    /// </summary>
    public static async Task<ReminderHistory> HistoryAsync(NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var from = today.AddDays(-(HistoryDays - 1));

        await using var connection = await ops.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("SELECT min(at) FROM reminder_counters"),
            Db.Command("""
                SELECT at, discord_max_id, push_max_id FROM reminder_counters
                WHERE at >= coalesce((SELECT max(at) FROM reminder_counters WHERE at < $1), '-infinity')
                ORDER BY at
                """, time.GetUtcNow().UtcDateTime.AddDays(-HistoryDays)),
        ], ct);

        await reader.RowAsync(ct);
        DateTime? since = reader.IsDBNull(0) ? null : reader.GetDateTime(0);
        await reader.NextResultAsync(ct);

        var discord = new Dictionary<DateOnly, int>();
        var push = new Dictionary<DateOnly, int>();
        (int Discord, int Push)? previous = null;
        while (await reader.ReadAsync(ct))
        {
            var (at, d, p) = (reader.GetDateTime(0), reader.GetInt32(1), reader.GetInt32(2));
            if (previous is { } last)
            {
                var day = OpsCalendar.BerlinDate(at);
                discord[day] = discord.GetValueOrDefault(day) + Math.Max(0, d - last.Discord);
                push[day] = push.GetValueOrDefault(day) + Math.Max(0, p - last.Push);
            }
            previous = (d, p);
        }

        var perDay = OpsCalendar.Days(from, today, discord).Select(x => new ChannelDay(x.Date, x.Count, push.GetValueOrDefault(x.Date))).ToList();
        return new(since, perDay);
    }

    /// <summary>
    /// Stores the current last values of the <c>ReminderMessages</c> and <c>PushReminders</c> id sequences (never used = 0)
    /// when either differs from the newest snapshot. Returns whether a row was written.
    /// </summary>
    public static async Task<bool> SnapshotAsync(NpgsqlDataSource ember, NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        int discord, push;
        await using (var command = ember.CreateCommand("""
            SELECT coalesce(pg_sequence_last_value(pg_get_serial_sequence('"ReminderMessages"', 'Id')), 0)::int,
                   coalesce(pg_sequence_last_value(pg_get_serial_sequence('"PushReminders"', 'Id')), 0)::int
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            await reader.RowAsync(ct);
            (discord, push) = (reader.GetInt32(0), reader.GetInt32(1));
        }

        await using var insert = ops.CreateCommand("""
            INSERT INTO reminder_counters (at, discord_max_id, push_max_id)
            SELECT $1, $2, $3
            WHERE NOT EXISTS (SELECT FROM (SELECT discord_max_id, push_max_id FROM reminder_counters ORDER BY at DESC LIMIT 1) l
                              WHERE l.discord_max_id = $2 AND l.push_max_id = $3)
            ON CONFLICT (at) DO NOTHING
            """);
        insert.Parameters.Add(new NpgsqlParameter { Value = time.GetUtcNow().UtcDateTime });
        insert.Parameters.Add(new NpgsqlParameter { Value = discord });
        insert.Parameters.Add(new NpgsqlParameter { Value = push });
        return await insert.ExecuteNonQueryAsync(ct) == 1;
    }
}
