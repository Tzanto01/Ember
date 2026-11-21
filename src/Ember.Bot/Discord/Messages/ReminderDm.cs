using Ember.Core.Localization;

namespace Ember.Bot.Discord.Messages;

/// <summary>The lines of a reminder DM shared by the reminder channel, its buttons and the dispatch job.</summary>
internal static class ReminderDm
{
    /// <summary>Label of the reminder's Skip button; calm, with no red mark.</summary>
    public static string SkipLabel(Texts t) => t.Get("reminder.skip");

    /// <summary>Subtext added to a closed reminder whose habits are done for its day.</summary>
    public static string LoggedLine(Texts t) => t.Get("reminder.logged-line");

    /// <summary>Subtext added to a closed routine reminder that has nothing left for its day, though none of its habits is done.</summary>
    public static string NothingLeftLine(Texts t) => t.Get("reminder.nothing-left-line");

    /// <summary>Subtext added to a closed reminder whose habits are not all done for its day.</summary>
    public static string ClosedLine(Texts t) => t.Get("reminder.closed-line");
}
