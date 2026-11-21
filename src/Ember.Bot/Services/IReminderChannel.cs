using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Services;

namespace Ember.Bot.Services;

public enum DeliveryOutcome
{
    Delivered,
    UserNotFound,
    Undeliverable
}

/// <param name="UserId">The Ember user ID.</param>
/// <param name="DiscordUserId">The user's Discord ID, null when no Discord login is linked.</param>
/// <param name="ItemId">The id of the habit or routine the reminder is for.</param>
/// <param name="ItemName">The name of the habit or routine the reminder is for.</param>
/// <param name="RecentCompletions">Completed check-ins in the 7 local days before today; picks the tone.</param>
/// <param name="Snoozed">True when this is the follow-up of a snooze rather than the daily reminder.</param>
/// <param name="PendingHabits">
/// Set for a routine reminder (<see cref="IsRoutine"/>): the member habits still pending or partial today, in routine order.
/// </param>
/// <param name="Day">The user's local day the reminder is for.</param>
/// <param name="Second">True for the one second reminder that follows a sent regular reminder.</param>
/// <param name="PushSubscriptionIds">The push subscriptions the reminder may go to (<see cref="ReminderTargets.PushSubscriptionIds"/>); null or empty sends no web push.</param>
/// <param name="Language">The user's language (<see cref="User.Language"/>) the reminder is written in.</param>
/// <param name="LetGo">
/// True for a <see cref="HabitKind.LetGo"/> habit's quiet check-in: the <c>letgo.reminder</c> text without Done, Skip or Snooze
/// buttons, push actions or action tokens, never recorded for closing.
/// </param>
public sealed record Reminder(Guid UserId, ulong? DiscordUserId, int ItemId, string ItemName, int RecentCompletions, bool Snoozed,
    DateOnly Day, IReadOnlyList<RoutineHabit>? PendingHabits = null, bool Second = false, IReadOnlyList<int>? PushSubscriptionIds = null,
    string Language = Languages.English, bool LetGo = false)
{
    /// <summary>True for a routine reminder (<see cref="PendingHabits"/> is set), false for a habit reminder.</summary>
    public bool IsRoutine => PendingHabits is not null;
}

/// <summary>A member habit listed in a routine reminder.</summary>
public sealed record RoutineHabit(int Id, string Name);

/// <summary>Delivers a due habit reminder to a user through one channel (Discord DM, web push).</summary>
public interface IReminderChannel
{
    /// <summary>The habit reminder channel this implementation delivers.</summary>
    ReminderChannels Channel { get; }

    Task<DeliveryOutcome> SendAsync(Reminder reminder, CancellationToken cancellationToken = default);
}
