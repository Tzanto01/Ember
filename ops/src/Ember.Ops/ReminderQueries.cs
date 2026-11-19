using Npgsql;

namespace Ember.Ops;

/// <summary>The reminder health check: whether the bot keeps advancing the next reminders.</summary>
public static class ReminderQueries
{
    /// <summary>Overdue = a next reminder more than this far in the past, which means the bot stopped advancing them.</summary>
    public const string OverdueAfter = "15 minutes";

    /// <summary>Habits and routines whose next reminder is overdue.</summary>
    public static async Task<int> OverdueAsync(NpgsqlDataSource ember, CancellationToken ct)
    {
        await using var command = ember.CreateCommand($"""
            SELECT (SELECT count(*) FROM "Habits" WHERE "NextReminderUtc" < now() - interval '{OverdueAfter}')::int
                 + (SELECT count(*) FROM "Routines" WHERE "NextReminderUtc" < now() - interval '{OverdueAfter}')::int
            """);
        return (int)(await command.ExecuteScalarAsync(ct))!;
    }
}
