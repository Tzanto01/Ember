using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
using Ember.Bot.Jobs;
using Ember.Bot.Modules;
using Ember.Bot.Services;
using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Quartz;

namespace Ember.Tests;

/// <summary>
/// Custom IDs live on in Discord messages that were sent long ago: every ID the bot emits must reach a registered handler, and a handler
/// must refuse a click by someone else or an ID that was mangled without touching the owner's data.
/// </summary>
[Collection("Integration")]
public class BotRoutingTests
{
    private const ulong Owner = 77;
    private const ulong Stranger = 78;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly Texts English = BotTexts.English;

    private static IEnumerable<string> Ids(MessageComponent? components)
        => components?.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).Where(c => c is not ButtonComponent { IsDisabled: true }).OfType<IInteractableComponent>().Select(c => c.CustomId)
           ?? [];

    private static bool Matches(string pattern, string id)
        => System.Text.RegularExpressions.Regex.IsMatch(id, "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");

    private static async Task<(int BuildId, int LetGoId)> SeedAsync(IntegrationTestHost host, bool timezone = true)
        => await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await service.GiveHealthDataConsentAsync(Owner);
            if (timezone) await service.SetTimezoneAsync(Owner, "Europe/Berlin");
            var userId = await service.FindDiscordUserAsync(Owner);
            var build = await service.AddHabitAsync(userId, "Walk", new TimeOnly(9, 0));
            var letGo = await service.AddHabitAsync(userId, "Smoking", null, kind: HabitKind.LetGo);
            return (build.Id, letGo.Id);
        });

    private static Task<BotReplies> DriveAsync<TModule>(IntegrationTestHost host, Func<HabitService, BotReplies, TModule> create, Func<TModule, Task> call,
        ulong user = Owner, bool slash = false) where TModule : IInteractionModuleBase
        => host.WithScopeAsync(async sp =>
        {
            var replies = new BotReplies();
            var module = BotHandlers.Attach(create(sp.GetRequiredService<HabitService>(), replies), user, slash: slash);
            await BotHandlers.RunAsync(() => call(module));
            return replies;
        });

    private static async Task<List<string>> EmittedIdsAsync(IntegrationTestHost host, int buildId, int letGoId)
    {
        var ids = new List<string>();
        void Add(BotReplies replies) => ids.AddRange(replies.Sent.SelectMany(r => Ids(r.Components)));

        async Task Habit(Func<RecordingHabitModule, Task> call) => Add(await DriveAsync(host, (h, r) => new RecordingHabitModule(h, r), call, slash: true));
        await Habit(m => m.ListAsync());
        await Habit(m => m.CheckInAsync());
        await Habit(m => m.HistoryAsync());
        await Habit(m => m.StreakAsync());
        await Habit(m => m.ShareAsync());
        await Habit(m => m.DeleteAsync());
        await Habit(m => m.EditAsync());
        await Habit(m => m.FrequencyAsync());
        await Habit(m => m.PauseAsync());
        await Habit(m => m.GraceAsync());
        await Habit(m => m.TemplateAsync());

        Add(await DriveAsync(host, (h, r) => new RecordingHabitActionModule(h, r), m => m.OnAddModalAsync(new HabitAddModal { HabitName = "Read" })));
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GiveHealthDataConsentAsync(79UL));
        Add(await DriveAsync(host, (h, r) => new RecordingHabitActionModule(h, r), m => m.OnAddModalAsync(new HabitAddModal { HabitName = "Stretch" }), user: 79));
        Add(await DriveAsync(host, (h, r) => new RecordingFrequencyModule(h, r), m => m.ShowMenuAsync(buildId.ToString())));
        Add(await DriveAsync(host, (h, r) => new RecordingPrivacyModule(h, r), m => m.WithdrawAsync(), slash: true));
        Add(await DriveAsync(host, (h, r) => new RecordingPrivacyModule(h, r), m => m.DeleteAsync(), slash: true));
        Add(await DriveAsync(host, (h, r) => new RecordingOnboardingModule(h, r), m => m.OnRegionSelectAsync("1", ["moon"])));

        var t = English;
        var days = Enumerable.Range(0, 3).Select(i => new HabitDay(Today.AddDays(-i), DayState.NotDone, null, 0)).ToList();
        var habit = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetHabitAsync(Owner, letGoId));
        var statics = new[]
        {
            ConsentModule.PromptButtons(t, Owner), HabitSelectModule.FrequencyComponents(t, buildId), DmRetryModule.RetryButton(t, Owner),
            FiresideModule.NoticeButtons(t, 4, null), FiresideModule.NoticeButtons(t, 4, 9),
            DiscordRemindersModule.AskButtons(t, Owner), DiscordRemindersModule.TurnOnButton(t, Owner),
            HistoryModule.BuildGrid(t, Owner, buildId, days, new HashSet<DateOnly> { Today }),
            HistoryModule.BuildLetGoGrid(t, habit!, Today, TimeZoneInfo.Utc),
        };
        ids.AddRange(statics.SelectMany(Ids));

        var sender = new TestDiscordDmSender();
        var channel = new DiscordDmReminderChannel(sender, TimeProvider.System, new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DiscordDmReminderChannel>.Instance);
        var user = Guid.NewGuid();
        await channel.SendAsync(new Reminder(user, Owner, buildId, "Walk", 0, false, Today));
        await channel.SendAsync(new Reminder(user, Owner, buildId, "Walk", 0, true, Today, Second: true));
        await channel.SendAsync(new Reminder(user, Owner, letGoId, "Smoking", 0, false, Today, LetGo: true));
        await channel.SendAsync(new Reminder(user, Owner, 7, "Morning", 0, false, Today, [new RoutineHabit(1, "Water"), new RoutineHabit(2, "Stretch")]));
        ids.AddRange(sender.Messages.SelectMany(m => Ids(m.Components)));
        return ids;
    }

    [Fact]
    public async Task Every_custom_id_the_bot_emits_resolves_to_a_registered_component_handler()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, letGoId) = await SeedAsync(host);
        using var scope = host.Host.Services.CreateScope();
        using var interactions = new InteractionService(new DiscordSocketClient(), AppHost.InteractionConfig());
        await interactions.AddModulesAsync(typeof(AppHost).Assembly, scope.ServiceProvider);
        var patterns = interactions.ComponentCommands.Select(c => c.Name).ToList();

        var emitted = (await EmittedIdsAsync(host, buildId, letGoId)).Distinct().Order().ToList();

        emitted.Select(id => string.Join(':', id.Split(':').Take(2))).Distinct().Should().Contain(
            ["checkin:done", "checkin:skip", "checkin:snooze", "discordreminders:stop", "discordreminders:yes", "dmretry:77", "consent:agree",
             "fireside:ok", "fireside:back", "freq:menu", "freq:weekly", "habit:select", "habit:grace", "habit:streaks", "history:day", "history:low", "letgo:slip", "onboard:timezone", "privacy:withdraw",
             "privacy:delete", "reminder:set", "reminder:skip"], "the render must cover every button family it can reach");
        emitted.Where(id => !patterns.Any(p => Matches(p, id))).Should().BeEmpty("every emitted custom id needs a handler");
    }

    [Fact]
    public async Task A_reflection_dm_carries_only_ids_a_handler_serves()
    {
        var hour = new DateTimeOffset(2026, 1, 14, 10, 0, 0, TimeSpan.Zero);
        await using var host = await IntegrationTestHost.CreateAsync(time: new FakeTimeProvider(hour));
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(1001), Logins = TestUsers.Discord(1001), Timezone = "UTC" };
            user.Habits.Add(new Habit { Name = "Stretch", CreatedAt = hour.UtcDateTime.AddDays(-5) });
            db.Users.Add(user);
            await db.SaveChangesAsync();
        });
        using var scope = host.Host.Services.CreateScope();
        using var interactions = new InteractionService(new DiscordSocketClient(), AppHost.InteractionConfig());
        await interactions.AddModulesAsync(typeof(AppHost).Assembly, scope.ServiceProvider);

        var scheduler = await host.WithScopeAsync(sp => sp.GetRequiredService<ISchedulerFactory>().GetScheduler());
        await scheduler.TriggerJob(MissedDayReflectionJob.Key);
        await host.WaitForDmCountAsync(1);

        var ids = Ids(host.Discord.Messages.Single().Components).ToList();
        ids.Should().Contain(id => id.StartsWith("reflect:log:"));
        ids.Where(id => !interactions.ComponentCommands.Any(c => Matches(c.Name, id))).Should().BeEmpty();
    }

    private static Task<string> StateAsync(IntegrationTestHost host)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var habits = await db.Habits.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
            return string.Join(";", habits.Select(h => $"{h.Id}:{h.Name}:{h.FrequencyType}:{h.WeeklyTarget}:{h.Days}:{h.TimesPerDay}:{h.PausedFrom}:{h.PausedUntil}:{h.ReminderTime}"))
                   + $"|logs={await db.HabitLogs.CountAsync()}|slips={await db.HabitSlips.CountAsync()}|marks={await db.DayMarks.CountAsync()}"
                   + $"|user={string.Join(",", (await db.Users.AsNoTracking().ToListAsync()).Select(u => $"{u.ConsentWithdrawnAt}:{u.DiscordReminders}:{u.GraceDaysPerWeek}"))}";
        });

    [Fact]
    public async Task A_click_by_someone_else_on_a_habit_button_changes_nothing()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, letGoId) = await SeedAsync(host);
        var before = await StateAsync(host);
        var b = buildId.ToString();

        await DriveAsync(host, (h, r) => new RecordingHabitActionModule(h, r), async m =>
        {
            await m.OnCheckinDoneAsync(b);
            await m.OnCheckinSkipAsync(b);
            await m.OnSlipAsync(letGoId.ToString());
            await m.OnListSlipAsync(letGoId.ToString());
            await m.OnDeleteConfirmAsync(b);
            await m.OnPauseAsync(b, "1week");
            await m.OnFreqDailyAsync(b, "daily");
        }, user: Stranger);
        await DriveAsync(host, (h, r) => new RecordingFrequencyModule(h, r), async m =>
        {
            await m.SetWeeklyTargetAsync(b, "3");
            await m.SetWeekdaysAsync(b, ["mon"]);
            await m.SetTimesAsync(b, ["4"]);
        }, user: Stranger);
        await DriveAsync(host, (h, r) => new RecordingReminderSetupModule(h, r), async m =>
        {
            await m.OnClearReminderAsync(b);
        }, user: Stranger);

        (await StateAsync(host)).Should().Be(before);
    }

    [Fact]
    public async Task A_malformed_id_on_a_habit_button_changes_nothing_and_asks_to_retry()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        await SeedAsync(host);
        var before = await StateAsync(host);

        var action = await DriveAsync(host, (h, r) => new RecordingHabitActionModule(h, r), async m =>
        {
            await m.OnCheckinDoneAsync("abc");
            await m.OnCheckinSkipAsync("abc");
            await m.OnSlipAsync("abc");
            await m.OnDeleteConfirmAsync("abc");
            await m.OnPauseAsync("abc", "1week");
            await m.OnFreqDailyAsync("abc", "daily");
            await m.OnFreqDailyAsync("1", "weekly");
            await m.OnStreaksAsync("maybe");
            await m.OnGraceAsync("abc");
        });
        var frequency = await DriveAsync(host, (h, r) => new RecordingFrequencyModule(h, r), async m =>
        {
            await m.SetWeeklyTargetAsync("abc", "3");
            await m.SetWeeklyTargetAsync("1", "abc");
            await m.SetWeekdaysAsync("abc", ["mon"]);
            await m.SetTimesAsync("1", ["abc"]);
            await m.ShowMenuAsync("abc");
        });
        var checkIn = await DriveAsync(host, (h, r) => new RecordingCheckInModule(h, r), async m =>
        {
            await m.OnDoneAsync("abc", "100");
            await m.OnSkipAsync("abc", "100");
        });

        (await StateAsync(host)).Should().Be(before);
        action.Sent.Should().HaveCount(9).And.OnlyContain(r => r.Text != null);
        frequency.Sent.Should().HaveCount(5).And.OnlyContain(r => r.Text == English.Get("freq.retry"));
        checkIn.Sent.Should().HaveCount(2).And.OnlyContain(r => r.Text == English.Get("checkin.button-error"));
    }

    [Fact]
    public async Task Owner_bound_buttons_refuse_someone_elses_click_and_a_mangled_owner_id()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, _) = await SeedAsync(host);
        var before = await StateAsync(host);
        var owner = Owner.ToString();
        var date = "20270115";
        var refused = new List<string?>();

        foreach (var user in new[] { Stranger, Owner })
        {
            var id = user == Stranger ? owner : "abc";
            refused.AddRange((await DriveAsync(host, (h, r) => new RecordingPrivacyModule(h, r), async m =>
            {
                await m.OnConfirmWithdrawAsync(id);
                await m.OnCancelWithdrawAsync(id);
                await m.OnStartDeleteAsync(id);
                await m.OnConfirmDeleteAsync(id);
                await m.OnCancelDeleteAsync(id);
            }, user: user)).Sent.Select(r => r.Text));
            refused.AddRange((await DriveAsync(host, (h, r) => new RecordingDiscordRemindersModule(h, r), async m =>
            {
                await m.OnYesAsync(id);
                await m.OnNoAsync(id);
                await m.OnStopAsync(id);
            }, user: user)).Sent.Select(r => r.Text));
            refused.AddRange((await DriveAsync(host, (h, r) => new RecordingReflectModule(h, r), async m =>
            {
                await m.LogAsync(id, date);
                await m.LowDayAsync(id, date);
                await m.PickAsync(id, date, [buildId.ToString()]);
            }, user: user)).Sent.Select(r => r.Text));
            refused.AddRange((await DriveAsync(host, (h, r) => new RecordingHistoryModule(h, r), async m =>
            {
                await m.OnDayAsync(id, buildId.ToString(), date);
                await m.OnLowDayAsync(id, buildId.ToString(), [date]);
            }, user: user)).Sent.Select(r => r.Text));
            refused.AddRange((await DriveAsync(host, (h, r) => new RecordingDmRetryModule(h, r), m => m.OnRetryAsync(id), user: user)).Sent.Select(r => r.Text));
        }

        (await StateAsync(host)).Should().Be(before);
        refused.Should().HaveCount(2 * (5 + 3 + 3 + 2 + 1)).And.OnlyContain(t => t == English.Get("common.not-yours") || t == English.Get("common.retry.comma"));
    }

    [Fact]
    public async Task A_consent_answer_by_someone_else_or_with_a_mangled_owner_id_stores_nothing()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

        var answers = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            return new[]
            {
                await ConsentModule.TryAnswerAsync(service, Owner.ToString(), Stranger, agreed: true),
                await ConsentModule.TryAnswerAsync(service, "abc", Owner, agreed: true),
                await ConsentModule.TryAnswerAsync(service, "", Owner, agreed: false),
            };
        });

        answers.Should().Equal(false, false, false);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }
}
