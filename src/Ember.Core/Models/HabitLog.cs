namespace Ember.Core.Models;

public class HabitLog
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>
    /// The user's local calendar date (in <see cref="User.Timezone"/>) this log entry is for.
    /// Logs written before <see cref="Rules.TimeZones.SwitchoverDate"/> carry the UTC date.
    /// </summary>
    public DateOnly Date { get; set; }

    /// <summary>
    /// True when the day is done: <see cref="Count"/> reached the habit's <see cref="Habit.TimesPerDay"/> at the time of writing.
    /// A log with <see cref="Count"/> 0 and Completed false is a skip ("not done") when <see cref="Skipped"/> is set, otherwise a count lowered to 0.
    /// Never true together with <see cref="Skipped"/>.
    /// </summary>
    public bool Completed { get; set; }

    /// <summary>Number of check-ins that day, 0 to the habit's <see cref="Habit.TimesPerDay"/>.</summary>
    public int Count { get; set; }

    /// <summary>
    /// True when the day was written by a skip, so it is not done (<see cref="Completed"/> false): a Discord skip (<c>/habit checkin</c>
    /// or a DM reminder's Skip) keeps the day's <see cref="Count"/>, an explicit "not done" (the API, <c>/habit history</c>) writes
    /// <see cref="Count"/> 0. A skip of a day already done keeps it done and leaves this false. Any other write (a check-in, a count change,
    /// including to 0) resets it. A skip of the user's local today ends that day's reminders. Logs written before the marker existed carry false.
    /// </summary>
    public bool Skipped { get; set; }

    /// <summary>Guild in which the check-in was performed. Null if done via DM.</summary>
    public long? GuildId { get; set; }

    public Habit Habit { get; set; } = null!;
}
