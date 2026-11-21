using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;

namespace Ember.Bot.Discord.Messages;

/// <summary>The buttons of a Fireside DM (<see cref="FiresideIds"/>).</summary>
internal static class FiresideDm
{
    /// <summary>
    /// The buttons of a Fireside DM for the receiver's <paramref name="linkId"/>: "Send some back" (warmth only) and "Okay", which also
    /// dismisses the warmth <paramref name="warmthId"/>.
    /// </summary>
    internal static MessageComponent NoticeButtons(Texts t, int linkId, int? warmthId)
    {
        var buttons = new ComponentBuilder();
        if (warmthId is null) return buttons.WithButton(t.Get("fireside.okay"), FiresideIds.Ok(linkId, null), ButtonStyle.Secondary).Build();
        return buttons.WithButton(t.Get("fireside.send-back"), FiresideIds.Back(linkId), ButtonStyle.Primary)
            .WithButton(t.Get("fireside.okay"), FiresideIds.Ok(linkId, warmthId), ButtonStyle.Secondary).Build();
    }
}
