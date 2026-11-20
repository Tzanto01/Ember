namespace Ember.Bot.Discord.CustomIds;

/// <summary><c>/habit</c>: the habit pickers, the add and rename forms, the template picker, grace days and the streaks toggle.</summary>
internal static class HabitIds
{
    public const string SelectCheckIn = "habit:select:checkin";
    public const string SelectShare = "habit:select:share";
    public const string SelectDelete = "habit:select:delete";
    public const string SelectEdit = "habit:select:edit";
    public const string SelectPause = "habit:select:pause";
    public const string SelectFrequency = "habit:select:frequency";
    public const string SelectHistory = "habit:select:history";

    /// <summary>The add form; it has no arguments, so the id is its own handler pattern.</summary>
    public const string AddModal = "habit:add:modal";

    /// <summary>The template picker; it has no arguments, so the id is its own handler pattern.</summary>
    public const string TemplateSelect = "habit:template:select";

    public const string RenameModalPattern = "habit:rename:modal:*:*";
    public const string GracePattern = "habit:grace:*";
    public const string StreaksPattern = "habit:streaks:*";

    /// <summary>The rename form for <paramref name="habitId"/> as the clicked button carried it, and the message to update afterwards.</summary>
    public static string RenameModal(string habitId, ulong sourceMessageId) => $"habit:rename:modal:{habitId}:{sourceMessageId}";

    public static string Grace(int days) => $"habit:grace:{days}";

    /// <summary>Shows (<c>on</c>) or hides (<c>off</c>) the streak lines.</summary>
    public static string Streaks(bool show) => $"habit:streaks:{(show ? "on" : "off")}";
}
