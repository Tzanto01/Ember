using System.Globalization;

namespace Ember.Bot.Discord.CustomIds;

/// <summary>The history grid, bound to its owner: <c>history:day:{ownerId}:{habitId}:{yyyyMMdd}</c> and the low-day select <c>history:low:{ownerId}:{habitId}</c>.</summary>
internal static class HistoryIds
{
    /// <summary>The date format in day ids and in the low-day select's values.</summary>
    public const string DateFormat = "yyyyMMdd";

    public const string DayPattern = "history:day:*:*:*";
    public const string LowPattern = "history:low:*:*";

    public static string Day(ulong ownerId, int habitId, DateOnly date)
        => $"history:day:{ownerId}:{habitId}:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}";

    public static string Low(ulong ownerId, int habitId) => $"history:low:{ownerId}:{habitId}";

    /// <summary>A day of a let-go habit's grid. These buttons are always disabled, so no handler serves this id.</summary>
    public static string LetGo(int habitId, DateOnly date) => $"history:letgo:{habitId}:{date.ToString(DateFormat, CultureInfo.InvariantCulture)}";
}
