using Npgsql;

namespace Ember.Ops;

/// <summary>Which of Ember's newer columns this database has and the dashboard role may read.</summary>
public sealed record EmberFeatures(bool UserCreated, bool SessionPlatform, bool SessionAppVersion)
{
    /// <summary>Reads the columns visible to the current role, so an ungranted column counts as missing.</summary>
    public static async Task<EmberFeatures> DetectAsync(NpgsqlDataSource ember, CancellationToken ct)
    {
        await using var command = ember.CreateCommand("""
            SELECT table_name || '.' || column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND (table_name, column_name) IN (('AspNetUsers', 'CreatedUtc'), ('AuthSessions', 'Platform'), ('AuthSessions', 'AppVersion'))
            """);
        var found = new HashSet<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) found.Add(reader.GetString(0));
        return new(found.Contains("AspNetUsers.CreatedUtc"), found.Contains("AuthSessions.Platform"), found.Contains("AuthSessions.AppVersion"));
    }

    /// <summary>
    /// One row per user (<c>user_id</c>, <c>created</c>): account creation when Ember records it (unknown ones as <c>-infinity</c>),
    /// else the user's first habit as a proxy, which leaves out users without a habit.
    /// </summary>
    public string Signups => UserCreated
        ? """SELECT "Id" AS user_id, coalesce("CreatedUtc", '-infinity') AS created FROM "AspNetUsers" """
        : """SELECT "UserId" AS user_id, min("CreatedAt") AS created FROM "Habits" GROUP BY 1""";

    /// <summary>The note for counts by <see cref="Signups"/>.</summary>
    public string SignupNote => UserCreated ? "by account creation" : "by first habit, a proxy until Ember records account creation";
}

/// <summary>Calendar helpers shared by the sections.</summary>
public static class OpsCalendar
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    /// <summary>Today in Europe/Berlin, the operator's day.</summary>
    public static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Berlin).DateTime);

    /// <summary>The Europe/Berlin calendar date of a UTC instant.</summary>
    public static DateOnly BerlinDate(DateTime utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Berlin));

    /// <summary>The Monday of the week containing <paramref name="day"/>.</summary>
    public static DateOnly Monday(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>Every date from <paramref name="from"/> to <paramref name="to"/> with its count, 0 where missing.</summary>
    public static List<DayCount> Days(DateOnly from, DateOnly to, IReadOnlyDictionary<DateOnly, int> counts)
    {
        var days = new List<DayCount>();
        for (var day = from; day <= to; day = day.AddDays(1)) days.Add(new(day, counts.GetValueOrDefault(day)));
        return days;
    }

    /// <summary>The Mondays of the last <paramref name="weeks"/> weeks up to the week of <paramref name="today"/>, oldest first.</summary>
    public static IEnumerable<DateOnly> Weeks(DateOnly today, int weeks)
    {
        var last = Monday(today);
        for (var i = weeks - 1; i >= 0; i--) yield return last.AddDays(-7 * i);
    }
}

public sealed record DayCount(DateOnly Date, int Count);
