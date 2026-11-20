using System.Reflection;
using System.Runtime.CompilerServices;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Modules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Tests;

/// <summary>An answer a handler gave through the module's response methods.</summary>
internal sealed record BotReply(string? Text, Embed? Embed, MessageComponent? Components, bool Ephemeral);

/// <summary>A file a handler attached to its answer, read while the handler's stream was still open.</summary>
internal sealed record BotFile(string FileName, string Content);

/// <summary>What a driven handler answered. Edits of the clicked message (<c>UpdateAsync</c>) go to Discord directly and are not recorded.</summary>
internal sealed class BotReplies
{
    public List<BotReply> Sent { get; } = [];
    public List<BotFile> Files { get; } = [];
}

/// <summary>
/// Drives real <see cref="InteractionModuleBase{T}"/> handlers without Discord: an uninitialized <see cref="SocketInteractionContext"/> carries the
/// user and locale, and the recording modules below capture what the handler answers. A handler that edits the clicked message
/// (<c>SocketMessageComponent.UpdateAsync</c>) or answers through <c>Context.Interaction</c> cannot be captured, because those
/// calls are not virtual; <see cref="RunAsync"/> lets such a final call fail so the persisted state can be asserted.
/// </summary>
internal static class BotHandlers
{
    public static readonly IConfiguration NoConfig = new ConfigurationBuilder().Build();

    public static SocketInteractionContext ContextFor<TInteraction>(ulong discordId, string locale = "en-US") where TInteraction : SocketInteraction
    {
        var user = (SocketUser)RuntimeHelpers.GetUninitializedObject(typeof(SocketUser).Assembly.GetType("Discord.WebSocket.SocketGlobalUser")!);
        Set(user, "Id", discordId);
        var interaction = (TInteraction)RuntimeHelpers.GetUninitializedObject(typeof(TInteraction));
        Set(interaction, "User", user);
        Set(interaction, "UserLocale", locale);
        var context = (SocketInteractionContext)RuntimeHelpers.GetUninitializedObject(typeof(SocketInteractionContext));
        Set(context, "Interaction", interaction);
        Set(context, "User", user);
        return context;
    }

    public static T Attach<T>(T module, ulong discordId, string locale = "en-US", bool slash = false) where T : IInteractionModuleBase
    {
        module.SetContext(slash ? ContextFor<SocketSlashCommand>(discordId, locale) : ContextFor<SocketMessageComponent>(discordId, locale));
        return module;
    }

    /// <summary>Runs a handler whose last step edits the clicked message or answers through Discord itself; that step fails here, anything else rethrows.</summary>
    public static async Task RunAsync(Func<Task> handler)
    {
        try
        {
            await handler();
        }
        catch (Exception ex) when (ReachedDiscord(ex))
        {
        }
    }

    private static bool ReachedDiscord(Exception ex)
        => ex.StackTrace is { } trace && trace.Contains("Discord.WebSocket.Socket", StringComparison.Ordinal)
           && (trace.Contains("UpdateAsync", StringComparison.Ordinal) || trace.Contains("RespondAsync", StringComparison.Ordinal) || trace.Contains("FollowupAsync", StringComparison.Ordinal));

    public static IEnumerable<string?> ButtonIds(MessageComponent? components)
        => components!.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.CustomId);

    public static IEnumerable<string?> ComponentIds(MessageComponent? components)
        => components!.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<IInteractableComponent>().Select(c => c.CustomId);

    private static void Set(object target, string name, object value)
    {
        for (var type = target.GetType(); type is not null; type = type.BaseType)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            if (type.GetProperty(name, all) is { SetMethod: not null } property) { property.SetMethod.Invoke(target, [value]); return; }
            if (type.GetField($"<{name}>k__BackingField", all) is { } field) { field.SetValue(target, value); return; }
        }
        throw new InvalidOperationException(name);
    }
}

internal sealed class RecordingHabitActionModule(HabitService habits, BotReplies replies) : HabitActionModule(habits, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingFrequencyModule(HabitService habits, BotReplies replies) : FrequencyButtonModule(habits)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingPrivacyModule(HabitService habits, AccountService accounts, BotReplies replies) : PrivacyModule(habits, accounts, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingTimezoneModule(HabitService habits, BotReplies replies) : TimezoneModule(habits)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingOnboardingModule(HabitService habits, BotReplies replies) : OnboardingModule(habits)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingExportModule(HabitService habits, AccountService accounts, BotReplies replies) : ExportModule(habits, accounts, BotHandlers.NoConfig)
{
    protected override Task DeferAsync(bool ephemeral = false, RequestOptions? options = null) => Task.CompletedTask;

    protected override Task<IUserMessage> FollowupAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.FromResult<IUserMessage>(null!);
    }

    protected override Task<IUserMessage> FollowupWithFileAsync(FileAttachment attachment, string? text = null, Embed[]? embeds = null,
        bool isTTS = false, bool ephemeral = false, AllowedMentions? allowedMentions = null, MessageComponent? components = null,
        Embed? embed = null, RequestOptions? options = null, PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        using var reader = new StreamReader(attachment.Stream);
        replies.Files.Add(new BotFile(attachment.FileName, reader.ReadToEnd()));
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.FromResult<IUserMessage>(null!);
    }
}

internal sealed class RecordingHabitModule(HabitService habits, BotReplies replies) : HabitModule(habits, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }

    protected override Task<IUserMessage> FollowupAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.FromResult<IUserMessage>(null!);
    }
}

internal sealed class RecordingReflectModule(HabitService habits, BotReplies replies) : ReflectDismissModule(habits, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingHistoryModule(HabitService habits, BotReplies replies) : HistoryModule(habits, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingDiscordRemindersModule(HabitService habits, BotReplies replies) : DiscordRemindersModule(habits)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingReminderSetupModule(HabitService habits, BotReplies replies) : ReminderSetupModule(habits)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}

internal sealed class RecordingCheckInModule(HabitService habits, BotReplies replies) : CheckInButtonModule(habits, TimeProvider.System, BotHandlers.NoConfig)
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }

    protected override Task<IUserMessage> FollowupAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.FromResult<IUserMessage>(null!);
    }
}

internal sealed class RecordingDmRetryModule(HabitService habits, BotReplies replies) : DmRetryModule(habits, new TestDiscordDmSender())
{
    protected override Task RespondAsync(string? text = null, Embed[]? embeds = null, bool isTTS = false, bool ephemeral = false,
        AllowedMentions? allowedMentions = null, RequestOptions? options = null, MessageComponent? components = null, Embed? embed = null,
        PollProperties? poll = null, MessageFlags flags = MessageFlags.None)
    {
        replies.Sent.Add(new BotReply(text, embed, components, ephemeral));
        return Task.CompletedTask;
    }
}
