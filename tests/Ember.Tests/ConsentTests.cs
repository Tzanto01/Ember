using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class ConsentTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    private static async Task SeedExistingUserAsync(IntegrationTestHost host, long userId, string? policyVersion = null)
    {
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User
            {
                Id = TestUsers.Id(userId), Logins = TestUsers.Discord(userId),
                Timezone = "Pacific/Auckland",
                HealthDataConsentAt = policyVersion is null ? null : new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                ConsentPolicyVersion = policyVersion,
            };
            user.Habits.Add(new Habit { Name = "Walk", CreatedAt = Now.UtcDateTime.AddDays(-30) });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
    }

    private static Task<bool> ClaimAsync(IntegrationTestHost host, ulong userId)
        => host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().ClaimConsentPromptAsync(await TestUsers.UserIdAsync(sp, userId)));

    [Fact]
    public async Task AddHabit_WithoutConsent_IsRefusedAndStoresNothing()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

        var (added, _) = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().AddHabitAsync(await TestUsers.UserIdAsync(sp, 1), "Take meds", null));

        added.Should().Be(HabitWriteResult.ConsentRequired);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task AgreeingByOwner_StoresTimestampAndPolicyVersion_AndUnlocksHabits()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

        var answered = await host.WithScopeAsync(sp =>
            ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: true));
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().AddHabitAsync(await TestUsers.UserIdAsync(sp, 1), "Take meds", null));

        answered.Should().BeTrue();
        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.Include(u => u.Habits).SingleAsync());
        user.HealthDataConsentAt.Should().Be(Now.UtcDateTime);
        user.ConsentPolicyVersion.Should().Be(ConsentService.CurrentConsentPolicyVersion);
        user.Habits.Should().ContainSingle();
    }

    [Fact]
    public async Task DecliningOrSomeoneElsesClick_StoresNothing()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

        var declined = await host.WithScopeAsync(sp =>
            ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));
        var stranger = await host.WithScopeAsync(sp =>
            ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 2, agreed: true));

        declined.Should().BeTrue();
        stranger.Should().BeFalse();
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task ExistingUserWithoutConsent_IsPromptedOncePerLocalDay_AndNoLongerAfterAgreeing()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedExistingUserAsync(host, 1);

        (await ClaimAsync(host, 1)).Should().BeTrue();
        time.Advance(TimeSpan.FromHours(2));
        (await ClaimAsync(host, 1)).Should().BeFalse();

        time.Advance(TimeSpan.FromHours(12));
        (await ClaimAsync(host, 1)).Should().BeTrue("Auckland has reached the next local day");

        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, 1)));
        time.Advance(TimeSpan.FromDays(1));
        (await ClaimAsync(host, 1)).Should().BeFalse();
    }

    [Fact]
    public async Task ConsentForAnOlderPolicyVersion_DoesNotCount()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedExistingUserAsync(host, 1, policyVersion: "2020-01");

        (await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().HasHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1)))).Should().BeFalse();
        (await ClaimAsync(host, 1)).Should().BeTrue();
    }

    [Fact]
    public async Task ReleasingTodaysClaim_AfterTheFollowUpFailed_LetsTheNextInteractionAskAgain()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedExistingUserAsync(host, 1);

        (await ClaimAsync(host, 1)).Should().BeTrue();
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().ReleaseConsentPromptAsync(await TestUsers.UserIdAsync(sp, 1)));

        (await ClaimAsync(host, 1)).Should().BeTrue();
    }

    [Fact]
    public async Task UserWithoutHabits_IsNotPromptedAfterInteractions()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<UserSettingsService>().SetGraceDaysAsync(await TestUsers.ResolveOrCreateAsync(sp, 1), 2));

        (await ClaimAsync(host, 1)).Should().BeFalse();
    }

    [Fact]
    public async Task NoThanks_KeepsTheAutomaticPromptAwayFor30Days()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedExistingUserAsync(host, 1);
        (await ClaimAsync(host, 1)).Should().BeTrue();

        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));

        time.Advance(TimeSpan.FromDays(1));
        (await ClaimAsync(host, 1)).Should().BeFalse();
        time.Advance(TimeSpan.FromDays(28));
        (await ClaimAsync(host, 1)).Should().BeFalse();
        time.Advance(TimeSpan.FromDays(1));
        (await ClaimAsync(host, 1)).Should().BeTrue();
    }

    private static Task<User> UserAsync(IntegrationTestHost host, long discordId)
        => host.WithScopeAsync(async sp =>
        {
            var id = await sp.GetRequiredService<HabitService>().FindDiscordUserAsync((ulong)discordId);
            return await sp.GetRequiredService<EmberDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == id);
        });

    private static async Task SeedConsentedUserWithDueRemindersAsync(IntegrationTestHost host, long userId)
    {
        await SeedExistingUserAsync(host, userId, ConsentService.CurrentConsentPolicyVersion);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var due = Now.UtcDateTime.AddMinutes(-1);
            await db.Habits.Where(h => h.UserId == TestUsers.Id(userId))
                .ExecuteUpdateAsync(x => x.SetProperty(h => h.NextReminderUtc, due).SetProperty(h => h.SecondReminderUtc, due));
            db.Routines.Add(new Routine { UserId = TestUsers.Id(userId), Name = "Morning", NextReminderUtc = due, SecondReminderUtc = due });
            await db.SaveChangesAsync();
        });
    }

    [Fact]
    public async Task Withdrawing_BlocksEveryGatedWrite_AndStopsAllDueAndSecondReminders()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedConsentedUserWithDueRemindersAsync(host, 1);
        await SeedConsentedUserWithDueRemindersAsync(host, 2);

        var withdrawn = await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)));
        var again = await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)));

        withdrawn.Should().BeTrue();
        again.Should().BeFalse("it was already withdrawn");
        (await UserAsync(host, 1)).ConsentWithdrawnAt.Should().Be(Now.UtcDateTime);
        var (added, _) = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().AddHabitAsync(await TestUsers.UserIdAsync(sp, 1), "Take meds", null));
        added.Should().Be(HabitWriteResult.ConsentRequired);
        (await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().SetLowDayAsync(await TestUsers.UserIdAsync(sp, 1), new DateOnly(2027, 1, 15))))
            .Should().Be(DayEditResult.ConsentRequired);

        var (habits, routines, secondHabits, secondRoutines, owners) = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<ReminderService>();
            var db = sp.GetRequiredService<EmberDbContext>();
            var now = Now.UtcDateTime;
            var h = (await service.GetDueReminderHabitsAsync(now)).Select(x => x.Id).ToList();
            var r = (await service.GetDueReminderRoutinesAsync(now)).Select(x => x.Id).ToList();
            var sh = await service.GetDueSecondReminderHabitIdsAsync(now);
            var sr = await service.GetDueSecondReminderRoutineIdsAsync(now);
            var all = h.Concat(sh).ToList();
            var habitOwners = await db.Habits.Where(x => all.Contains(x.Id)).Select(x => x.UserId).Distinct().ToListAsync();
            var routineIds = r.Concat(sr).ToList();
            var routineOwners = await db.Routines.Where(x => routineIds.Contains(x.Id)).Select(x => x.UserId).Distinct().ToListAsync();
            return (h, r, sh, sr, habitOwners.Concat(routineOwners).Distinct().ToList());
        });
        (habits.Count, routines.Count, secondHabits.Count, secondRoutines.Count).Should().Be((1, 1, 1, 1), "only user 2 still gets reminders");
        owners.Should().Equal(TestUsers.Id(2));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetUserTimezonesWithHabitsAsync()))
            .Select(u => u.UserId).Should().Equal(TestUsers.Id(2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CheckInsAndSlips_AreRefusedWithoutCurrentConsent_WhileClearingStaysAllowed(bool withdrawn)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedExistingUserAsync(host, 1, withdrawn ? ConsentService.CurrentConsentPolicyVersion : "2020-01");
        var day = new DateOnly(2027, 1, 14);
        var (buildId, letGoId) = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var build = await db.Habits.SingleAsync();
            build.Logs.Add(new HabitLog { Date = day.AddDays(-1), Completed = true, Count = 1 });
            var letGo = new Habit { UserId = build.UserId, Name = "Smoking", Kind = HabitKind.LetGo, CreatedAt = build.CreatedAt };
            db.Habits.Add(letGo);
            await db.SaveChangesAsync();
            return (build.Id, letGo.Id);
        });
        if (withdrawn) await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)));

        var results = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            return (await service.AddCheckInAsync(await TestUsers.UserIdAsync(sp, 1), buildId, day), await service.CheckInAsync(await TestUsers.UserIdAsync(sp, 1), buildId, day, true),
                await service.AddSlipAsync(await TestUsers.UserIdAsync(sp, 1), letGoId, day), await service.ClearDayAsync(await TestUsers.UserIdAsync(sp, 1), buildId, day.AddDays(-1)));
        });

        results.Should().Be((DayEditResult.ConsentRequired, DayEditResult.ConsentRequired, SlipResult.ConsentRequired, DayEditResult.Saved));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitLogs.CountAsync())).Should().Be(0);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitSlips.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Renaming_AHabit_NeedsConsent_ButItsRemindersCanStillChange()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedConsentedUserWithDueRemindersAsync(host, 1);
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)));
        var habitId = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.Select(h => h.Id).SingleAsync());

        var (renamed, _) = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().EditHabitAsync(await TestUsers.UserIdAsync(sp, 1UL), habitId, "Take meds", null, false));
        renamed.Should().Be(HabitWriteResult.ConsentRequired);
        var (_, edited) = await host.WithScopeAsync(async sp => await sp.GetRequiredService<HabitService>().EditHabitAsync(await TestUsers.UserIdAsync(sp, 1UL), habitId, null, new TimeOnly(7, 0), false));

        edited!.Name.Should().Be("Walk");
        edited.ReminderTime.Should().Be(new TimeOnly(7, 0));
    }

    [Fact]
    public async Task NoThanks_WhenAlreadyWithdrawn_ChangesNothing_AndAnswersWithTheDeletionDay()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedExistingUserAsync(host, 1);
        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));
        var before = await UserAsync(host, 1);
        time.Advance(TimeSpan.FromDays(28));

        var withdrawal = await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().GetConsentWithdrawalAsync(await TestUsers.UserIdAsync(sp, 1UL)));
        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));

        var after = await UserAsync(host, 1);
        (after.ConsentWithdrawnAt, after.ConsentPromptedOn).Should().Be((before.ConsentWithdrawnAt, before.ConsentPromptedOn));
        ConsentModule.DeclinedText(Ember.Bot.BotTexts.English, withdrawal, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"))
            .Should().Contain("February 14, 2027", "30 days after the withdrawal at midnight UTC is the 14th in Auckland");
        ConsentModule.DeclinedText(Ember.Bot.BotTexts.English, null, TimeZoneInfo.Utc).Should().Be(Ember.Bot.BotTexts.English.Get("consent.declined"));
    }

    [Fact]
    public async Task NoThanks_ByAUserWithHabits_WithdrawsAndHoldsBackThePrompt_ButNotForSomeoneWithNothingStored()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedExistingUserAsync(host, 1);
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<UserSettingsService>().SetGraceDaysAsync(await TestUsers.ResolveOrCreateAsync(sp, 2), 2));

        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));
        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "2", clickerId: 2, agreed: false));

        var withData = await UserAsync(host, 1);
        withData.ConsentWithdrawnAt.Should().Be(Now.UtcDateTime);
        withData.ConsentPromptedOn.Should().Be(new DateOnly(2027, 1, 15).AddDays(ConsentService.ConsentDeclineBackoffDays - 1));
        var empty = await UserAsync(host, 2);
        empty.ConsentWithdrawnAt.Should().BeNull();
        empty.ConsentPromptedOn.Should().NotBeNull("the back-off still applies");
    }

    [Fact]
    public async Task AgreeingAgain_ClearsTheWithdrawalAndTheDeletionReminderClaim_AndUnlocksWrites()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedConsentedUserWithDueRemindersAsync(host, 1);
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)));
        time.Advance(TimeSpan.FromDays(27));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<ConsentService>().ClaimDeletionReminderAsync(TestUsers.Id(1)))).Should().NotBeNull();

        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, 1UL)));

        var user = await UserAsync(host, 1);
        (user.ConsentWithdrawnAt, user.DeletionReminderSentAt).Should().Be(((DateTime?)null, (DateTime?)null));
        (await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().HasHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, 1UL)))).Should().BeTrue();
        (await host.WithScopeAsync(sp => sp.GetRequiredService<ReminderService>().GetDueReminderHabitsAsync(time.GetUtcNow().UtcDateTime)))
            .Should().ContainSingle();
    }

    [Fact]
    public void Prompt_NamesEveryKindOfHealthDataEmberStores()
    {
        var embed = ConsentModule.PromptEmbed(Ember.Bot.BotTexts.English, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        embed.Description.Should().ContainEquivalentOf(
            "your habit names, check-ins, low days, day notes, habit notes, moods, slips with the reasons you give, and your \"What helps me\" list");
    }

    [Fact]
    public async Task AskingAgainFromAddHabit_AfterNoThanks_KeepsTheBackOff()
    {
        var time = new FakeTimeProvider(Now);
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        await SeedExistingUserAsync(host, 1);
        await host.WithScopeAsync(sp => ConsentModule.TryAnswerAsync(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ConsentService>(), "1", clickerId: 1, agreed: false));

        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().MarkConsentPromptedAsync(await TestUsers.UserIdAsync(sp, 1)));
        time.Advance(TimeSpan.FromDays(1));

        (await ClaimAsync(host, 1)).Should().BeFalse();
    }
}
