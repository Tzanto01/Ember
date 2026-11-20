using Ember.Core.Rules;

namespace Ember.Core.Services;

/// <summary>
/// Thrown when a user already tracks <see cref="Limits.HabitsPerUser"/> habits.
/// The message is user-facing copy.
/// </summary>
public sealed class HabitLimitReachedException : InvalidOperationException
{
    public HabitLimitReachedException()
        : base($"You're already tracking {Limits.HabitsPerUser} habits, which is the most Ember can hold at once. " +
               "If you'd like to add this one, make some room with `/habit delete` first. 💙")
    {
    }
}
