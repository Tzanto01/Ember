using Npgsql;

namespace Ember.Ops;

public sealed record OverviewUsers(int Total, int Active7, int Active14, IReadOnlyList<WeekCount> NewPerWeek);
public sealed record WeekCount(DateOnly Week, int Count);

/// <summary>The cheap counters of the overview, refreshed often.</summary>
public sealed record OverviewCounters(DateTime GeneratedAt, int Users, int Active7, int Active14);

/// <summary>The 12-week series of the overview and the active-user history, refreshed rarely.</summary>
/// <param name="NewPerWeek">New users per week by <see cref="EmberFeatures.Signups"/>.</param>
/// <param name="UsersBefore">Users who signed up before the first of the 12 weeks.</param>
/// <param name="Active7PerDay">Users seen in the 7 days up to each of the last 14 days, from the dashboard's snapshots; days without one are left out.</param>
/// <param name="Active7Before">Users seen in the 7 days up to 7 days ago, or null without a snapshot that day.</param>
/// <param name="SignupNote">How new users are dated (<see cref="EmberFeatures.SignupNote"/>).</param>
public sealed record OverviewSeries(
    DateTime GeneratedAt, IReadOnlyList<WeekCount> NewPerWeek, int UsersBefore, IReadOnlyList<DayCount> Active7PerDay, int? Active7Before, string SignupNote);

/// <summary>The overview stats; <see cref="GeneratedAt"/> is when the counters were read.</summary>
public sealed record Overview(DateTime GeneratedAt, OverviewUsers Users, OverviewSeries Series)
{
    public static Overview From(OverviewCounters counters, OverviewSeries series) =>
        new(counters.GeneratedAt, new(counters.Users, counters.Active7, counters.Active14, series.NewPerWeek), series);
}

/// <summary>Aggregate reads for the overview page. Counts only; no per-user data leaves the database.</summary>
public static class OverviewQueries
{
    public const int SeriesWeeks = 12;
    public const int TrendDays = 14;

    public static async Task<OverviewCounters> CountersAsync(NpgsqlDataSource ember, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;

        await using var connection = await ember.OpenConnectionAsync(ct);
        await using var reader = await Db.RunAsync(connection,
        [
            Db.Command("""SELECT count(*)::int FROM "AspNetUsers" """),
            Db.Command("""
                SELECT count(DISTINCT "UserId") FILTER (WHERE seen >= (($1::date - 6)::timestamp AT TIME ZONE 'Europe/Berlin'))::int, count(DISTINCT "UserId")::int
                FROM (SELECT "UserId", coalesce("LastSeenUtc", "CreatedUtc") AS seen FROM "AuthSessions") s
                WHERE seen >= (($1::date - 13)::timestamp AT TIME ZONE 'Europe/Berlin')
                """, today),
        ], ct);

        await reader.RowAsync(ct);
        var users = reader.GetInt32(0);
        await reader.NextResultAsync(ct);

        await reader.RowAsync(ct);
        return new(generatedAt, users, reader.GetInt32(0), reader.GetInt32(1));
    }

    public static async Task<OverviewSeries> SeriesAsync(NpgsqlDataSource ember, NpgsqlDataSource ops, EmberFeatures features, TimeProvider time, CancellationToken ct)
    {
        var today = OpsCalendar.Today(time);
        var generatedAt = time.GetUtcNow().UtcDateTime;
        var weeks = OpsCalendar.Weeks(today, SeriesWeeks).ToList();

        Dictionary<DateOnly, int> signups;
        int usersBefore;
        await using (var connection = await ember.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
        [
            Db.Command($"""
                SELECT (created AT TIME ZONE 'Europe/Berlin')::date, count(*)::int FROM ({features.Signups}) f
                WHERE created >= ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin' AND created < ($2::date + 1)::timestamp AT TIME ZONE 'Europe/Berlin' GROUP BY 1
                """, weeks[0], today),
            Db.Command($"""
                SELECT count(*)::int FROM ({features.Signups}) f
                WHERE created < ($1::date)::timestamp AT TIME ZONE 'Europe/Berlin'
                """, weeks[0]),
        ], ct))
        {
            signups = await Db.DaysAsync(reader, ct);
            await reader.RowAsync(ct);
            usersBefore = reader.GetInt32(0);
        }

        Dictionary<DateOnly, int> active7;
        await using (var connection = await ops.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
            [Db.Command("SELECT day, active7 FROM activity_days WHERE day BETWEEN $1 AND $2", today.AddDays(-(TrendDays - 1)), today)], ct))
            active7 = await Db.DaysAsync(reader, ct);

        var newPerWeek = weeks.Select(w => new WeekCount(w, signups.Where(d => OpsCalendar.Monday(d.Key) == w).Sum(d => d.Value))).ToList();
        return new(generatedAt, newPerWeek, usersBefore, active7.OrderBy(d => d.Key).Select(d => new DayCount(d.Key, d.Value)).ToList(),
            active7.TryGetValue(today.AddDays(-7), out var before) ? before : null, features.SignupNote);
    }
}
