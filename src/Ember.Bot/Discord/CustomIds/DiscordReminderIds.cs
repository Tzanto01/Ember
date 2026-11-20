namespace Ember.Bot.CustomIds;

/// <summary>Turning Discord reminders on or off: <c>discordreminders:{yes|no|stop}:{discordUserId}</c>.</summary>
internal static class DiscordReminderIds
{
    public const string YesPattern = "discordreminders:yes:*";
    public const string NoPattern = "discordreminders:no:*";
    public const string StopPattern = "discordreminders:stop:*";

    /// <summary>The start of every stop id.</summary>
    public const string StopPrefix = "discordreminders:stop:";

    public static string Yes(ulong ownerId) => $"discordreminders:yes:{ownerId}";

    public static string No(ulong ownerId) => $"discordreminders:no:{ownerId}";

    public static string Stop(ulong ownerId) => $"discordreminders:stop:{ownerId}";
}
