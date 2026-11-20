namespace Ember.Bot.Discord.CustomIds;

/// <summary>
/// The missed-day reflection, bound to its owner: <c>reflect:{log|low|pick}:{discordUserId}:{yyyyMMdd}</c> and <c>reflect:dismiss:{discordUserId}</c>.
/// <c>date</c> is the reflected day as <c>yyyyMMdd</c>.
/// </summary>
internal static class ReflectIds
{
    public const string DismissPattern = "reflect:dismiss:*";
    public const string LogPattern = "reflect:log:*:*";
    public const string PickPattern = "reflect:pick:*:*";
    public const string LowPattern = "reflect:low:*:*";

    public static string Dismiss(ulong ownerId) => $"reflect:dismiss:{ownerId}";

    public static string Log(ulong ownerId, string date) => $"reflect:log:{ownerId}:{date}";

    /// <summary>The select of habits to log for the day.</summary>
    public static string Pick(ulong ownerId, string date) => $"reflect:pick:{ownerId}:{date}";

    public static string Low(ulong ownerId, string date) => $"reflect:low:{ownerId}:{date}";
}
