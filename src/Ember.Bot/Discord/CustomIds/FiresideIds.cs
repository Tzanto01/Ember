namespace Ember.Bot.Discord.CustomIds;

/// <summary>Fireside: the friend and message pickers and the notice buttons, by the receiver's link id.</summary>
internal static class FiresideIds
{
    /// <summary>The friend picker; it has no arguments, so the id is its own handler pattern.</summary>
    public const string To = "fireside:to";

    public const string MessagePattern = "fireside:msg:*";
    public const string BackPattern = "fireside:back:*";

    /// <summary>Matches both <c>fireside:ok:{linkId}</c> and <c>fireside:ok:{linkId}:{warmthId}</c>; the handler splits the rest.</summary>
    public const string OkPattern = "fireside:ok:*";

    public static string Message(int linkId) => $"fireside:msg:{linkId}";

    public static string Back(int linkId) => $"fireside:back:{linkId}";

    /// <summary>"Okay", which also dismisses <paramref name="warmthId"/> when there is one.</summary>
    public static string Ok(int linkId, int? warmthId) => warmthId is null ? $"fireside:ok:{linkId}" : $"fireside:ok:{linkId}:{warmthId}";
}
