using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Core.Localization;
using Ember.Core.Services;

namespace Ember.Bot.Modules;

/// <summary>
/// Fireside in Discord: <c>/fireside code</c>, <c>/fireside join</c>, <c>/fireside send</c> and the buttons of the Fireside DMs.
/// Turning Fireside on and choosing the name happen in the app or on the web. Custom IDs carry the clicking user's own link id, never a
/// user id: fireside:to (friend select, link id values), fireside:msg:{linkId} (message select), fireside:back:{linkId} ("Send some
/// back") and fireside:ok:{linkId} ("Okay", always removes the buttons). Every other handler checks that the link belongs to the clicking
/// user's account and otherwise answers <c>fireside.unavailable</c>. Handlers that read the database defer first. Every name is escaped with <see cref="Format.Sanitize"/>.
/// </summary>
[Group("fireside", "Send and get warmth from friends at your fireside")]
public class FiresideModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly FiresideService _fireside;

    public FiresideModule(HabitService habits, FiresideService fireside)
    {
        _habits = habits;
        _fireside = fireside;
    }

    /// <summary>The buttons of a Fireside DM: "Send some back" (warmth only) and "Okay", both for the receiver's <paramref name="linkId"/>.</summary>
    internal static MessageComponent NoticeButtons(Texts t, int linkId, bool warmth)
    {
        var buttons = new ComponentBuilder();
        if (warmth) buttons.WithButton(t.Get("fireside.send-back"), $"fireside:back:{linkId}", ButtonStyle.Primary);
        return buttons.WithButton(t.Get("fireside.okay"), $"fireside:ok:{linkId}", ButtonStyle.Secondary).Build();
    }

    private static MessageComponent MessageMenu(Texts t, int linkId)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId($"fireside:msg:{linkId}")
            .WithPlaceholder(t.Get("fireside.pick-message.placeholder"));
        foreach (var message in FiresideService.Messages)
            menu.AddOption(t.Get("fireside.message." + message), message);
        return new ComponentBuilder().WithSelectMenu(menu).Build();
    }

    [SlashCommand("code", "Get a code a friend can use to join your fireside")]
    public async Task CodeAsync()
    {
        await DeferAsync(ephemeral: true);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var code = userId == Guid.Empty ? null : await _fireside.CreateCodeAsync(userId);
        await FollowupAsync(code is null ? t.Get("fireside.off") : t.Format("fireside.code", FiresideService.DisplayCode(code.Code)), ephemeral: true);
    }

    [SlashCommand("join", "Join a friend's fireside with their code")]
    public async Task JoinAsync([Summary("code", "The code you got")] string code)
    {
        await DeferAsync(ephemeral: true);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        if (userId == Guid.Empty)
        {
            await FollowupAsync(t.Get("fireside.off"), ephemeral: true);
            return;
        }

        var (result, friend, _) = await _fireside.JoinAsync(userId, code);
        await FollowupAsync(result switch
        {
            FiresideJoinResult.Joined when friend is not null => t.Format("fireside.joined", Format.Sanitize(friend.Name)),
            FiresideJoinResult.Off => t.Get("fireside.off"),
            FiresideJoinResult.Full => t.Get("fireside.full"),
            FiresideJoinResult.RateLimited => t.Get("fireside.too-many"),
            _ => t.Get("fireside.invalid-code"),
        }, ephemeral: true);
    }

    [SlashCommand("send", "Send warmth to a friend")]
    public async Task SendAsync()
    {
        await DeferAsync(ephemeral: true);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        if (userId == Guid.Empty || await _fireside.GetStateAsync(userId) is not { Enabled: true } state)
        {
            await FollowupAsync(t.Get("fireside.off"), ephemeral: true);
            return;
        }
        if (state.Friends.Count == 0)
        {
            await FollowupAsync(t.Get("fireside.no-friends"), ephemeral: true);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId("fireside:to")
            .WithPlaceholder(t.Get("fireside.pick-friend.placeholder"));
        foreach (var friend in state.Friends.Take(FiresideService.MaxFriends))
            menu.AddOption(friend.Name.Length is > 0 and <= 100 ? friend.Name : "…", friend.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await FollowupAsync(t.Get("fireside.pick-friend"), components: new ComponentBuilder().WithSelectMenu(menu).Build(), ephemeral: true);
    }

    [ComponentInteraction("fireside:to", ignoreGroupNames: true)]
    public async Task PickFriendAsync(string[] values)
    {
        await DeferAsync();
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (await OwnLinkAsync(values.FirstOrDefault()) is not { } linkId)
        {
            await UpdateAsync(t.Get("fireside.unavailable"), new ComponentBuilder().Build());
            return;
        }
        await UpdateAsync(t.Get("fireside.pick-message"), MessageMenu(t, linkId));
    }

    [ComponentInteraction("fireside:msg:*", ignoreGroupNames: true)]
    public async Task PickMessageAsync(string link, string[] values)
    {
        await DeferAsync();
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var result = int.TryParse(link, out var linkId) && userId != Guid.Empty
            ? await _fireside.SendWarmthAsync(userId, linkId, values.FirstOrDefault())
            : FiresideSendResult.NotFound;
        await UpdateAsync(result switch
        {
            FiresideSendResult.Sent => t.Get("fireside.sent"),
            FiresideSendResult.Limit => t.Get("fireside.limit"),
            _ => t.Get("fireside.unavailable"),
        }, new ComponentBuilder().Build());
    }

    [ComponentInteraction("fireside:back:*", ignoreGroupNames: true)]
    public async Task SendBackAsync(string link)
    {
        await DeferAsync(ephemeral: true);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (await OwnLinkAsync(link) is not { } linkId)
        {
            await FollowupAsync(t.Get("fireside.unavailable"), ephemeral: true);
            return;
        }
        await FollowupAsync(t.Get("fireside.pick-message"), components: MessageMenu(t, linkId), ephemeral: true);
    }

    [ComponentInteraction("fireside:ok:*", ignoreGroupNames: true)]
    public async Task OkayAsync(string link)
        => await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m => m.Components = new ComponentBuilder().Build());

    /// <summary>The link id when <paramref name="value"/> is one of the clicking user's own links, else null.</summary>
    private async Task<int?> OwnLinkAsync(string? value)
    {
        if (!int.TryParse(value, out var linkId)) return null;
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        return userId != Guid.Empty && await _fireside.GetFriendAsync(userId, linkId) is not null ? linkId : null;
    }

    private async Task UpdateAsync(string text, MessageComponent components) =>
        await ((SocketMessageComponent)Context.Interaction).ModifyOriginalResponseAsync(m =>
        {
            m.Content = text;
            m.Components = components;
        });
}
