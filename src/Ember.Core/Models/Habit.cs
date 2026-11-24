using System.ComponentModel.DataAnnotations.Schema;

namespace Ember.Core.Models;

public enum FrequencyType { Daily, Weekly, Weekdays }

/// <summary>Build = a habit to build up (check-ins, streaks). LetGo = something to stop or do less (slips, days free); set at creation, never changed.</summary>
public enum HabitKind { Build, LetGo }

/// <summary>
/// The server channels the bot dispatches by (<see cref="Discord"/>, <see cref="WebPush"/>) and the names of the deprecated
/// <c>reminderChannels</c> field (v1.14 derives them, <see cref="Services.ReminderDestinations.LegacyChannels"/>).
/// </summary>
[Flags]
public enum ReminderChannels
{
    None = 0,
    Discord = 1,
    WebPush = 2,

    /// <summary>Only a name of the deprecated <c>reminderChannels</c> field (v1.14 derives it when no server destination is a target); never dispatched.</summary>
    Mobile = 4,
}

public class Habit
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether the habit is built up or let go of. A <see cref="HabitKind.LetGo"/> habit is daily, once a day, has at most one reminder
    /// time, no routine and no <see cref="Logs"/>; it records <see cref="Slips"/> and <see cref="MadeItDays"/> instead.
    /// </summary>
    public HabitKind Kind { get; set; } = HabitKind.Build;

    /// <summary>
    /// Times of day to send the reminder, in the user's local time, sorted ascending without repeats; at most
    /// <see cref="TimesPerDay"/> of them. Empty means no reminder.
    /// </summary>
    public List<TimeOnly> ReminderTimes { get; set; } = [];

    /// <summary>The first of <see cref="ReminderTimes"/>, or null. Setting it replaces all of them with this one time, or clears them when null.</summary>
    [NotMapped]
    public TimeOnly? ReminderTime
    {
        get => ReminderTimes.Count > 0 ? ReminderTimes[0] : null;
        set => ReminderTimes = value is { } time ? [time] : [];
    }

    /// <summary>UTC instant of the next reminder (the next local time of any of the <see cref="ReminderTimes"/>, or a snooze). Null means nothing is due.</summary>
    public DateTime? NextReminderUtc { get; set; }

    /// <summary>
    /// UTC instant of the pending second reminder (<see cref="User.SecondReminderMinutes"/> after the regular reminder was sent,
    /// on the same local day). Null when none is pending; a snooze clears it.
    /// </summary>
    public DateTime? SecondReminderUtc { get; set; }

    /// <summary>
    /// Last local day of the pause. Days from <see cref="PausedFrom"/> through this day are paused: reminders are
    /// suppressed and the days are excluded from streak calculations.
    /// </summary>
    public DateOnly? PausedUntil { get; set; }

    /// <summary>First local day of the pause. Null with a <see cref="PausedUntil"/> means every day up to it is paused (pauses from before this field existed).</summary>
    public DateOnly? PausedFrom { get; set; }

    /// <summary>Daily = check in every day. Weekly = check in WeeklyTarget times per week. Weekdays = check in on the <see cref="Days"/> only.</summary>
    public FrequencyType FrequencyType { get; set; } = FrequencyType.Daily;

    /// <summary>Target number of check-ins per week when FrequencyType = Weekly. Null for daily habits.</summary>
    public int? WeeklyTarget { get; set; }

    /// <summary>The weekdays a <see cref="FrequencyType.Weekdays"/> habit is scheduled on. Null for daily and weekly habits.</summary>
    public ScheduleDays? Days { get; set; }

    /// <summary>Check-ins needed for a day to be done (always 1 for weekly habits).</summary>
    public int TimesPerDay { get; set; } = 1;

    /// <summary>
    /// Length of the habit's timer in minutes, 1 to <see cref="Rules.Limits.TimerMinutes"/>; null without one, always for a
    /// <see cref="HabitKind.LetGo"/> habit. The running timer lives on the device only.
    /// </summary>
    public int? TimerMinutes { get; set; }

    /// <summary>The routine this habit belongs to, if any.</summary>
    public int? RoutineId { get; set; }

    /// <summary>Order of this habit inside its routine.</summary>
    public int RoutinePosition { get; set; }

    /// <summary>Reminder destination ids (<see cref="Services.ReminderDestinations"/>) this habit does not remind on; empty means everywhere.</summary>
    public List<string> ReminderExclusions { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public Routine? Routine { get; set; }
    public ICollection<HabitLog> Logs { get; set; } = new List<HabitLog>();
    public ICollection<HabitSlip> Slips { get; set; } = new List<HabitSlip>();
    public ICollection<MadeItDay> MadeItDays { get; set; } = new List<MadeItDay>();
    public ICollection<HabitNote> Notes { get; set; } = new List<HabitNote>();
}
