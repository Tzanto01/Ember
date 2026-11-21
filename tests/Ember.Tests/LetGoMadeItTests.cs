using Ember.Bot;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Bot.Discord.Messages;
using Ember.Bot.Modules;
using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>The "Made it today" tick of let-go habits: the handlers' persisted marks and answers, and the buttons and marks users see.</summary>
[Collection("Integration")]
public class LetGoMadeItTests
{
    private const ulong User = 77;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly Texts English = BotTexts.English;

    private static Task<IntegrationTestHost> HostAsync() => IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

    private static Task<int> SeedHabitAsync(IntegrationTestHost host, string name, HabitKind kind = HabitKind.LetGo)
        => host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, User));
            return (await service.AddHabitAsync(await service.FindDiscordUserAsync(User), name, null, kind: kind)).Habit!.Id;
        });

    private static Task<BotReplies> HabitActionAsync(IntegrationTestHost host, Func<RecordingHabitActionModule, Task> call, ulong user = User)
        => host.WithScopeAsync(async sp =>
        {
            var replies = new BotReplies();
            var module = BotHandlers.Attach(new RecordingHabitActionModule(sp, replies), user);
            await BotHandlers.RunAsync(() => call(module));
            return replies;
        });

    private static Task<List<MadeItDay>> MarksAsync(IntegrationTestHost host)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().MadeItDays.AsNoTracking().ToListAsync());

    private static Habit LetGoHabit(int id, Action<Habit>? configure = null)
    {
        var habit = new Habit { Id = id, Name = $"Habit {id}", Kind = HabitKind.LetGo, CreatedAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        configure?.Invoke(habit);
        return habit;
    }

    private static Habit Marked(int id) => LetGoHabit(id, h => h.MadeItDays.Add(new MadeItDay { HabitId = id, Date = Today }));

    private static Habit Slipped(int id) => LetGoHabit(id, h => h.Slips.Add(new HabitSlip { HabitId = id, Date = Today, Count = 1 }));

    private static Habit Paused(int id) => LetGoHabit(id, h => (h.PausedFrom, h.PausedUntil) = (Today, Today));

    private static LetGoDay TodayOf(Habit habit) => LetGoRules.DayOn(habit, Today, TimeZoneInfo.Utc);

    [Fact]
    public async Task Made_it_buttons_mark_the_local_today_and_answer_with_the_warm_line_and_an_undo_for_that_day()
    {
        await using var host = await HostAsync();
        var fromList = await SeedHabitAsync(host, "Smoking");
        var inPlace = await host.WithScopeAsync(async sp => (await sp.GetRequiredService<HabitService>()
            .AddHabitAsync(await TestUsers.UserIdAsync(sp, User), "Sugar", null, kind: HabitKind.LetGo)).Habit!.Id);

        var reply = await HabitActionAsync(host, m => m.OnListMadeItAsync(fromList.ToString()));
        await HabitActionAsync(host, m => m.OnMadeItAsync(inPlace.ToString()));

        var answer = reply.Sent.Should().ContainSingle().Subject;
        answer.Ephemeral.Should().BeTrue();
        answer.Text.Should().Be(English.Get("habit.letgo.made-it.ack"));
        BotHandlers.ButtonIds(answer.Components).Should().Equal(LetGoIds.UndoMadeIt(fromList, Today));
        (await MarksAsync(host)).Select(m => (m.HabitId, m.Date)).Should().BeEquivalentTo([(fromList, Today), (inPlace, Today)]);
    }

    [Fact]
    public async Task Made_it_on_a_build_habit_or_a_day_with_a_slip_writes_nothing_and_says_why()
    {
        await using var host = await HostAsync();
        var buildId = await SeedHabitAsync(host, "Walk", HabitKind.Build);
        var slippedId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            var userId = await service.FindDiscordUserAsync(User);
            var id = (await service.AddHabitAsync(userId, "Smoking", null, kind: HabitKind.LetGo)).Habit!.Id;
            await service.AddSlipAsync(userId, id, Today);
            return id;
        });

        var onBuild = await HabitActionAsync(host, m => m.OnListMadeItAsync(buildId.ToString()));
        var onSlipped = await HabitActionAsync(host, m => m.OnListMadeItAsync(slippedId.ToString()));

        onBuild.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-found"));
        onSlipped.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("habit.letgo.made-it.not-free"));
        (await MarksAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Made_it_without_health_data_consent_writes_nothing()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host, "Smoking");
        await host.WithScopeAsync(async sp => await sp.GetRequiredService<ConsentService>().WithdrawHealthDataConsentAsync(await TestUsers.UserIdAsync(sp, User)));

        await HabitActionAsync(host, m => m.OnListMadeItAsync(habitId.ToString()));

        (await MarksAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_removes_the_mark_of_that_day()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host, "Smoking");
        await HabitActionAsync(host, m => m.OnListMadeItAsync(habitId.ToString()));

        await HabitActionAsync(host, m => m.OnUndoMadeItAsync(habitId.ToString(), Today.ToString(HistoryIds.DateFormat)));

        (await MarksAsync(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Undo_with_a_mangled_date_or_from_another_user_keeps_the_mark()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host, "Smoking");
        await HabitActionAsync(host, m => m.OnListMadeItAsync(habitId.ToString()));

        var mangled = await HabitActionAsync(host, m => m.OnUndoMadeItAsync(habitId.ToString(), "2027-01-15"));
        var stranger = await HabitActionAsync(host, m => m.OnUndoMadeItAsync(habitId.ToString(), Today.ToString(HistoryIds.DateFormat)), user: 78);

        mangled.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.retry.dash"));
        stranger.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-found"));
        (await MarksAsync(host)).Should().ContainSingle().Which.Should().Match<MadeItDay>(m => m.HabitId == habitId && m.Date == Today);
    }

    [Fact]
    public void The_checkin_question_offers_made_it_on_a_free_day_its_undo_once_marked_and_neither_on_a_slipped_or_paused_day()
    {
        string?[] Offered(Habit habit) => BotHandlers.ButtonIds(LetGoButtons.Question(English, habit, TodayOf(habit))).ToArray();

        Offered(LetGoHabit(1)).Should().Equal(HabitActionIds.Slip(1), HabitActionIds.MadeIt(1));
        Offered(Marked(2)).Should().Equal(HabitActionIds.Slip(2), LetGoIds.UndoMadeIt(2, Today));
        Offered(Slipped(3)).Should().Equal(HabitActionIds.Slip(3));
        Offered(Paused(4)).Should().Equal(HabitActionIds.Slip(4));
    }

    [Fact]
    public void The_list_offers_made_it_only_for_let_go_habits_whose_today_is_free_and_unmarked()
    {
        var build = new Habit { Id = 1, Name = "Walk", CreatedAt = LetGoHabit(0).CreatedAt };
        var habits = new[] { build, LetGoHabit(2), Marked(3), Slipped(4), Paused(5) };

        var buttons = BotHandlers.ButtonIds(LetGoButtons.List(English, habits, Today, TimeZoneInfo.Utc));

        buttons.Should().Equal(LetGoIds.Slip(2), LetGoIds.Slip(3), LetGoIds.Slip(4), LetGoIds.Slip(5), LetGoIds.MadeIt(2));
    }

    [Fact]
    public void The_history_grid_shows_the_made_it_mark_on_a_marked_free_day_and_the_plain_free_mark_on_an_unmarked_one()
    {
        var habit = LetGoHabit(1, h => h.MadeItDays.Add(new MadeItDay { HabitId = 1, Date = Today }));
        var labels = LetGoGridLabels(habit);

        var yesterday = labels[^2];
        var today = labels[^1];

        today.Should().EndWith(HabitText.LetGoMark(TodayOf(habit)));
        yesterday.Should().EndWith(HabitText.LetGoMark(LetGoDayState.Free));
        HabitText.LetGoMark(TodayOf(habit)).Should().NotBe(HabitText.LetGoMark(LetGoDayState.Free));
    }

    private static List<string> LetGoGridLabels(Habit habit)
        => HistoryModule.BuildLetGoGrid(English, habit, Today, TimeZoneInfo.Utc).Components
            .OfType<Discord.ActionRowComponent>().SelectMany(r => r.Components).OfType<Discord.ButtonComponent>().Select(b => b.Label).ToList();
}
