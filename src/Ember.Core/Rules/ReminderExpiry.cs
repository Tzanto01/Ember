namespace Ember.Core.Rules;

/// <summary>
/// When a reminder for a local day stops being usable: at <see cref="LocalHour"/>:00 local time on the next day, when the
/// "Log yesterday" check-in takes over. Its Discord buttons close then and its web push Done token expires.
/// </summary>
public static class ReminderExpiry
{
    /// <summary>Local hour on the day after a reminder's day at which the reminder expires.</summary>
    public const int LocalHour = 10;

    /// <summary>The local wall-clock time at which a reminder for the local <paramref name="day"/> expires.</summary>
    public static DateTime ExpiresLocal(DateOnly day)
        => day.AddDays(1).ToDateTime(new TimeOnly(LocalHour, 0));

    /// <summary>True once a reminder for the local <paramref name="day"/> can no longer be used at the user's <paramref name="localNow"/>.</summary>
    public static bool IsExpired(DateOnly day, DateTime localNow)
        => localNow >= ExpiresLocal(day);
}
