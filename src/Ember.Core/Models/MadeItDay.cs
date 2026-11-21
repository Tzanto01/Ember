namespace Ember.Core.Models;

/// <summary>
/// A "made it today" mark of a <see cref="HabitKind.LetGo"/> habit on one local day: at most one row per habit and date. An
/// acknowledgement only; it changes no let-go number.
/// </summary>
public class MadeItDay
{
    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>The user's local calendar date (in <see cref="User.Timezone"/>) of the mark.</summary>
    public DateOnly Date { get; set; }

    public DateTime CreatedAt { get; set; }

    public Habit Habit { get; set; } = null!;
}
