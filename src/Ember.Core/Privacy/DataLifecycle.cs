using Ember.Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Ember.Core.Privacy;

/// <summary>What happens to an entity's rows when the health data of a user whose consent stayed withdrawn is deleted.</summary>
public enum WithdrawalRule
{
    /// <summary>Health data: every row of the user is deleted.</summary>
    Deleted,

    /// <summary>Not health data itself; the rows tied to a habit go with the habit, the others stay.</summary>
    DeletedWithHabit,

    /// <summary>Kept.</summary>
    Kept,
}

/// <summary>Which data exports hold an entity's rows.</summary>
public enum ExportRule
{
    /// <summary>The web export and the Discord export.</summary>
    WebAndDiscord,

    /// <summary>The web export only.</summary>
    WebOnly,

    /// <summary>No export.</summary>
    Never,
}

/// <summary>What merging a bot-only account into a web account does with the source's rows.</summary>
public enum MergeRule
{
    /// <summary>Every row moves to the target.</summary>
    Moves,

    /// <summary>The rows move to the target, except where the target already has the same one.</summary>
    MovesUnlessTargetHasIt,

    /// <summary>The rows move with the parent row they belong to.</summary>
    MovesWithParent,

    /// <summary>The rows go with the source account's deletion.</summary>
    DeletedWithSource,

    /// <summary>The source and target rows are combined into the target's.</summary>
    Combined,

    /// <summary>The rows aren't keyed to a user, so the merge leaves them as they are.</summary>
    Untouched,
}

/// <summary>
/// One entity of the EF model and its place in the data lifecycle: whose it is (<paramref name="Owner"/>, the path to the user),
/// what a health-data deletion does with it, whether it makes an account non-empty for an import, which exports hold it and
/// what an account merge does with it. <paramref name="ExportNote"/> and <paramref name="MergeNote"/> give the detail and, for
/// <see cref="ExportRule.Never"/>, the reason.
/// </summary>
public sealed record UserData(
    Type Entity, string Owner, WithdrawalRule Withdrawal, bool BlocksImport, ExportRule Export, string ExportNote, MergeRule Merge,
    string MergeNote)
{
    /// <summary>Whether the rows are health data, deleted when the consent stays withdrawn.</summary>
    public bool HealthData => Withdrawal == WithdrawalRule.Deleted;
}

/// <summary>
/// The data-lifecycle registry: every entity type the EF model maps, with what consent withdrawal, import, export and account merge
/// do with it today. The architecture tests hold the model, the health-data deletion, the import emptiness check and the export
/// to it, so a new entity can't be added without deciding each of these.
/// </summary>
public static class DataLifecycle
{
    /// <summary>Every entity type of <see cref="Data.EmberDbContext"/>.</summary>
    public static readonly IReadOnlyList<UserData> Entities =
    [
        new(typeof(User), "Id", WithdrawalRule.Kept, false, ExportRule.WebAndDiscord,
            "Settings and consent; email, creation time and Today order in the web export only. Secrets (password hash, email codes) never.",
            MergeRule.Combined,
            "The target keeps its settings; it takes the source's Discord reminder answer when it has none, the union of seen tips and the "
            + "source's Fireside settings when its Fireside is off. The source is deleted."),
        new(typeof(IdentityUserLogin<Guid>), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "The Discord ID only.", MergeRule.Moves, "The Discord login moves; the target has none."),
        new(typeof(IdentityUserClaim<Guid>), "UserId", WithdrawalRule.Kept, false, ExportRule.Never,
            "Unused by Ember.", MergeRule.DeletedWithSource, "Unused by Ember."),
        new(typeof(IdentityUserToken<Guid>), "UserId", WithdrawalRule.Kept, false, ExportRule.Never,
            "Authenticator keys and recovery codes are secrets.", MergeRule.DeletedWithSource, "A bot-only account has no two-step sign-in."),
        new(typeof(Habit), "UserId", WithdrawalRule.Deleted, true, ExportRule.WebAndDiscord,
            "", MergeRule.Moves, "The habit limit does not apply."),
        new(typeof(HabitLog), "Habit.UserId", WithdrawalRule.Deleted, false, ExportRule.WebAndDiscord,
            "", MergeRule.MovesWithParent, "With its habit."),
        new(typeof(HabitSlip), "Habit.UserId", WithdrawalRule.Deleted, false, ExportRule.WebAndDiscord,
            "", MergeRule.MovesWithParent, "With its habit."),
        new(typeof(MadeItDay), "Habit.UserId", WithdrawalRule.Deleted, false, ExportRule.WebAndDiscord,
            "", MergeRule.MovesWithParent, "With its habit."),
        new(typeof(HabitNote), "Habit.UserId", WithdrawalRule.Deleted, false, ExportRule.WebOnly,
            "", MergeRule.MovesWithParent, "With its habit."),
        new(typeof(DayMark), "UserId", WithdrawalRule.Deleted, true, ExportRule.WebAndDiscord,
            "", MergeRule.MovesUnlessTargetHasIt, "One per day and kind."),
        new(typeof(DayNote), "UserId", WithdrawalRule.Deleted, true, ExportRule.WebOnly,
            "", MergeRule.MovesUnlessTargetHasIt, "The target's entry wins on a date both have."),
        new(typeof(Mood), "UserId", WithdrawalRule.Deleted, true, ExportRule.WebOnly,
            "", MergeRule.MovesUnlessTargetHasIt,
            "A plaintext mood with the same trimmed label (ignoring case) as a target mood is combined into it, unless the source has encrypted entries."),
        new(typeof(UserHelps), "UserId", WithdrawalRule.Deleted, false, ExportRule.WebOnly,
            "The ciphertext as stored.", MergeRule.MovesUnlessTargetHasIt, "Dropped with the source when the target has a list."),
        new(typeof(Routine), "UserId", WithdrawalRule.Kept, true, ExportRule.WebAndDiscord,
            "", MergeRule.Moves, "After the target's routines, keeping their order; the routine limit does not apply."),
        new(typeof(JournalKey), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "The wrapped key; the server never has the unwrapped one.", MergeRule.Moves, "The merge is refused when both accounts have one."),
        new(typeof(PushSubscription), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "Endpoint, label, enabled and time; keys never.", MergeRule.Moves,
            "One without a session joins the merging session; kept at most at the per-user limit."),
        new(typeof(ReminderPhone), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "", MergeRule.Moves, "One without a session joins the merging session; kept at most at the per-user limit."),
        new(typeof(ReminderMessage), "UserId", WithdrawalRule.DeletedWithHabit, false, ExportRule.Never,
            "Delivery state of sent reminders.", MergeRule.Moves, ""),
        new(typeof(PushReminder), "UserId", WithdrawalRule.DeletedWithHabit, false, ExportRule.Never,
            "Delivery state of sent reminders.", MergeRule.Moves, ""),
        new(typeof(ReminderActionToken), "UserId", WithdrawalRule.DeletedWithHabit, false, ExportRule.Never,
            "Only a hash of a one-time token.", MergeRule.Moves, ""),
        new(typeof(AuthSession), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "Times, platform, app version and whether a widget key is set (decision D7); the session id (a credential) and the widget key hash never.",
            MergeRule.DeletedWithSource, "The source's sessions are ended before the merge."),
        new(typeof(Feedback), "UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "Linked reports only (decision D7); DigestedAt never.", MergeRule.Moves, ""),
        new(typeof(FeedbackImage), "Feedback.UserId", WithdrawalRule.Kept, false, ExportRule.WebOnly,
            "With its report: base64 in the JSON, listed without bytes in the CSV (decision D7).", MergeRule.MovesWithParent, "With its feedback."),
        new(typeof(FiresideCode), "UserId", WithdrawalRule.Kept, false, ExportRule.Never,
            "Fireside codes are never exported.", MergeRule.DeletedWithSource, ""),
        new(typeof(FiresideLink), "OwnerId, FriendId", WithdrawalRule.Kept, false, ExportRule.Never,
            "Fireside friends are never exported.", MergeRule.MovesUnlessTargetHasIt,
            "Up to the friend limit, none the target already has; links between the two accounts are dropped."),
        new(typeof(Warmth), "FromUserId, ToUserId", WithdrawalRule.Kept, false, ExportRule.Never,
            "Warmth is never exported.", MergeRule.MovesUnlessTargetHasIt, "Between the friends that move; the rest goes with the source."),
        new(typeof(LookoutUserRef), "UserId", WithdrawalRule.Kept, false, ExportRule.Never,
            "A pseudonymous reference for the feedback board.", MergeRule.Moves, ""),
        new(typeof(LookoutOutbox), "UserRef (no foreign key)", WithdrawalRule.Kept, false, ExportRule.Never,
            "The queue to the feedback board, keyed by reference, not by user.", MergeRule.Untouched, ""),
    ];
}
