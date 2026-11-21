using System.Globalization;

namespace Ember.Api.Hosting;

/// <summary>
/// The contract's wire formats: local dates as <c>yyyy-MM-dd</c>, times of day as <c>HH:mm</c>; and the <c>from</c>/<c>to</c> range
/// of the per-day reads (<see cref="DayRange"/>).
/// </summary>
internal static class Formats
{
    public static bool TryParseDate(string? value, out DateOnly date)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    public static bool TryParseTime(string? value, out TimeOnly time)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    public static string Time(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The parsed <paramref name="value"/>, or <paramref name="fallback"/> when it is null; a malformed value adds an error for <paramref name="field"/>.</summary>
    public static DateOnly DateOr(string? value, DateOnly fallback, string field, Dictionary<string, string[]> errors)
    {
        if (value is null) return fallback;
        if (TryParseDate(value, out var date)) return date;
        errors[field] = [DateMessage];
        return fallback;
    }

    /// <summary><paramref name="date"/> minus <paramref name="days"/>, never earlier than <see cref="DateOnly.MinValue"/>.</summary>
    public static DateOnly DaysBefore(DateOnly date, int days)
        => date.DayNumber < days ? DateOnly.MinValue : date.AddDays(-days);

    public const string DateMessage = "Must be a date as yyyy-MM-dd.";

    /// <summary>The most days a <see cref="DayRange"/> may span.</summary>
    public const int MaxRangeDays = 366;

    public static readonly string RangeLimitMessage = $"At most {MaxRangeDays} days per request.";

    /// <summary>The journal's wording of the <see cref="MaxRangeDays"/> limit.</summary>
    public static readonly string JournalRangeLimitMessage = $"At most {MaxRangeDays} days.";

    /// <summary>
    /// The <c>from</c>/<c>to</c> range of a per-day read: <c>to</c> defaults to <paramref name="today"/> and can't be after it unless
    /// <paramref name="allowFutureTo"/>, <c>from</c> defaults to <paramref name="defaultDays"/> before <c>to</c> and can't be after <c>to</c>,
    /// and the range spans at most <see cref="MaxRangeDays"/> days unless <paramref name="noMaximum"/> (else <paramref name="limitMessage"/>
    /// on <c>from</c>, by default <see cref="RangeLimitMessage"/>). A malformed date stops the other checks.
    /// </summary>
    public static (DateOnly Start, DateOnly End, Dictionary<string, string[]> Errors) DayRange(string? from, string? to, DateOnly today,
        int defaultDays, bool allowFutureTo = false, bool noMaximum = false, string? limitMessage = null)
    {
        var errors = new Dictionary<string, string[]>();
        var end = DateOr(to, today, "to", errors);
        var start = DateOr(from, DaysBefore(end, defaultDays), "from", errors);
        if (errors.Count == 0)
        {
            if (!allowFutureTo && end > today) errors["to"] = ["Can't be after today."];
            else if (start > end) errors["from"] = ["Can't be after to."];
            else if (!noMaximum && end.DayNumber - start.DayNumber + 1 > MaxRangeDays) errors["from"] = [limitMessage ?? RangeLimitMessage];
        }
        return (start, end, errors);
    }
}
