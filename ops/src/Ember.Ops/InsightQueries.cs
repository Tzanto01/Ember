using Npgsql;

namespace Ember.Ops;

/// <summary>A count with the distinct users behind it, so small buckets can be suppressed.</summary>
public sealed record UserBucket(string Label, int Count, int Users);

/// <summary>
/// Sign-in activity from the dashboard's own snapshots of <c>AuthSessions</c>. Days and weeks are Europe/Berlin.
/// Empty until the first snapshot.
/// </summary>
/// <param name="Since">The first day with a snapshot.</param>
/// <param name="PerDay">Users seen per day over the last 90 days, oldest first; days without a snapshot are left out.</param>
/// <param name="PerWeek">Users seen per week, from the week of <see cref="Since"/> or 12 weeks back, whichever is later.</param>
public sealed record ActivityInsights(DateTime GeneratedAt, DateOnly? Since, IReadOnlyList<DayCount> PerDay, IReadOnlyList<UserBucket> PerWeek);

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

    private static readonly (int Min, string Label)[] HabitBuckets = [(0, "0"), (1, "1"), (2, "2–3"), (4, "4–6"), (7, "7+")];
    private static readonly string[] Schedules = ["Daily", "Times per week", "Set weekdays"];

    /// <summary>
    /// Stores the distinct users seen today, in the 7 days up to today and this week, keeping the highest value of each day and week,
    /// since a session's last-seen time is overwritten and a deleted session drops out. Counts only, never user ids.
    /// </summary>
    public static async Task SnapshotActivityAsync(NpgsqlDataSource ember, NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var monday = OpsCalendar.Monday(today);
        int day, active7, week;
        await using (var connection = await ember.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT count(DISTINCT "UserId") FILTER (WHERE seen >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin')::int,
                       count(DISTINCT "UserId") FILTER (WHERE seen >= ($1::date - 6)::timestamp AT TIME ZONE 'Europe/Berlin')::int,
                       count(DISTINCT "UserId") FILTER (WHERE seen >= ($2::date)::timestamp AT TIME ZONE 'Europe/Berlin')::int
                FROM (SELECT "UserId", coalesce("LastSeenUtc", "CreatedUtc") AS seen FROM "AuthSessions") s
                """, today, monday),
        ], ct))
        {
            await reader.RowAsync(ct);
            (day, active7, week) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }

        await using var opsConnection = await ops.OpenConnectionAsync(ct);
        await using var upsert = await Db.RunAsync(opsConnection,
        [
            Db.Command("""
                INSERT INTO activity_days (day, users, active7) VALUES ($1, $2, $3)
                ON CONFLICT (day) DO UPDATE SET users = greatest(activity_days.users, excluded.users), active7 = greatest(activity_days.active7, excluded.active7)
                """, today, day, active7),
            Db.Command("""
                INSERT INTO activity_weeks (week, users) VALUES ($1, $2)
                ON CONFLICT (week) DO UPDATE SET users = greatest(activity_weeks.users, excluded.users)
                """, monday, week),
        ], ct);
    }

    /// <summary>Users seen per day and per week, from the snapshots in emberops.</summary>
    public static async Task<ActivityInsights> ActivityAsync(NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var weeks = OpsCalendar.Weeks(today, Weeks).ToList();

        await using var connection = await ops.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("SELECT min(day) FROM activity_days"),
            Db.Command("SELECT day, users FROM activity_days WHERE day BETWEEN $1 AND $2", today.AddDays(-(Days - 1)), today),
            Db.Command("SELECT week, users FROM activity_weeks WHERE week >= $1", weeks[0]),
        ], ct);
        await reader.RowAsync(ct);
        DateOnly? since = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateOnly>(0);
        await reader.NextResultAsync(ct);
        var dayRows = await Db.DaysAsync(reader, ct);
        var weekRows = await Db.DaysAsync(reader, ct);
        if (since is not { } first) return new(generatedAt, null, [], []);

        return new(generatedAt, first, dayRows.OrderBy(d => d.Key).Select(d => new DayCount(d.Key, d.Value)).ToList(),
            weeks.Where(w => w >= OpsCalendar.Monday(first)).Select(w => new UserBucket(Date(w), weekRows.GetValueOrDefault(w), weekRows.GetValueOrDefault(w))).ToList());
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

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
