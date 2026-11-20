namespace Ember.Core.Services;

/// <summary>Outcome of <see cref="AccountService.MergeBotOnlyAccountAsync"/>.</summary>
public enum AccountMergeResult
{
    /// <summary>The bot-only account was merged into the target and deleted.</summary>
    Merged,

    /// <summary>
    /// Nothing changed: the source is no longer a bot-only account owning the Discord login, or the target is missing
    /// or already has a Discord login.
    /// </summary>
    Conflict,

    /// <summary>Nothing changed: both accounts have a private-journal key.</summary>
    JournalConflict,

    /// <summary>Nothing changed: the target hasn't agreed to the current health-data consent, which the moved health data needs.</summary>
    ConsentRequired,
}

/// <summary>
/// What a merge of a bot-only account would move. <c>FiresideDropped</c> counts the source's Fireside friends that would not come
/// along (already friends with the target, or over the cap); a link between the two accounts doesn't count.
/// </summary>
public sealed record MergePreview(int Habits, int Logs, int DayMarks, IReadOnlyList<string> HabitNames, int FiresideDropped = 0);
