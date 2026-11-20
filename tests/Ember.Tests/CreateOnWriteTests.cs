using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>
/// A Discord id Ember knows nothing about gets a bot-only user, in the Discord locale's language, on the first write a bot handler makes.
/// </summary>
[Collection("Integration")]
public class CreateOnWriteTests
{
    private const ulong Stranger = 4242;
    private const string German = "de";
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static Task<IntegrationTestHost> HostAsync() => IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

    private static Task<TResult> RunAsync<TModule, TResult>(IntegrationTestHost host, Func<IServiceProvider, BotReplies, TModule> create, Func<TModule, Task> call,
        Func<IServiceProvider, Task<TResult>> then, bool slash = false) where TModule : Discord.Interactions.IInteractionModuleBase
        => host.WithScopeAsync(async sp =>
        {
            var module = BotHandlers.Attach(create(sp, new BotReplies()), Stranger, German, slash);
            await BotHandlers.RunAsync(() => call(module));
            return await then(sp);
        });

    private static async Task<User> OnlyUserAsync(IntegrationTestHost host)
    {
        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.Include(u => u.Logins).AsNoTracking().SingleAsync());
        user.Logins.Should().ContainSingle().Which.Should().Match<Microsoft.AspNetCore.Identity.IdentityUserLogin<Guid>>(
            l => l.LoginProvider == DiscordLogin.Provider && l.ProviderKey == DiscordLogin.Key(Stranger));
        user.Language.Should().Be(Languages.German);
        return user;
    }

    private static Task<object?> NothingAsync(IServiceProvider sp) => Task.FromResult<object?>(null);

    [Fact]
    public async Task Grace_button_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_days()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingHabitActionModule(sp.GetRequiredService<HabitService>(), r), m => m.OnGraceAsync("3"), NothingAsync);

        (await OnlyUserAsync(host)).GraceDaysPerWeek.Should().Be(3);
    }

    [Fact]
    public async Task Streaks_button_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_choice()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingHabitActionModule(sp.GetRequiredService<HabitService>(), r), m => m.OnStreaksAsync("on"), NothingAsync);

        (await OnlyUserAsync(host)).ShowStreaks.Should().BeTrue();
    }

    [Fact]
    public async Task Streaks_tip_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_choice()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingHabitActionModule(sp.GetRequiredService<HabitService>(), r), m => m.OnStreaksTipAsync("on"), NothingAsync);

        (await OnlyUserAsync(host)).ShowStreaks.Should().BeTrue();
    }

    [Fact]
    public async Task Timezone_set_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_zone()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingTimezoneModule(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ReminderService>(), r),
            m => m.SetAsync("Europe/Berlin"), NothingAsync, slash: true);

        (await OnlyUserAsync(host)).Timezone.Should().Be("Europe/Berlin");
    }

    [Fact]
    public async Task Onboarding_timezone_pick_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_zone()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingOnboardingModule(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ReminderService>(), r),
            m => m.OnTimezoneSelectAsync("1", ["Pacific/Auckland"]), NothingAsync);

        (await OnlyUserAsync(host)).Timezone.Should().Be("Pacific/Auckland");
    }

    [Fact]
    public async Task Consent_agree_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_records_the_consent()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, _) => new ConsentModule(sp.GetRequiredService<HabitService>()), m => m.OnAgreeAsync(Stranger.ToString()), NothingAsync);

        var user = await OnlyUserAsync(host);
        user.HealthDataConsentAt.Should().Be(Now.UtcDateTime);
        user.ConsentWithdrawnAt.Should().BeNull();
    }

    [Fact]
    public async Task Second_reminder_from_an_unknown_discord_user_creates_the_user_with_the_locale_language_and_stores_the_delay()
    {
        await using var host = await HostAsync();

        await RunAsync(host, (sp, r) => new RecordingHabitModule(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ReminderService>(), r),
            m => m.SecondReminderAsync(SecondReminderDelay.OneHour), NothingAsync, slash: true);

        (await OnlyUserAsync(host)).SecondReminderMinutes.Should().Be(60);
    }
}
