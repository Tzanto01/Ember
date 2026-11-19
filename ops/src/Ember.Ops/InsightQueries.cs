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
/// <param name="Retained">The retention snapshots of the sign-up weeks of the last 12 weeks.</param>
public sealed record ActivityInsights(
    DateTime GeneratedAt, DateOnly? Since, IReadOnlyList<DayCount> PerDay, IReadOnlyList<UserBucket> PerWeek, IReadOnlyList<RetainedCount> Retained);

/// <summary>Users of the sign-up week <paramref name="Cohort"/> seen in the later week <paramref name="Week"/>, from the dashboard's snapshots.</summary>
public sealed record RetainedCount(DateOnly Cohort, DateOnly Week, int Users);

/// <summary>
/// The devices of the users: sign-in platform and app version of the users seen in the last 7 days (null when Ember does not record them),
/// and adoption counts.
/// </summary>
public sealed record DeviceInsights(
    DateTime GeneratedAt, int Users, int WebPush, int AndroidInstalls, int DiscordLinked,
    IReadOnlyList<UserBucket>? Platforms, IReadOnlyList<UserBucket>? Versions);

/// <summary>Aggregate reads for the engagement and devices pages. Counts only; no per-user data leaves the database.</summary>
public static class InsightQueries
{
    public const int Days = 90;
    public const int Weeks = 12;

    public const int DeviceDays = 7;

    /// <summary>The weeks after sign-up whose activity is kept for retention.</summary>
    public static readonly int[] RetentionWeeks = [1, 2, 4];

    /// <summary>
    /// Stores the distinct users seen today, in the 7 days up to today and this week, and of the users who signed up
    /// <see cref="RetentionWeeks"/> weeks before this one those seen this week, keeping the highest value of each day and week,
    /// since a session's last-seen time is overwritten and a deleted session drops out. Counts only, never user ids.
    /// </summary>
    public static async Task SnapshotActivityAsync(NpgsqlDataSource ember, NpgsqlDataSource ops, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var monday = OpsCalendar.Monday(today);
        var cohorts = RetentionWeeks.Select(w => monday.AddDays(-7 * w)).ToList();
        var features = await EmberFeatures.DetectAsync(ember, ct);
        int day, active7, week;
        Dictionary<DateOnly, int> retained;
        await using (var connection = await ember.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
        [
            Db.Command("""
                SELECT count(DISTINCT "UserId") FILTER (WHERE seen >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin')::int,
                       count(DISTINCT "UserId") FILTER (WHERE seen >= ($1::date - 6)::timestamp AT TIME ZONE 'Europe/Berlin')::int,
                       count(DISTINCT "UserId") FILTER (WHERE seen >= ($2::date)::timestamp AT TIME ZONE 'Europe/Berlin')::int
                FROM (SELECT "UserId", coalesce("LastSeenUtc", "CreatedUtc") AS seen FROM "AuthSessions") s
                """, today, monday),
            Db.Command($"""
                SELECT date_trunc('week', created AT TIME ZONE 'Europe/Berlin')::date, count(*)::int FROM ({features.Signups}) u
                WHERE created >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin' AND created < ($2::date)::timestamp AT TIME ZONE 'Europe/Berlin'
                  AND EXISTS (SELECT FROM "AuthSessions" s WHERE s."UserId" = u.user_id
                              AND coalesce(s."LastSeenUtc", s."CreatedUtc") >= ($2::date)::timestamp AT TIME ZONE 'Europe/Berlin')
                GROUP BY 1
                """, cohorts.Min(), monday),
        ], ct))
        {
            await reader.RowAsync(ct);
            (day, active7, week) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
            await reader.NextResultAsync(ct);
            retained = await Db.DaysAsync(reader, ct);
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
            ..cohorts.Select(cohort => Db.Command("""
                INSERT INTO activity_retention (cohort, week, users) VALUES ($1, $2, $3)
                ON CONFLICT (cohort, week) DO UPDATE SET users = greatest(activity_retention.users, excluded.users)
                """, cohort, monday, retained.GetValueOrDefault(cohort))),
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
            Db.Command("SELECT cohort, week, users FROM activity_retention WHERE cohort >= $1 ORDER BY cohort, week", weeks[0]),
        ], ct);
        await reader.RowAsync(ct);
        DateOnly? since = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateOnly>(0);
        await reader.NextResultAsync(ct);
        var dayRows = await Db.DaysAsync(reader, ct);
        var weekRows = await Db.DaysAsync(reader, ct);
        var retained = new List<RetainedCount>();
        while (await reader.ReadAsync(ct)) retained.Add(new(reader.GetFieldValue<DateOnly>(0), reader.GetFieldValue<DateOnly>(1), reader.GetInt32(2)));
        if (since is not { } first) return new(generatedAt, null, [], [], []);

        return new(generatedAt, first, dayRows.OrderBy(d => d.Key).Select(d => new DayCount(d.Key, d.Value)).ToList(),
            weeks.Where(w => w >= OpsCalendar.Monday(first)).Select(w => new UserBucket(Date(w), weekRows.GetValueOrDefault(w), weekRows.GetValueOrDefault(w))).ToList(),
            retained);
    }

    /// <summary>Platforms and app versions of the users seen in the last <see cref="DeviceDays"/> days, and adoption counts.</summary>
    public static async Task<DeviceInsights> DevicesAsync(NpgsqlDataSource ember, EmberFeatures features, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        const string recent = """
            FROM "AuthSessions" WHERE coalesce("LastSeenUtc", "CreatedUtc") >= ($1::date - 6)::timestamp AT TIME ZONE 'Europe/Berlin'
            """;
        List<NpgsqlBatchCommand> commands =
        [
            Db.Command("""
                SELECT (SELECT count(*) FROM "AspNetUsers")::int,
                       (SELECT count(DISTINCT "UserId") FROM "PushSubscriptions" WHERE "Enabled")::int,
                       (SELECT count(*) FROM "ReminderPhones")::int,
                       (SELECT count(DISTINCT "UserId") FROM "AspNetUserLogins" WHERE "LoginProvider" = 'Discord')::int
                """),
        ];
        if (features.SessionPlatform)
            commands.Add(Db.Command($"""
                SELECT CASE lower("Platform") WHEN 'web' THEN 'Web' WHEN 'android' THEN 'Android' WHEN 'discord' THEN 'Discord bot'
                       ELSE CASE WHEN "Platform" IS NULL THEN 'Not recorded' ELSE 'Other' END END, count(DISTINCT "UserId")::int
                {recent} GROUP BY 1 ORDER BY 2 DESC, 1
                """, today));
        if (features.SessionAppVersion)
            commands.Add(Db.Command($"""
                SELECT coalesce(left("AppVersion", 40), 'Not recorded'), count(DISTINCT "UserId")::int
                {recent} GROUP BY 1 ORDER BY 2 DESC, 1 DESC
                """, today));

        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection, commands, ct);
        await reader.RowAsync(ct);
        var (users, push, phones, discord) = (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
        await reader.NextResultAsync(ct);

        async Task<IReadOnlyList<UserBucket>> BucketsAsync()
        {
            var buckets = new List<UserBucket>();
            while (await reader.ReadAsync(ct)) buckets.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(1)));
            await reader.NextResultAsync(ct);
            return buckets;
        }

        var platforms = features.SessionPlatform ? await BucketsAsync() : null;
        var versions = features.SessionAppVersion ? await BucketsAsync() : null;
        return new(generatedAt, users, push, phones, discord, platforms, versions);
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
