using Ember.Core.Rules;

namespace Ember.Core.Services;

/// <summary>Outcome of creating or editing a habit.</summary>
public enum HabitWriteResult
{
    /// <summary>The habit was stored.</summary>
    Saved,

    /// <summary>The habit doesn't exist or belongs to someone else. Nothing was written.</summary>
    NotFound,

    /// <summary>The user already tracks <see cref="Limits.HabitsPerUser"/> habits. Nothing was written.</summary>
    LimitReached,

    /// <summary>The user hasn't agreed to the current health-data consent. Nothing was written.</summary>
    ConsentRequired,
}
