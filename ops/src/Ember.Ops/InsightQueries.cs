using Npgsql;

namespace Ember.Ops;

/// <summary>A count with the distinct users behind it, so small buckets can be suppressed.</summary>
public sealed record UserBucket(string Label, int Count, int Users);

/// <summary>A cohort by week of first habit and how many of it completed a check-in 1 and 4 weeks later.</summary>
public sealed record Cohort(DateOnly Week, int Users, int Week1, int Week4);

/// <summary>Check-in, journal and low-day activity. Days and weeks are Europe/Berlin.</summary>
/// <param name="ActivePerDay">Users with a completed check-in per day, 90 days.</param>
/// <param name="ActivePerWeek">Users with a completed check-in per week, 12 weeks.</param>
/// <param name="ActiveDays">Users by days with a completed check-in in the last 30 days.</param>
/// <param name="JournalPerWeek">Journal days and their writers per week, 12 weeks.</param>
/// <param name="LowDaysPerWeek">Low days and the users who marked them per week, 12 weeks.</param>
public sealed record ActivityInsights(
    DateTime GeneratedAt, IReadOnlyList<DayCount> ActivePerDay, IReadOnlyList<UserBucket> ActivePerWeek, IReadOnlyList<UserBucket> ActiveDays,
    IReadOnlyList<Cohort> Cohorts, IReadOnlyList<UserBucket> JournalPerWeek, int Journal30, int Writers30, int PrivateJournals,
    IReadOnlyList<UserBucket> LowDaysPerWeek);

/// <summary>Habit and routine setup.</summary>
public sealed record HabitInsights(
    DateTime GeneratedAt, int Habits, int UsersWithHabits, int InRoutine, int WithReminderTime, int Routines,
    IReadOnlyList<(string Label, int Count)> BySchedule, IReadOnlyList<(string Label, int Count)> ByTimesPerDay,
    IReadOnlyList<(int Hour, int Habits, int Routines)> ReminderHours, IReadOnlyList<(string Label, int Count)> UsersByHabits,
    IReadOnlyList<(DateOnly Week, int Habits, int Routines)> CreatedPerWeek);

/// <summary>Sign-in, reminder destinations and design choice of the users.</summary>
public sealed record UserInsights(
    DateTime GeneratedAt, int Users, int DiscordLinked, int DiscordDms, int WebPush, int Phone, int NoDestination,
    IReadOnlyList<(string Label, int Count)> ByTheme);

/// <summary>Aggregate reads for the activity, habits and users pages. Counts only; no per-user data leaves the database.</summary>
public static class InsightQueries
{
    public const int Days = 90;
    public const int Weeks = 12;

    private static readonly (int Min, string Label)[] ActiveDayBuckets = [(1, "1 day"), (2, "2–4 days"), (5, "5–9 days"), (10, "10–19 days"), (20, "20–30 days")];
    private static readonly (int Min, string Label)[] HabitBuckets = [(0, "0"), (1, "1"), (2, "2–3"), (4, "4–6"), (7, "7+")];
    private static readonly string[] Schedules = ["Daily", "Times per week", "Set weekdays"];

    public static async Task<ActivityInsights> ActivityAsync(NpgsqlDataSource ember, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var from = today.AddDays(-(Days - 1));
        var weeks = OpsCalendar.Weeks(today, Weeks).ToList();

        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT l."Date", count(DISTINCT h."UserId")::int FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                WHERE l."Completed" AND l."Date" BETWEEN $1 AND $2 GROUP BY 1
                """, from, today),
            Db.Command("""
                SELECT date_trunc('week', l."Date"::timestamp)::date, count(DISTINCT h."UserId")::int FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                WHERE l."Completed" AND l."Date" BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today),
            Db.Command("""
                SELECT n, count(*)::int FROM (
                    SELECT count(DISTINCT l."Date")::int AS n FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                    WHERE l."Completed" AND l."Date" BETWEEN $1::date - 29 AND $1::date GROUP BY h."UserId") u
                GROUP BY 1
                """, today),
            Db.Command("""
                WITH first AS (
                    SELECT "UserId", date_trunc('week', min("CreatedAt") AT TIME ZONE 'Europe/Berlin')::date AS week FROM "Habits" GROUP BY 1),
                active AS (
                    SELECT DISTINCT h."UserId", date_trunc('week', l."Date"::timestamp)::date AS week FROM "HabitLogs" l JOIN "Habits" h ON h."Id" = l."HabitId"
                    WHERE l."Completed" AND l."Date" >= $1::date + 7)
                SELECT f.week, count(*)::int, count(a1."UserId")::int, count(a4."UserId")::int
                FROM first f
                LEFT JOIN active a1 ON a1."UserId" = f."UserId" AND a1.week = f.week + 7
                LEFT JOIN active a4 ON a4."UserId" = f."UserId" AND a4.week = f.week + 28
                WHERE f.week >= $1 GROUP BY 1
                """, weeks[0]),
            Db.Command("""
                SELECT date_trunc('week', "Date"::timestamp)::date, count(*)::int, count(DISTINCT "UserId")::int FROM "DayNotes"
                WHERE "Date" BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today),
            Db.Command("""SELECT count(*)::int, count(DISTINCT "UserId")::int FROM "DayNotes" WHERE "Date" BETWEEN $1::date - 29 AND $1::date""", today),
            Db.Command("""SELECT count(*)::int FROM "JournalKeys" """),
            Db.Command("""
                SELECT date_trunc('week', "Date"::timestamp)::date, count(*)::int, count(DISTINCT "UserId")::int FROM "DayMarks"
                WHERE "Kind" = 0 AND "Date" BETWEEN $1 AND $2 GROUP BY 1
                """, weeks[0], today),
        ], ct);

        var perDay = OpsCalendar.Days(from, today, await Db.DaysAsync(reader, ct));
        var perWeekRows = await Db.DaysAsync(reader, ct);
        var perWeek = weeks.Select(w => new UserBucket(Date(w), perWeekRows.GetValueOrDefault(w), perWeekRows.GetValueOrDefault(w))).ToList();

        var daysRows = await Db.CountsAsync(reader, ct);
        var activeDays = ActiveDayBuckets.Select((b, i) =>
        {
            var max = i + 1 < ActiveDayBuckets.Length ? ActiveDayBuckets[i + 1].Min : int.MaxValue;
            var users = daysRows.Where(r => r.Key >= b.Min && r.Key < max).Sum(r => r.Value);
            return new UserBucket(b.Label, users, users);
        }).ToList();

        var cohortRows = new Dictionary<DateOnly, Cohort>();
        while (await reader.ReadAsync(ct))
        {
            var week = reader.GetFieldValue<DateOnly>(0);
            cohortRows[week] = new(week, reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        }
        await reader.NextResultAsync(ct);
        var cohorts = weeks.Select(w => cohortRows.GetValueOrDefault(w) ?? new(w, 0, 0, 0)).ToList();

        var journal = await WeekBucketsAsync(reader, weeks, ct);
        await reader.RowAsync(ct);
        var (journal30, writers30) = (reader.GetInt32(0), reader.GetInt32(1));
        await reader.NextResultAsync(ct);
        await reader.RowAsync(ct);
        var privateJournals = reader.GetInt32(0);
        await reader.NextResultAsync(ct);
        var lowDays = await WeekBucketsAsync(reader, weeks, ct);

        return new(generatedAt, perDay, perWeek, activeDays, cohorts, journal, journal30, writers30, privateJournals, lowDays);
    }

    public static async Task<HabitInsights> HabitsAsync(NpgsqlDataSource ember, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var weeks = OpsCalendar.Weeks(today, Weeks).ToList();

        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT count(*)::int, count(DISTINCT "UserId")::int, count(*) FILTER (WHERE "RoutineId" IS NOT NULL)::int,
                       count(*) FILTER (WHERE cardinality("ReminderTimes") > 0)::int, (SELECT count(*) FROM "Routines")::int
                FROM "Habits"
                """),
            Db.Command("""SELECT "FrequencyType", count(*)::int FROM "Habits" GROUP BY 1"""),
            Db.Command("""SELECT least("TimesPerDay", 4), count(*)::int FROM "Habits" GROUP BY 1"""),
            Db.Command("""SELECT extract(hour FROM t)::int, count(*)::int FROM "Habits", unnest("ReminderTimes") t GROUP BY 1"""),
            Db.Command("""SELECT extract(hour FROM "ReminderTime")::int, count(*)::int FROM "Routines" WHERE "ReminderTime" IS NOT NULL GROUP BY 1"""),
            Db.Command("""
                SELECT coalesce(n, 0), count(*)::int FROM "AspNetUsers" u
                LEFT JOIN (SELECT "UserId", count(*)::int AS n FROM "Habits" GROUP BY 1) h ON h."UserId" = u."Id"
                GROUP BY 1
                """),
            Db.Command("""
                SELECT date_trunc('week', "CreatedAt" AT TIME ZONE 'Europe/Berlin')::date, count(*)::int FROM "Habits"
                WHERE "CreatedAt" >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin' GROUP BY 1
                """, weeks[0]),
            Db.Command("""
                SELECT date_trunc('week', "CreatedAt" AT TIME ZONE 'Europe/Berlin')::date, count(*)::int FROM "Routines"
                WHERE "CreatedAt" >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin' GROUP BY 1
                """, weeks[0]),
        ], ct);

        await reader.RowAsync(ct);
        var (habits, users, inRoutine, withTime, routines) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
        await reader.NextResultAsync(ct);
        var schedule = await Db.CountsAsync(reader, ct);
        var perDay = await Db.CountsAsync(reader, ct);
        var habitHours = await Db.CountsAsync(reader, ct);
        var routineHours = await Db.CountsAsync(reader, ct);
        var byHabits = await Db.CountsAsync(reader, ct);
        var habitWeeks = await Db.DaysAsync(reader, ct);
        var routineWeeks = await Db.DaysAsync(reader, ct);

        return new(generatedAt, habits, users, inRoutine, withTime, routines,
            Schedules.Select((label, kind) => (label, schedule.GetValueOrDefault(kind))).ToList(),
            Enumerable.Range(1, 4).Select(n => (n == 4 ? "4+" : n.ToString(System.Globalization.CultureInfo.InvariantCulture), perDay.GetValueOrDefault(n))).ToList(),
            Enumerable.Range(0, 24).Select(h => (h, habitHours.GetValueOrDefault(h), routineHours.GetValueOrDefault(h))).ToList(),
            HabitBuckets.Select((b, i) =>
            {
                var max = i + 1 < HabitBuckets.Length ? HabitBuckets[i + 1].Min : int.MaxValue;
                return (b.Label, byHabits.Where(r => r.Key >= b.Min && r.Key < max).Sum(r => r.Value));
            }).ToList(),
            weeks.Select(w => (w, habitWeeks.GetValueOrDefault(w), routineWeeks.GetValueOrDefault(w))).ToList());
    }

    public static async Task<UserInsights> UsersAsync(NpgsqlDataSource ember, TimeProvider time, CancellationToken ct)
    {
        var generatedAt = time.GetUtcNow().UtcDateTime;
        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT count(*)::int, count(*) FILTER (WHERE d.discord)::int, count(*) FILTER (WHERE d.push)::int, count(*) FILTER (WHERE d.phone)::int,
                       count(*) FILTER (WHERE NOT (d.discord OR d.push OR d.phone))::int,
                       (SELECT count(DISTINCT "UserId") FROM "AspNetUserLogins" WHERE "LoginProvider" = 'Discord')::int
                FROM (
                    SELECT u."DiscordReminders" IS TRUE AS discord,
                           EXISTS (SELECT FROM "PushSubscriptions" s WHERE s."UserId" = u."Id" AND s."Enabled") AS push,
                           EXISTS (SELECT FROM "ReminderPhones" p WHERE p."UserId" = u."Id" AND p."Enabled") AS phone
                    FROM "AspNetUsers" u) d
                """),
            Db.Command("""SELECT coalesce("Theme", ''), count(*)::int FROM "AspNetUsers" GROUP BY 1"""),
        ], ct);

        await reader.RowAsync(ct);
        var (users, discord, push, phone, none, linked) =
            (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5));
        await reader.NextResultAsync(ct);
        var themes = new Dictionary<string, int>();
        while (await reader.ReadAsync(ct)) themes[reader.GetString(0)] = reader.GetInt32(1);

        var known = new[] { "hearthlight", "fieldnotes", "tidepool", "marigold" };
        var byTheme = known.Select(t => (t, themes.GetValueOrDefault(t))).ToList();
        byTheme.Add(("default", themes.Where(t => !known.Contains(t.Key)).Sum(t => t.Value)));
        return new(generatedAt, users, linked, discord, push, phone, none, byTheme);
    }

    private static async Task<List<UserBucket>> WeekBucketsAsync(NpgsqlDataReader reader, List<DateOnly> weeks, CancellationToken ct)
    {
        var rows = new Dictionary<DateOnly, (int Count, int Users)>();
        while (await reader.ReadAsync(ct)) rows[reader.GetFieldValue<DateOnly>(0)] = (reader.GetInt32(1), reader.GetInt32(2));
        await reader.NextResultAsync(ct);
        return weeks.Select(w => new UserBucket(Date(w), rows.GetValueOrDefault(w).Count, rows.GetValueOrDefault(w).Users)).ToList();
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
