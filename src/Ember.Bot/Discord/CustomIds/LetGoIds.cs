using System.Globalization;

namespace Ember.Bot.Discord.CustomIds;

/// <summary>
/// A let-go habit's buttons: the slip and "made it today" buttons under the habit list (<c>letgo:slip:{habitId}</c>,
/// <c>letgo:madeit:{habitId}</c>) and the undo of a made-it mark (<c>letgo:madeit-undo:{habitId}:{yyyyMMdd}</c>).
/// </summary>
internal static class LetGoIds
{
    public const string SlipPattern = "letgo:slip:*";
    public const string MadeItPattern = "letgo:madeit:*";
    public const string UndoMadeItPattern = "letgo:madeit-undo:*:*";

    public static string Slip(int habitId) => $"letgo:slip:{habitId}";

    public static string MadeIt(int habitId) => $"letgo:madeit:{habitId}";

    /// <summary>Removes the mark of <paramref name="date"/>, the local day it was set on, in <see cref="HistoryIds.DateFormat"/>.</summary>
    public static string UndoMadeIt(int habitId, DateOnly date)
        => $"letgo:madeit-undo:{habitId}:{date.ToString(HistoryIds.DateFormat, CultureInfo.InvariantCulture)}";
}
