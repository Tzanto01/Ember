using System.Reflection;
using Discord;
using Ember.Bot;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ember.Tests;

public class BotServiceTests
{
    [Fact]
    public async Task Status_without_an_active_notice_is_the_habit_count()
    {
        var (text, activity) = await BotService.StatusAsync(null, () => Task.FromResult(3));

        text.Should().Be("3 habits tracked 🔥");
        activity.Should().BeOfType<Game>().Which.Type.Should().Be(ActivityType.Watching);
        activity.Name.Should().Be(text);
    }

    [Fact]
    public async Task Status_with_an_active_notice_is_its_text_as_custom_status_without_counting_habits()
    {
        var notice = new Ember.Bot.Services.Notice("a", null, null, new string('x', 200));

        var (text, activity) = await BotService.StatusAsync(notice, () => throw new InvalidOperationException("Not counted"));

        text.Should().Be(new string('x', 128));
        activity.Should().BeOfType<CustomStatusGame>().Which.State.Should().Be(text);
    }

    [Fact]
    public async Task StartClientAsync_SetsOnlineStatusBeforeLoginAndStart()
    {
        var calls = new List<string>();

        Task SetStatusAsync(UserStatus status)
        {
            calls.Add($"status:{status}");
            return Task.CompletedTask;
        }

        Task LoginAsync()
        {
            calls.Add("login");
            return Task.CompletedTask;
        }

        Task StartAsync()
        {
            calls.Add("start");
            return Task.CompletedTask;
        }

        await BotService.StartClientAsync(SetStatusAsync, LoginAsync, StartAsync);

        calls.Should().Equal("status:Online", "login", "start");
    }

    [Fact]
    public async Task CommandRegistration_RunsOnceAcrossRepeatedReady()
    {
        var registrations = 0;
        var once = new BotService.OncePerProcess(() => { registrations++; return Task.CompletedTask; });

        await once.RunAsync();
        await once.RunAsync();

        registrations.Should().Be(1);
    }

    [Fact]
    public async Task CommandRegistration_RetriesOnNextReadyAfterFailure()
    {
        var attempts = 0;
        var once = new BotService.OncePerProcess(() =>
            ++attempts == 1 ? throw new HttpRequestException("gateway hiccup") : Task.CompletedTask);

        await once.Invoking(o => o.RunAsync()).Should().ThrowAsync<HttpRequestException>();
        await once.RunAsync();
        await once.RunAsync();

        attempts.Should().Be(2);
    }

    [Theory]
    [InlineData(true, "FollowupAsync")]
    [InlineData(false, "RespondAsync")]
    public async Task ReplyWithError_UsesFollowupOnlyWhenAlreadyResponded(bool hasResponded, string expected)
    {
        var interaction = FakeInteraction.Create(hasResponded, failReplies: false);

        await BotService.ReplyWithErrorAsync(interaction, "oops", NullLogger.Instance);

        ((FakeInteraction)(object)interaction).Calls.Should().Equal(expected);
    }

    [Fact]
    public async Task ReplyWithError_DoesNotThrowWhenTheReplyFails()
    {
        var interaction = FakeInteraction.Create(hasResponded: true, failReplies: true);

        var reply = () => BotService.ReplyWithErrorAsync(interaction, "oops", NullLogger.Instance);

        await reply.Should().NotThrowAsync();
    }

    public class FakeInteraction : DispatchProxy
    {
        public bool HasResponded { get; set; }
        public bool FailReplies { get; set; }
        public List<string> Calls { get; } = [];

        public static IDiscordInteraction Create(bool hasResponded, bool failReplies)
        {
            var proxy = DispatchProxy.Create<IDiscordInteraction, FakeInteraction>();
            var fake = (FakeInteraction)(object)proxy;
            fake.HasResponded = hasResponded;
            fake.FailReplies = failReplies;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_HasResponded": return HasResponded;
                case "get_Id": return 1UL;
                case "FollowupAsync":
                    Calls.Add(method.Name);
                    return FailReplies
                        ? Task.FromException<IUserMessage>(new InvalidOperationException("interaction expired"))
                        : Task.FromResult<IUserMessage>(null!);
                case "RespondAsync":
                    Calls.Add(method.Name);
                    return FailReplies
                        ? Task.FromException(new InvalidOperationException("interaction expired"))
                        : Task.CompletedTask;
                default: throw new NotSupportedException(method.Name);
            }
        }
    }
}
