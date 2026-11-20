namespace Ember.Bot.CustomIds;

/// <summary>"Try again" after DM reminders were paused: <c>dmretry:{discordUserId}</c> (no action segment).</summary>
internal static class DmRetryIds
{
    public const string RetryPattern = "dmretry:*";

    public static string Retry(ulong ownerId) => $"dmretry:{ownerId}";
}
