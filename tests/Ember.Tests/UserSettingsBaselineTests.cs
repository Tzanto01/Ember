using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class UserSettingsBaselineTests
{
    private static readonly Guid UserId = TestUsers.Id(31);

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    private static async Task SeedUserAsync(IntegrationTestHost host, string timezone, bool withHabit = false)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = UserId, Logins = TestUsers.Discord(31), Timezone = timezone };
            if (withHabit) user.Habits.Add(new Habit { Name = "Walk", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });

    private static Task<T> InServiceAsync<T>(IntegrationTestHost host, Func<UserSettingsService, Task<T>> action)
        => host.WithScopeAsync(sp => action(sp.GetRequiredService<UserSettingsService>()));

    public static TheoryData<string, int, int, int, int, int, int, int, int> LocalDays => new()
    {
        { "Asia/Kolkata", 2027, 1, 14, 18, 29, 59, 1, 14 },
        { "Asia/Kolkata", 2027, 1, 14, 18, 30, 0, 1, 15 },
    };

    [Theory]
    [MemberData(nameof(LocalDays))]
    public async Task LocalToday_FollowsTheStoredZone(
        string zone, int year, int month, int day, int hour, int minute, int second, int expectedMonth, int expectedDay)
    {
        var instant = Utc(year, month, day, hour, minute, second);
        var expectedYear = month == 12 && expectedMonth == 1 ? year + 1 : year;
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(instant));
        await SeedUserAsync(host, zone);

        (await InServiceAsync(host, s => s.GetLocalTodayAsync(UserId))).Should().Be(new DateOnly(expectedYear, expectedMonth, expectedDay));
    }

    [Theory]
    [InlineData("Mars/Olympus")]
    [InlineData("")]
    public async Task LocalToday_FallsBackToTheUtcDayForAnUnknownStoredZone(string zone)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Utc(2027, 1, 14, 23, 30, 0)));
        await SeedUserAsync(host, zone);

        (await InServiceAsync(host, s => s.GetLocalTodayAsync(UserId))).Should().Be(new DateOnly(2027, 1, 14));
    }

    [Fact]
    public async Task LocalToday_FallsBackToTheUtcDayForAMissingUser()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Utc(2027, 1, 14, 23, 30, 0)));

        (await InServiceAsync(host, s => s.GetLocalTodayAsync(Guid.NewGuid()))).Should().Be(new DateOnly(2027, 1, 14));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DecliningTheConsentPrompt_HoldsItBackForThirtyLocalDaysCountedFromTheUsersLocalDay(bool holdsHabits)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Utc(2027, 1, 14, 18, 40, 0)));
        await SeedUserAsync(host, "Asia/Kolkata", withHabit: holdsHabits);

        await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().DeclineConsentPromptAsync(UserId));

        var promptedOn = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.Where(u => u.Id == UserId).Select(u => u.ConsentPromptedOn).SingleAsync());
        promptedOn.Should().Be(new DateOnly(2027, 1, 15).AddDays(ConsentService.ConsentDeclineBackoffDays - 1));
    }

    [Fact]
    public async Task WithdrawingConsent_HoldsTheConsentPromptBackFromTheUsersLocalDay()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Utc(2027, 1, 15, 3, 40, 0)));
        await SeedUserAsync(host, "America/St_Johns", withHabit: true);
        await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(UserId));

        (await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(UserId))).Should().BeTrue();

        var promptedOn = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.Where(u => u.Id == UserId).Select(u => u.ConsentPromptedOn).SingleAsync());
        promptedOn.Should().Be(new DateOnly(2027, 1, 15).AddDays(ConsentService.ConsentDeclineBackoffDays - 1));
    }

    [Fact]
    public async Task MarkingTheConsentPromptShown_RecordsTheUsersLocalDayNotTheUtcDay()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Utc(2027, 1, 14, 18, 40, 0)));
        await SeedUserAsync(host, "Asia/Kolkata", withHabit: true);

        await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().MarkConsentPromptedAsync(UserId));

        var promptedOn = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.Where(u => u.Id == UserId).Select(u => u.ConsentPromptedOn).SingleAsync());
        promptedOn.Should().Be(new DateOnly(2027, 1, 15));
    }

    [Fact]
    public async Task Language_IsEnglishForAnUnknownUser_AndSettingItCreatesNoUser()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<UserSettingsService>();
            await service.SetLanguageAsync(UserId, "de");
            (await service.GetLanguageAsync(UserId)).Should().Be("en");
        });

        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Language_ReturnsWhatWasSetForTheUser()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedUserAsync(host, "UTC");

        (await InServiceAsync(host, s => s.GetLanguageAsync(UserId))).Should().Be("en");
        await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().SetLanguageAsync(UserId, "de"));

        (await InServiceAsync(host, s => s.GetLanguageAsync(UserId))).Should().Be("de");
    }

    [Fact]
    public async Task ShowStreaks_IsOffWithoutAnAccount_AndNeverAsksTheStreaksQuestion()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            var settings = sp.GetRequiredService<UserSettingsService>();
            (await settings.GetShowStreaksAsync(UserId)).Should().BeFalse();
            (await sp.GetRequiredService<UserSettingsService>().GetShowStreaksAsync(await TestUsers.UserIdAsync(sp, 77UL))).Should().BeFalse();
            (await settings.NeedsStreaksQuestionAsync(UserId)).Should().BeFalse();
            (await sp.GetRequiredService<UserSettingsService>().NeedsStreaksQuestionAsync(await TestUsers.UserIdAsync(sp, 77UL))).Should().BeFalse();
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SettingShowStreaksForANewDiscordUser_CreatesTheUserStoresTheChoiceAndMarksTheStreaksTipSeen(bool show)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        await host.WithScopeAsync(async sp => await sp.GetRequiredService<UserSettingsService>().SetShowStreaksAsync(await TestUsers.ResolveOrCreateAsync(sp, 78UL), show));

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            var id = await service.FindDiscordUserAsync(78);
            id.Should().NotBe(Guid.Empty);
            (await sp.GetRequiredService<UserSettingsService>().GetShowStreaksAsync(await TestUsers.UserIdAsync(sp, 78UL))).Should().Be(show);
            (await sp.GetRequiredService<UserSettingsService>().NeedsStreaksQuestionAsync(await TestUsers.UserIdAsync(sp, 78UL))).Should().BeFalse();
            (await sp.GetRequiredService<EmberDbContext>().Users.Where(u => u.Id == id).Select(u => u.TipsSeen).SingleAsync())
                .Should().Contain(User.StreaksTip);
        });
    }

    [Fact]
    public async Task NeedsStreaksQuestion_IsTrueForAFreshAccountAndFalseOnceStreaksAreOnOrTheTipIsSeen()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        await SeedUserAsync(host, "UTC");

        (await InServiceAsync(host, s => s.NeedsStreaksQuestionAsync(UserId))).Should().BeTrue();
        await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().MarkTipSeenAsync(UserId, User.StreaksTip));

        (await InServiceAsync(host, s => s.NeedsStreaksQuestionAsync(UserId))).Should().BeFalse();
    }

    [Fact]
    public async Task HasTimezoneSet_IsFalseForNoUserAndForUtc_AndTrueOnceARealZoneIsStored()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        (await InServiceAsync(host, s => s.HasTimezoneSetAsync(UserId))).Should().BeFalse();
        await SeedUserAsync(host, "UTC");
        (await InServiceAsync(host, s => s.HasTimezoneSetAsync(UserId))).Should().BeFalse();

        await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().SetTimezoneAsync(UserId, "Europe/Berlin"));
        (await InServiceAsync(host, s => s.HasTimezoneSetAsync(UserId))).Should().BeTrue();

        await host.WithScopeAsync(sp => sp.GetRequiredService<UserSettingsService>().SetTimezoneAsync(UserId, "UTC"));
        (await InServiceAsync(host, s => s.HasTimezoneSetAsync(UserId))).Should().BeFalse();
    }

    [Fact]
    public async Task SettingTimezoneOrGraceDaysForANewDiscordUser_CreatesTheUserWithTheValue()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<UserSettingsService>().SetTimezoneAsync(await TestUsers.ResolveOrCreateAsync(sp, 79UL), "Asia/Tokyo");
            await sp.GetRequiredService<UserSettingsService>().SetGraceDaysAsync(await TestUsers.ResolveOrCreateAsync(sp, 80UL), 3);
        });

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            (await sp.GetRequiredService<UserSettingsService>().HasTimezoneSetAsync(await TestUsers.UserIdAsync(sp, 79UL))).Should().BeTrue();
            (await sp.GetRequiredService<UserSettingsService>().GetGraceDaysAsync(await TestUsers.UserIdAsync(sp, 80UL))).Should().Be(3);
        });
    }
}
