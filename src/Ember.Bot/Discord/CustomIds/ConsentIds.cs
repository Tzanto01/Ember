namespace Ember.Bot.CustomIds;

/// <summary>The health-data consent prompt's answers: <c>consent:{agree|decline}:{discordUserId}</c>.</summary>
internal static class ConsentIds
{
    public const string AgreePattern = "consent:agree:*";
    public const string DeclinePattern = "consent:decline:*";

    public static string Agree(ulong ownerId) => $"consent:agree:{ownerId}";

    public static string Decline(ulong ownerId) => $"consent:decline:{ownerId}";
}
