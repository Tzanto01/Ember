using Npgsql;

namespace Ember.Ops;

public sealed record OverviewUsers(int Total, int Active7, int Active14, IReadOnlyList<WeekCount> NewPerWeek);
public sealed record WeekCount(DateOnly Week, int Count);
public sealed record HabitCounts(int Build, int? LetGo, int Paused, int WithReminder);
public sealed record RoutineCounts(int Total, int Users);
public sealed record GraceCount(int Days, int Count);
public sealed record StreakCounts(int Users, int ShowStreaks, IReadOnlyList<GraceCount> Grace);

/// <summary>The cheap counters of the overview, refreshed often.</summary>
public sealed record OverviewCounters(
    DateTime GeneratedAt, int Users, int Active7, int Active14, HabitCounts Habits, RoutineCounts Routines, StreakCounts Streaks);

/// <summary>The 12-week series of the overview and the active-user history, refreshed rarely.</summary>
/// <param name="UsersBefore">Users whose first habit is older than the first of the 12 weeks.</param>
/// <param name="Active7PerDay">Users seen in the 7 days up to each of the last 14 days, from the dashboard's snapshots; days without one are left out.</param>
/// <param name="Active7Before">Users seen in the 7 days up to 7 days ago, or null without a snapshot that day.</param>
public sealed record OverviewSeries(
    DateTime GeneratedAt, IReadOnlyList<WeekCount> NewPerWeek, int UsersBefore, IReadOnlyList<DayCount> Active7PerDay, int? Active7Before);

/// <summary>The overview stats; <see cref="GeneratedAt"/> is when the counters were read.</summary>
public sealed record Overview(
    DateTime GeneratedAt, OverviewUsers Users, HabitCounts Habits, RoutineCounts Routines, StreakCounts Streaks, OverviewSeries Series)
{
    public static Overview From(OverviewCounters counters, OverviewSeries series) => new(
        counters.GeneratedAt,
        new(counters.Users, counters.Active7, counters.Active14, series.NewPerWeek),
        counters.Habits, counters.Routines, counters.Streaks, series);
}

/// <summary>Aggregate reads for the overview page. Counts only; no per-user data leaves the database.</summary>
public static class OverviewQueries
{
    public const int SeriesWeeks = 12;
    public const int TrendDays = 14;

    public static async Task<OverviewCounters> CountersAsync(NpgsqlDataSource ember, EmberFeatures features, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;

        await using var connection = await ember.OpenConnectionAsync(ct);
        List<NpgsqlBatchCommand> commands =
        [
            Db.Command("""SELECT count(*)::int, count(*) FILTER (WHERE "ShowStreaks")::int FROM "AspNetUsers" """),
            Db.Command("""
                SELECT count(DISTINCT "UserId") FILTER (WHERE seen >= (($1::date - 6)::timestamp AT TIME ZONE 'Europe/Berlin'))::int, count(DISTINCT "UserId")::int
                FROM (SELECT "UserId", coalesce("LastSeenUtc", "CreatedUtc") AS seen FROM "AuthSessions") s
                WHERE seen >= (($1::date - 13)::timestamp AT TIME ZONE 'Europe/Berlin')
                """, today),
            Db.Command($"""
                SELECT {(features.HabitKind ? "count(*) FILTER (WHERE \"Kind\" = 0)::int, count(*) FILTER (WHERE \"Kind\" = 1)::int" : "count(*)::int, NULL::int")},
                       count(*) FILTER (WHERE "PausedUntil" >= $1 AND ("PausedFrom" IS NULL OR "PausedFrom" <= $1))::int, count(*) FILTER (WHERE "NextReminderUtc" IS NOT NULL)::int
                FROM "Habits"
                """, today),
            Db.Command("""SELECT count(*)::int, count(DISTINCT "UserId")::int FROM "Routines" """),
            Db.Command("""SELECT "GraceDaysPerWeek", count(*)::int FROM "AspNetUsers" GROUP BY 1 ORDER BY 1"""),
        ];
        await using var reader = await Db.RunAsync(connection, commands, ct);

        await reader.RowAsync(ct);
        var (users, showStreaks) = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var (active7, active14) = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var habits = new HabitCounts(reader.GetInt32(0), Nullable(reader, 1), reader.GetInt32(2), reader.GetInt32(3));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var routines = new RoutineCounts(reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        var grace = new List<GraceCount>();
        while (await reader.ReadAsync(ct)) grace.Add(new(reader.GetInt32(0), reader.GetInt32(1)));

        return new(generatedAt, users, active7, active14, habits, routines, new(users, showStreaks, grace));
    }

    public static async Task<OverviewSeries> SeriesAsync(NpgsqlDataSource ember, NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var weeks = OpsCalendar.Weeks(today, SeriesWeeks).ToList();

        Dictionary<DateOnly, int> firstHabit;
        int usersBefore;
        await using (var connection = await ember.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT first, count(*)::int FROM (
                    SELECT (min("CreatedAt") AT TIME ZONE 'Europe/Berlin')::date AS first FROM "Habits" GROUP BY "UserId") f
                WHERE first BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today),
            Db.Command("""
                SELECT count(*)::int FROM (SELECT min("CreatedAt") AS first FROM "Habits" GROUP BY "UserId") f
                WHERE first < ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin'
                """, weeks[0]),
        ], ct))
        {
            firstHabit = await Db.DaysAsync(reader, ct);
            await reader.RowAsync(ct);
            usersBefore = reader.GetInt32(0);
        }

        Dictionary<DateOnly, int> active7;
        await using (var connection = await ops.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
            [Db.Command("SELECT day, active7 FROM activity_days WHERE day BETWEEN $1 AND $2", today.AddDays(-(TrendDays - 1)), today)], ct))
            active7 = await Db.DaysAsync(reader, ct);

        var newPerWeek = weeks.Select(w => new WeekCount(w, firstHabit.Where(d => OpsCalendar.Monday(d.Key) == w).Sum(d => d.Value))).ToList();
        return new(generatedAt, newPerWeek, usersBefore, active7.OrderBy(d => d.Key).Select(d => new DayCount(d.Key, d.Value)).ToList(),
            active7.TryGetValue(today.AddDays(-7), out var before) ? before : null);
    }

    private static int? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
