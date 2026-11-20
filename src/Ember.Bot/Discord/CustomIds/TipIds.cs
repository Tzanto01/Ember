namespace Ember.Bot.Discord.CustomIds;

/// <summary>The one-time streaks question: <c>tip:streaks:{on|off}</c>.</summary>
internal static class TipIds
{
    public const string StreaksPattern = "tip:streaks:*";

    public static string Streaks(bool show) => $"tip:streaks:{(show ? "on" : "off")}";
}
