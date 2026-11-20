using System.Collections.Concurrent;
using System.Reflection;
using Discord;

namespace Ember.Tests;

/// <summary>A follow-up a <see cref="FakeInteraction"/> received.</summary>
public sealed record FakeFollowup(string? Text, Embed? Embed, MessageComponent? Components, bool Ephemeral);

/// <summary>
/// A Discord interaction built from interface proxies, for driving Discord.Net's <c>InteractionService</c> without a gateway: an
/// answered button press or modal submit by <c>userId</c> with a custom id. It records follow-ups; every other member returns a default.
/// </summary>
public class FakeInteraction : DispatchProxy
{
    private Dictionary<string, object?> _values = [];
    private ConcurrentQueue<FakeFollowup>? _followups;

    public static IComponentInteraction Component(ulong userId, string customId, ConcurrentQueue<FakeFollowup>? followups = null)
    {
        var data = Proxy<IComponentInteractionData>(new() { ["get_CustomId"] = customId, ["get_Type"] = ComponentType.Button });
        return Interaction<IComponentInteraction>(userId, InteractionType.MessageComponent, data, followups);
    }

    public static IModalInteraction Modal(ulong userId, string customId)
    {
        var data = Proxy<IModalInteractionData>(new() { ["get_CustomId"] = customId });
        return Interaction<IModalInteraction>(userId, InteractionType.ModalSubmit, data, null);
    }

    private static T Interaction<T>(ulong userId, InteractionType type, object data, ConcurrentQueue<FakeFollowup>? followups) where T : class
    {
        var interaction = Proxy<T>(new()
        {
            ["get_User"] = Proxy<IUser>(new() { ["get_Id"] = userId }),
            ["get_Data"] = data,
            ["get_Type"] = type,
            ["get_UserLocale"] = "en-US",
            ["get_HasResponded"] = true,
        });
        ((FakeInteraction)(object)interaction)._followups = followups;
        return interaction;
    }

    private static T Proxy<T>(Dictionary<string, object?> values) where T : class
    {
        var proxy = Create<T, FakeInteraction>();
        ((FakeInteraction)(object)proxy)._values = values;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var method = targetMethod!;
        if (method.Name == nameof(IDiscordInteraction.FollowupAsync) && _followups is not null)
        {
            var parameters = method.GetParameters();
            object? Arg(string name) => args![Array.FindIndex(parameters, p => p.Name == name)];
            _followups.Enqueue(new FakeFollowup((string?)Arg("text"), (Embed?)Arg("embed"), (MessageComponent?)Arg("components"), (bool)Arg("ephemeral")!));
            return Task.FromResult<IUserMessage>(null!);
        }

        if (_values.TryGetValue(method.Name, out var value))
            return value;

        var type = method.ReturnType;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type.GetGenericArguments()[0]).Invoke(null, [null]);
        return type.IsValueType && type != typeof(void) ? Activator.CreateInstance(type) : null;
    }
}

/// <summary>The context of a <see cref="FakeInteraction"/>: no guild, channel or client.</summary>
public sealed class FakeInteractionContext(IDiscordInteraction interaction) : IInteractionContext
{
    public IDiscordClient Client => null!;
    public IGuild Guild => null!;
    public IMessageChannel Channel => null!;
    public IUser User => interaction.User;
    public IDiscordInteraction Interaction => interaction;
}
