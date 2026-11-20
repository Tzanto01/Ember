namespace Ember.Bot.Discord.CustomIds;

/// <summary>The slip buttons under the habit list: <c>letgo:slip:{habitId}</c>.</summary>
internal static class LetGoIds
{
    public const string SlipPattern = "letgo:slip:*";

    public static string Slip(int habitId) => $"letgo:slip:{habitId}";
}
