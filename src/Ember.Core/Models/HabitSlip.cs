namespace Ember.Core.Models;

/// <summary>The slips of a <see cref="HabitKind.LetGo"/> habit on one local day: at most one row per habit and date.</summary>
public class HabitSlip
{
    /// <summary>The reason ids a slip can name, in the order the clients offer them.</summary>
    public static readonly IReadOnlyList<string> ReasonIds = ["stressed", "bored", "tired", "lonely", "aroundOthers", "other"];

    public int Id { get; set; }
    public int HabitId { get; set; }

    /// <summary>The user's local calendar date (in <see cref="User.Timezone"/>) of the slips.</summary>
    public DateOnly Date { get; set; }

    /// <summary>Number of slips that day, at least 1.</summary>
    public int Count { get; set; }

    /// <summary>The distinct <see cref="ReasonIds"/> picked for that day's slips, in the order they were first picked; may be empty.</summary>
    public List<string> Reasons { get; set; } = [];

    public DateTime UpdatedAt { get; set; }

    public Habit Habit { get; set; } = null!;
}
