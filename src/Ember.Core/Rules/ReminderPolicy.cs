using Ember.Core.Models;

namespace Ember.Core.Rules;

/// <summary>Whether a due habit reminder or second reminder is sent, and when a second reminder follows a delivered one.</summary>
public static class ReminderPolicy
{
    /// <summary>How long after its due time a reminder may still be sent; later ones (e.g. after downtime) are skipped.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(2);

    /// <summary>True when a reminder due at <paramref name="dueAt"/> is more than <see cref="StaleAfter"/> overdue at <paramref name="now"/>.</summary>
    public static bool IsStale(DateTime dueAt, DateTime now)
        => now - dueAt > StaleAfter;

    /// <summary>
    /// Whether the habit's reminder due at <paramref name="dueAt"/> is sent on the user's local <paramref name="today"/>: not when its
    /// routine has its own reminder time, when it is stale, or when the habit is paused or not scheduled today. A let-go habit's quiet
    /// check-in needs nothing more; any other habit must also still be open today (<see cref="IsStillOpen"/>).
    /// </summary>
    public static bool ShouldSendReminder(Habit habit, DateTime dueAt, DateTime now, DateOnly today, ReminderDayFacts facts)
        => habit.Routine?.ReminderTime is null
            && !IsStale(dueAt, now)
            && !HabitRules.IsPausedOn(habit, today)
            && HabitRules.IsScheduledOn(habit, today)
            && (habit.Kind == HabitKind.LetGo || IsStillOpen(habit, facts));

    /// <summary>
    /// Whether the habit's second reminder due at <paramref name="dueAt"/> is sent: never for a let-go habit, a habit without a reminder
    /// time or one whose routine has its own; otherwise only while it <see cref="IsSecondStillDue"/> and the habit is not paused,
    /// scheduled and still open on the user's local <paramref name="today"/> (<see cref="IsStillOpen"/>).
    /// </summary>
    public static bool ShouldSendSecondReminder(Habit habit, int secondReminderMinutes, DateTime dueAt, DateTime now, DateOnly today,
        TimeZoneInfo tz, ReminderDayFacts facts)
        => habit.Kind != HabitKind.LetGo
            && habit.ReminderTime is not null
            && habit.Routine?.ReminderTime is null
            && IsSecondStillDue(secondReminderMinutes, dueAt, now, today, tz)
            && !HabitRules.IsPausedOn(habit, today)
            && HabitRules.IsScheduledOn(habit, today)
            && IsStillOpen(habit, facts);

    /// <summary>
    /// Whether a second reminder (of a habit or routine) due at <paramref name="dueAt"/> still applies: the user's second reminder is on,
    /// it isn't stale and it falls on the user's local <paramref name="today"/>, the day of the first reminder.
    /// </summary>
    public static bool IsSecondStillDue(int secondReminderMinutes, DateTime dueAt, DateTime now, DateOnly today, TimeZoneInfo tz)
        => secondReminderMinutes > 0
            && !IsStale(dueAt, now)
            && TimeZones.LocalDay(dueAt, tz) == today;

    /// <summary>
    /// When the user's second reminder is on: <paramref name="secondReminderMinutes"/> after <paramref name="sentAt"/>, or null when
    /// that falls on another local day than <paramref name="today"/>.
    /// </summary>
    public static DateTime? SecondReminderAt(int secondReminderMinutes, TimeZoneInfo tz, DateOnly today, DateTime sentAt)
        => secondReminderMinutes > 0 && sentAt.AddMinutes(secondReminderMinutes) is var at && TimeZones.LocalDay(at, tz) == today
            ? at
            : null;

    /// <summary>
    /// Whether the habit still wants a reminder today: not completed, not a weekly habit whose week is done
    /// (<see cref="HabitRules.IsWeekDone"/>) and today is no low day.
    /// </summary>
    public static bool IsStillOpen(Habit habit, ReminderDayFacts facts)
        => !facts.CompletedToday
            && !HabitRules.IsWeekDone(habit, facts.CompletedInWeek)
            && !facts.LowDay;
}
