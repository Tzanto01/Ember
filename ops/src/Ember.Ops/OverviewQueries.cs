using Npgsql;

namespace Ember.Ops;

public sealed record OverviewUsers(int Total, int Active7, int Active30, IReadOnlyList<WeekCount> NewPerWeek);
public sealed record WeekCount(DateOnly Week, int Count);
public sealed record HabitCounts(int Build, int? LetGo, int Paused, int WithReminder);
public sealed record RoutineCounts(int Total, int Users);
public sealed record GraceCount(int Days, int Count);
public sealed record StreakCounts(int Users, int ShowStreaks, IReadOnlyList<GraceCount> Grace);
public sealed record FiresideStats(int UsersOn, int Friendships, IReadOnlyList<DayCount> WarmthPerDay);
public sealed record SlipWeek(DateOnly Week, int Slips, int Days);

/// <summary>The cheap counters of the overview, refreshed often.</summary>
public sealed record OverviewCounters(
    DateTime GeneratedAt, int Users, int Active7, int Active30, HabitCounts Habits, RoutineCounts Routines, StreakCounts Streaks,
    int? FiresideUsersOn, int? Friendships);

/// <summary>The 90-day and 12-week series of the overview, refreshed rarely.</summary>
/// <param name="UsersBefore">Users whose first habit is older than the first of the 12 weeks.</param>
/// <param name="Active7PerDay">For each of the last 14 days, the users with a completed check-in in the 7 days ending that day.</param>
/// <param name="Active7Before">Users with a completed check-in 13 to 7 days ago.</param>
/// <param name="Active30Before">Users with a completed check-in 59 to 30 days ago.</param>
/// <param name="CheckInUsersPerDay">The distinct users behind <see cref="CheckInsPerDay"/>.</param>
public sealed record OverviewSeries(
    DateTime GeneratedAt, IReadOnlyList<WeekCount> NewPerWeek, IReadOnlyList<DayCount> CheckInsPerDay, IReadOnlyList<DayCount> JournalPerDay,
    IReadOnlyList<DayCount> LowDaysPerDay, IReadOnlyList<DayCount>? WarmthPerDay, IReadOnlyList<SlipWeek>? LetGoSlipsPerWeek,
    int UsersBefore, IReadOnlyList<DayCount> Active7PerDay, int Active7Before, int Active30Before, IReadOnlyList<DayCount> CheckInUsersPerDay);

/// <summary>The overview stats; <see cref="GeneratedAt"/> is when the counters were read.</summary>
public sealed record Overview(
    DateTime GeneratedAt, OverviewUsers Users, IReadOnlyList<DayCount> CheckInsPerDay, HabitCounts Habits, RoutineCounts Routines,
    IReadOnlyList<DayCount> JournalPerDay, IReadOnlyList<DayCount> LowDaysPerDay, StreakCounts Streaks, FiresideStats? Fireside,
    IReadOnlyList<SlipWeek>? LetGoSlipsPerWeek, OverviewSeries Series)
{
    public static Overview From(OverviewCounters counters, OverviewSeries series) => new(
        counters.GeneratedAt,
        new(counters.Users, counters.Active7, counters.Active30, series.NewPerWeek),
        series.CheckInsPerDay, counters.Habits, counters.Routines, series.JournalPerDay, series.LowDaysPerDay, counters.Streaks,
        counters is { FiresideUsersOn: { } on, Friendships: { } friendships } && series.WarmthPerDay is { } warmth ? new(on, friendships, warmth) : null,
        series.LetGoSlipsPerWeek, series);
}

/// <summary>Aggregate reads for the overview page. Counts only; no per-user data leaves the database.</summary>
public static class OverviewQueries
{
    public const int SeriesDays = 90;
    public const int SeriesWeeks = 12;
    public const int WarmthDays = 30;
    public const int TrendDays = 14;

    public static async Task<OverviewCounters> CountersAsync(NpgsqlDataSource ember, EmberFeatures features, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;

        await using var connection = await ember.OpenConnectionAsync(ct);
        List<NpgsqlBatchCommand> commands =
        [
            Db.Command($"""
                SELECT count(*)::int, count(*) FILTER (WHERE "ShowStreaks")::int,
                       {(features.Fireside ? "count(*) FILTER (WHERE \"FiresideEnabled\")::int" : "NULL::int")}
                FROM "AspNetUsers"
                """),
            Db.Command("""
                SELECT count(DISTINCT h."UserId") FILTER (WHERE l."Date" >= $1::date - 6)::int, count(DISTINCT h."UserId")::int
                FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                WHERE l."Completed" AND l."Date" BETWEEN $1::date - 29 AND $1::date
                """, today),
            Db.Command($"""
                SELECT {(features.HabitKind ? "count(*) FILTER (WHERE \"Kind\" = 0)::int, count(*) FILTER (WHERE \"Kind\" = 1)::int" : "count(*)::int, NULL::int")},
                       count(*) FILTER (WHERE "PausedUntil" >= $1 AND ("PausedFrom" IS NULL OR "PausedFrom" <= $1))::int, count(*) FILTER (WHERE "NextReminderUtc" IS NOT NULL)::int
                FROM "Habits"
                """, today),
            Db.Command("""SELECT count(*)::int, count(DISTINCT "UserId")::int FROM "Routines" """),
            Db.Command("""SELECT "GraceDaysPerWeek", count(*)::int FROM "AspNetUsers" GROUP BY 1 ORDER BY 1"""),
            Db.Command(features.Fireside ? """SELECT (count(*) / 2)::int FROM "FiresideLinks" """ : "SELECT NULL::int"),
        ];
        await using var reader = await Db.RunAsync(connection, commands, ct);

        await reader.RowAsync(ct);
        var (users, showStreaks, firesideOn) = (reader.GetInt32(0), reader.GetInt32(1), Nullable(reader, 2));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var (active7, active30) = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var habits = new HabitCounts(reader.GetInt32(0), Nullable(reader, 1), reader.GetInt32(2), reader.GetInt32(3));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var routines = new RoutineCounts(reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);

        var grace = new List<GraceCount>();
        while (await reader.ReadAsync(ct)) grace.Add(new(reader.GetInt32(0), reader.GetInt32(1)));
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        var friendships = Nullable(reader, 0);

        return new(generatedAt, users, active7, active30, habits, routines, new(users, showStreaks, grace), firesideOn, friendships);
    }

    public static async Task<OverviewSeries> SeriesAsync(NpgsqlDataSource ember, EmberFeatures features, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var from = today.AddDays(-(SeriesDays - 1));
        var weeks = OpsCalendar.Weeks(today, SeriesWeeks).ToList();
        var warmthFrom = today.AddDays(-(WarmthDays - 1));

        await using var connection = await ember.OpenConnectionAsync(ct);
        List<NpgsqlBatchCommand> commands =
        [
            Db.Command("""
                SELECT first, count(*)::int FROM (
                    SELECT (min("CreatedAt") AT TIME ZONE 'Europe/Berlin')::date AS first FROM "Habits" GROUP BY "UserId") f
                WHERE first BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today),
            Db.Command("""
                SELECT l."Date", count(*)::int, count(DISTINCT h."UserId")::int FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                WHERE l."Completed" AND l."Date" BETWEEN $1 AND $2 GROUP BY 1
                """, from, today),
            Db.Command("""SELECT "Date", count(*)::int FROM "DayNotes" WHERE "Date" BETWEEN $1 AND $2 GROUP BY 1""", from, today),
            Db.Command("""SELECT "Date", count(*)::int FROM "DayMarks" WHERE "Kind" = 0 AND "Date" BETWEEN $1 AND $2 GROUP BY 1""", from, today),
            Db.Command("""
                SELECT count(*)::int FROM (SELECT min("CreatedAt") AS first FROM "Habits" GROUP BY "UserId") f
                WHERE first < ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin'
                """, weeks[0]),
            Db.Command("""
                WITH a AS (
                    SELECT DISTINCT h."UserId", l."Date" FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                    WHERE l."Completed" AND l."Date" BETWEEN $1::date - 6 AND $2::date)
                SELECT d::date, count(DISTINCT a."UserId")::int
                FROM generate_series($1::timestamp, $2::timestamp, interval '1 day') d
                JOIN a ON a."Date" BETWEEN d::date - 6 AND d::date
                GROUP BY 1
                """, today.AddDays(-(TrendDays - 1)), today),
            Db.Command("""
                SELECT count(DISTINCT h."UserId") FILTER (WHERE l."Date" >= $1::date - 13)::int, count(DISTINCT h."UserId") FILTER (WHERE l."Date" <= $1::date - 30)::int
                FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                WHERE l."Completed" AND l."Date" BETWEEN $1::date - 59 AND $1::date - 7
                """, today),
        ];
        if (features.Fireside)
            commands.Add(Db.Command("""
                SELECT ("SentAtUtc" AT TIME ZONE 'Europe/Berlin')::date, count(*)::int FROM "Warmth"
                WHERE "SentAtUtc" >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin' AND "SentAtUtc" < (($2::date + 1)::timestamp AT TIME ZONE 'Europe/Berlin') GROUP BY 1
                """, warmthFrom, today));
        if (features.HabitSlips)
            commands.Add(Db.Command("""
                SELECT "Date", sum("Count")::int, count(*)::int FROM "HabitSlips" WHERE "Date" BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today));
        await using var reader = await Db.RunAsync(connection, commands, ct);

        var firstHabit = await Db.DaysAsync(reader, ct);
        var newPerWeek = weeks.Select(w => new WeekCount(w, firstHabit.Where(d => OpsCalendar.Monday(d.Key) == w).Sum(d => d.Value))).ToList();
        var (checkInRows, checkInUsers) = (new Dictionary<DateOnly, int>(), new Dictionary<DateOnly, int>());
        while (await reader.ReadAsync(ct))
        {
            var day = reader.GetFieldValue<DateOnly>(0);
            (checkInRows[day], checkInUsers[day]) = (reader.GetInt32(1), reader.GetInt32(2));
        }
        await reader.NextResultAsync(ct);
        var checkIns = OpsCalendar.Days(from, today, checkInRows);
        var journal = OpsCalendar.Days(from, today, await Db.DaysAsync(reader, ct));
        var lowDays = OpsCalendar.Days(from, today, await Db.DaysAsync(reader, ct));
        await reader.RowAsync(ct);
        var usersBefore = reader.GetInt32(0);
        await reader.NextResultAsync(ct);
        var active7PerDay = OpsCalendar.Days(today.AddDays(-(TrendDays - 1)), today, await Db.DaysAsync(reader, ct));
        await reader.RowAsync(ct);
        var (active7Before, active30Before) = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);
        var warmth = features.Fireside ? OpsCalendar.Days(warmthFrom, today, await Db.DaysAsync(reader, ct)) : null;

        List<SlipWeek>? slips = null;
        if (features.HabitSlips)
        {
            var rows = new List<(DateOnly Date, int Slips, int Days)>();
            while (await reader.ReadAsync(ct)) rows.Add((reader.GetFieldValue<DateOnly>(0), reader.GetInt32(1), reader.GetInt32(2)));
            slips = weeks.Select(w =>
            {
                var inWeek = rows.Where(r => OpsCalendar.Monday(r.Date) == w).ToList();
                return new SlipWeek(w, inWeek.Sum(r => r.Slips), inWeek.Sum(r => r.Days));
            }).ToList();
        }

        return new(generatedAt, newPerWeek, checkIns, journal, lowDays, warmth, slips, usersBefore, active7PerDay, active7Before, active30Before,
            OpsCalendar.Days(from, today, checkInUsers));
    }

    private static int? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
