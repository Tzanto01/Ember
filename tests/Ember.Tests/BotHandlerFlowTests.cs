using System.Text.Json;
using System.Text.RegularExpressions;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
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
/// Drives the bot's button, select and slash handlers through <see cref="BotHandlers"/> and asserts what they persist and answer.
/// Edits of the clicked message cannot be observed, so those handlers are asserted by the state they leave behind.
/// </summary>
[Collection("Integration")]
public class BotHandlerFlowTests
{
    private const ulong User = 77;
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly Texts English = BotTexts.English;

    private static Task<IntegrationTestHost> HostAsync() => IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));

    private static async Task<int> SeedHabitAsync(IntegrationTestHost host, string name = "Walk", HabitKind kind = HabitKind.Build, bool consent = true)
        => await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await service.GiveHealthDataConsentAsync(User);
            var userId = await service.FindDiscordUserAsync(User);
            var habit = (await service.AddHabitAsync(userId, name, null, kind: kind)).Habit!;
            if (!consent) await service.WithdrawHealthDataConsentAsync(User);
            return habit.Id;
        });

    private static Task<BotReplies> DriveAsync<TModule>(IntegrationTestHost host, Func<HabitService, BotReplies, TModule> create, Func<TModule, Task> call,
        ulong user = User, bool slash = false) where TModule : IInteractionModuleBase
        => DriveAsync(host, (h, _, r) => create(h, r), call, user, slash);

    private static Task<BotReplies> DriveAsync<TModule>(IntegrationTestHost host, Func<HabitService, AccountService, BotReplies, TModule> create,
        Func<TModule, Task> call, ulong user = User, bool slash = false) where TModule : IInteractionModuleBase
        => DriveAsync(host, (h, _, a, r) => create(h, a, r), call, user, slash);

    private static Task<BotReplies> DriveAsync<TModule>(IntegrationTestHost host,
        Func<HabitService, ReminderService, AccountService, BotReplies, TModule> create, Func<TModule, Task> call, ulong user = User,
        bool slash = false) where TModule : IInteractionModuleBase
        => host.WithScopeAsync(async sp =>
        {
            var replies = new BotReplies();
            var module = BotHandlers.Attach(create(sp.GetRequiredService<HabitService>(), sp.GetRequiredService<ReminderService>(),
                sp.GetRequiredService<AccountService>(), replies), user, slash: slash);
            await BotHandlers.RunAsync(() => call(module));
            return replies;
        });

    private static Task<BotReplies> HabitActionAsync(IntegrationTestHost host, Func<RecordingHabitActionModule, Task> call, ulong user = User)
        => DriveAsync(host, (h, r) => new RecordingHabitActionModule(h, r), call, user);

    private static Task<BotReplies> FrequencyAsync(IntegrationTestHost host, Func<RecordingFrequencyModule, Task> call)
        => DriveAsync(host, (h, r) => new RecordingFrequencyModule(h, r), call);

    private static Task<BotReplies> PrivacyAsync(IntegrationTestHost host, Func<RecordingPrivacyModule, Task> call, ulong user = User, bool slash = false)
        => DriveAsync(host, (h, a, r) => new RecordingPrivacyModule(h, a, r), call, user, slash);

    private static Task<Habit> HabitAsync(IntegrationTestHost host, int habitId)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.AsNoTracking().SingleAsync(h => h.Id == habitId));

    private static Task<User> AccountAsync(IntegrationTestHost host)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.AsNoTracking().SingleAsync());

    [Theory]
    [InlineData("today", 0)]
    [InlineData("3days", 2)]
    [InlineData("1week", 6)]
    [InlineData("anything-else", 6)]
    public async Task Pause_buttons_pause_the_habit_through_the_chosen_day_starting_today(string duration, int daysThroughToday)
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        await HabitActionAsync(host, m => m.OnPauseAsync(habitId.ToString(), duration));

        var habit = await HabitAsync(host, habitId);
        (habit.PausedFrom, habit.PausedUntil).Should().Be((Today, Today.AddDays(daysThroughToday)));
    }

    [Fact]
    public async Task Pause_resume_ends_a_running_pause_yesterday()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);
        await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.PausedFrom, Today.AddDays(-5)).SetProperty(h => h.PausedUntil, Today.AddDays(3))));

        await HabitActionAsync(host, m => m.OnPauseAsync(habitId.ToString(), "resume"));

        var habit = await HabitAsync(host, habitId);
        (habit.PausedFrom, habit.PausedUntil).Should().Be((Today.AddDays(-5), Today.AddDays(-1)));
    }

    [Fact]
    public async Task Pause_for_a_habit_that_is_not_yours_or_has_a_broken_id_answers_without_pausing()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        var stranger = await HabitActionAsync(host, m => m.OnPauseAsync(habitId.ToString(), "1week"), user: 78);
        var broken = await HabitActionAsync(host, m => m.OnPauseAsync("abc", "1week"));

        stranger.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-found"));
        broken.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.retry.dash"));
        (await HabitAsync(host, habitId)).PausedUntil.Should().BeNull();
    }

    [Fact]
    public async Task Delete_confirm_removes_only_the_clickers_own_habit()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        var stranger = await HabitActionAsync(host, m => m.OnDeleteConfirmAsync(habitId.ToString()), user: 78);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.CountAsync())).Should().Be(1);
        await HabitActionAsync(host, m => m.OnDeleteConfirmAsync(habitId.ToString()));

        stranger.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-found"));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Slip_buttons_add_one_slip_to_todays_count_for_a_let_go_habit_only()
    {
        await using var host = await HostAsync();
        var letGoId = await SeedHabitAsync(host, "Smoking", HabitKind.LetGo);
        var buildId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            return (await service.AddHabitAsync(await service.FindDiscordUserAsync(User), "Walk", null)).Habit!.Id;
        });

        var fromList = await HabitActionAsync(host, m => m.OnListSlipAsync(letGoId.ToString()));
        await HabitActionAsync(host, m => m.OnSlipAsync(letGoId.ToString()));
        var onBuild = await HabitActionAsync(host, m => m.OnSlipAsync(buildId.ToString()));

        fromList.Sent.Should().ContainSingle().Which.Ephemeral.Should().BeTrue();
        onBuild.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-found"));
        var slips = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitSlips.AsNoTracking().ToListAsync());
        slips.Should().ContainSingle().Which.Should().Match<HabitSlip>(s => s.HabitId == letGoId && s.Date == Today && s.Count == 2);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    public async Task Grace_button_stores_zero_to_three_days(string days, int expected)
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host);

        await HabitActionAsync(host, m => m.OnGraceAsync(days));

        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetGraceDaysAsync(User))).Should().Be(expected);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("-1")]
    [InlineData("many")]
    public async Task Grace_button_outside_zero_to_three_changes_nothing_and_says_so(string days)
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host);

        var replies = await HabitActionAsync(host, m => m.OnGraceAsync(days));

        replies.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("habit.grace.invalid"));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetGraceDaysAsync(User))).Should().Be(1);
    }

    [Fact]
    public async Task Rename_modal_stores_the_trimmed_name_and_a_blank_one_changes_nothing()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        var blank = await HabitActionAsync(host, m => m.OnRenameModalAsync(habitId.ToString(), "1", new HabitRenameModal { HabitName = "   " }));
        var renamed = await HabitActionAsync(host, m => m.OnRenameModalAsync(habitId.ToString(), "1", new HabitRenameModal { HabitName = "  Evening walk " }));

        blank.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("habit.rename.empty"));
        renamed.Sent.Should().ContainSingle().Which.Embed.Should().NotBeNull();
        (await HabitAsync(host, habitId)).Name.Should().Be("Evening walk");
    }

    [Fact]
    public async Task Check_in_done_button_adds_a_check_in_for_today_and_skip_records_an_explicit_not_done()
    {
        await using var host = await HostAsync();
        var doneId = await SeedHabitAsync(host);
        var skipId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            return (await service.AddHabitAsync(await service.FindDiscordUserAsync(User), "Read", null)).Habit!.Id;
        });

        await HabitActionAsync(host, m => m.OnCheckinDoneAsync(doneId.ToString()));
        await HabitActionAsync(host, m => m.OnCheckinSkipAsync(skipId.ToString()));

        var logs = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitLogs.AsNoTracking().OrderBy(l => l.HabitId).ToListAsync());
        logs.Select(l => (l.HabitId, l.Date, l.Completed, l.Count)).Should().Equal((doneId, Today, true, 1), (skipId, Today, false, 0));
    }

    [Fact]
    public async Task Check_in_skip_slip_and_rename_buttons_write_nothing_without_consent()
    {
        await using var host = await HostAsync();
        var buildId = await SeedHabitAsync(host, consent: false);

        await HabitActionAsync(host, m => m.OnCheckinDoneAsync(buildId.ToString()));
        await HabitActionAsync(host, m => m.OnCheckinSkipAsync(buildId.ToString()));
        await HabitActionAsync(host, m => m.OnRenameModalAsync(buildId.ToString(), "1", new HabitRenameModal { HabitName = "Renamed" }));

        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitLogs.CountAsync())).Should().Be(0);
        (await HabitAsync(host, buildId)).Name.Should().Be("Walk");
    }

    [Fact]
    public async Task Frequency_weekly_button_sets_a_weekly_habit_with_the_chosen_target()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        await FrequencyAsync(host, m => m.SetWeeklyTargetAsync(habitId.ToString(), "4"));

        var habit = await HabitAsync(host, habitId);
        (habit.FrequencyType, habit.WeeklyTarget, habit.TimesPerDay).Should().Be((FrequencyType.Weekly, 4, 1));
    }

    [Fact]
    public async Task Frequency_days_select_turns_the_chosen_weekdays_into_the_schedule_bitmask()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        await FrequencyAsync(host, m => m.SetWeekdaysAsync(habitId.ToString(), ["sun", "mon", "wed"]));

        var habit = await HabitAsync(host, habitId);
        (habit.FrequencyType, habit.Days).Should().Be((FrequencyType.Weekdays, ScheduleDays.Mon | ScheduleDays.Wed | ScheduleDays.Sun));
    }

    [Theory]
    [InlineData("")]
    [InlineData("mon,tue,wed,thu,fri,sat,sun")]
    [InlineData("mon,funday")]
    public async Task Frequency_days_select_with_no_day_all_seven_or_an_unknown_day_changes_nothing(string selected)
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);
        var values = selected.Split(',', StringSplitOptions.RemoveEmptyEntries);

        var replies = await FrequencyAsync(host, m => m.SetWeekdaysAsync(habitId.ToString(), values));

        replies.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("freq.retry"));
        (await HabitAsync(host, habitId)).FrequencyType.Should().Be(FrequencyType.Daily);
    }

    [Fact]
    public async Task Frequency_times_select_sets_times_per_day_keeps_the_weekdays_and_refuses_values_above_the_maximum()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);
        await FrequencyAsync(host, m => m.SetWeekdaysAsync(habitId.ToString(), ["tue", "thu"]));

        await FrequencyAsync(host, m => m.SetTimesAsync(habitId.ToString(), ["3"]));
        var tooMany = await FrequencyAsync(host, m => m.SetTimesAsync(habitId.ToString(), [(Ember.Core.Rules.Limits.TimesPerDay + 1).ToString()]));

        var habit = await HabitAsync(host, habitId);
        (habit.TimesPerDay, habit.Days).Should().Be((3, ScheduleDays.Tue | ScheduleDays.Thu));
        tooMany.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("freq.retry"));
    }

    [Fact]
    public async Task Frequency_times_select_on_a_weekly_habit_is_refused()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);
        await FrequencyAsync(host, m => m.SetWeeklyTargetAsync(habitId.ToString(), "2"));

        var replies = await FrequencyAsync(host, m => m.SetTimesAsync(habitId.ToString(), ["2"]));

        replies.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("freq.times.weekly"));
        (await HabitAsync(host, habitId)).TimesPerDay.Should().Be(1);
    }

    [Fact]
    public async Task Frequency_menu_offers_the_schedule_buttons_for_a_build_habit_and_only_the_web_hint_for_a_let_go_habit()
    {
        await using var host = await HostAsync();
        var buildId = await SeedHabitAsync(host);
        var letGoId = await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            return (await service.AddHabitAsync(await service.FindDiscordUserAsync(User), "Smoking", null, kind: HabitKind.LetGo)).Habit!.Id;
        });

        var build = await FrequencyAsync(host, m => m.ShowMenuAsync(buildId.ToString()));
        var letGo = await FrequencyAsync(host, m => m.ShowMenuAsync(letGoId.ToString()));

        BotHandlers.ComponentIds(build.Sent.Single().Components).Should().Contain(
            [$"freq:weekly:{buildId}:3", $"freq:weekdays:{buildId}", $"freq:times:{buildId}", $"habitaction:freq:{buildId}:daily"]);
        letGo.Sent.Single().Text.Should().Be(HabitModule.LetGoWebOnlyText(English));
        letGo.Sent.Single().Components.Should().BeNull();
    }

    [Fact]
    public async Task Withdraw_command_asks_with_confirm_and_cancel_buttons_bound_to_the_asker()
    {
        await using var host = await HostAsync();

        var replies = await PrivacyAsync(host, m => m.WithdrawAsync(), slash: true);

        var reply = replies.Sent.Should().ContainSingle().Subject;
        reply.Ephemeral.Should().BeTrue();
        BotHandlers.ButtonIds(reply.Components).Should().Equal($"privacy:withdraw:confirm:{User}", $"privacy:withdraw:cancel:{User}");
    }

    [Fact]
    public async Task Withdraw_confirm_withdraws_the_consent_only_for_its_owner()
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host);

        var stranger = await PrivacyAsync(host, m => m.OnConfirmWithdrawAsync(User.ToString()), user: 78);
        (await AccountAsync(host)).ConsentWithdrawnAt.Should().BeNull();
        await PrivacyAsync(host, m => m.OnConfirmWithdrawAsync(User.ToString()));

        stranger.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-yours"));
        (await AccountAsync(host)).ConsentWithdrawnAt.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task Withdraw_confirm_for_an_account_without_consent_or_data_withdraws_nothing()
    {
        await using var host = await HostAsync();
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().SetGraceDaysAsync(User, 2));

        await PrivacyAsync(host, m => m.OnConfirmWithdrawAsync(User.ToString()));

        (await AccountAsync(host)).ConsentWithdrawnAt.Should().BeNull();
    }

    [Fact]
    public async Task Delete_start_asks_for_confirmation_with_buttons_bound_to_the_owner_and_refuses_everyone_else()
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host);

        var owner = await PrivacyAsync(host, m => m.OnStartDeleteAsync(User.ToString()));
        var stranger = await PrivacyAsync(host, m => m.OnStartDeleteAsync(User.ToString()), user: 78);

        BotHandlers.ButtonIds(owner.Sent.Single().Components).Should().Equal($"privacy:delete:confirm:{User}", $"privacy:delete:cancel:{User}");
        stranger.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("common.not-yours"));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(1);
    }

    [Fact]
    public async Task Delete_confirm_deletes_the_account_only_for_its_owner()
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host);

        await PrivacyAsync(host, m => m.OnConfirmDeleteAsync(User.ToString()), user: 78);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(1);
        await PrivacyAsync(host, m => m.OnConfirmDeleteAsync(User.ToString()));

        (await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Users.CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task Export_command_builds_the_discord_csv_by_default_and_json_on_request()
    {
        await using var host = await HostAsync();
        await SeedHabitAsync(host, "Walk");
        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<JournalService>().SetDayNoteAsync(await service.FindDiscordUserAsync(User), Today, "private words", null, null);
        });

        var csv = await DriveAsync(host, (h, a, r) => new RecordingExportModule(h, a, r), m => m.ExportAsync(), slash: true);
        var json = await DriveAsync(host, (h, a, r) => new RecordingExportModule(h, a, r), m => m.ExportAsync(ExportModule.ExportFormat.Json), slash: true);

        var csvFile = csv.Files.Should().ContainSingle().Subject;
        csvFile.FileName.Should().MatchRegex(@"^ember-export-\d{4}-\d{2}-\d{2}\.csv$");
        csvFile.Content.Should().StartWith(ExportBuilder.CsvHeader).And.Contain("habit,").And.Contain("Walk").And.NotContain("private words");
        var jsonFile = json.Files.Should().ContainSingle().Subject;
        jsonFile.FileName.Should().MatchRegex(@"^ember-export-\d{4}-\d{2}-\d{2}\.json$");
        JsonElement.Parse(jsonFile.Content).GetProperty("habits").GetArrayLength().Should().Be(1);
        jsonFile.Content.Should().NotContain("private words");
    }

    [Fact]
    public async Task Export_command_for_an_unknown_user_says_there_is_nothing_to_export()
    {
        await using var host = await HostAsync();

        var replies = await DriveAsync(host, (h, a, r) => new RecordingExportModule(h, a, r), m => m.ExportAsync(), slash: true);

        replies.Files.Should().BeEmpty();
        replies.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("export.nothing"));
    }

    [Fact]
    public async Task Timezone_set_stores_the_zone_and_reschedules_reminders_while_an_unknown_zone_changes_nothing()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().EditHabitAsync(User, habitId, null, new TimeOnly(18, 0), false));

        var unknown = await DriveAsync(host, (h, rem, _, r) => new RecordingTimezoneModule(h, rem, r), m => m.SetAsync("Mars/Olympus"), slash: true);
        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().HasTimezoneSetAsync(User))).Should().BeFalse();
        var set = await DriveAsync(host, (h, rem, _, r) => new RecordingTimezoneModule(h, rem, r), m => m.SetAsync("Europe/Berlin"), slash: true);

        unknown.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Format("timezone.not-found", "Mars/Olympus"));
        set.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Format("timezone.set", "Europe/Berlin") + "\n" + English.Format("timezone.rescheduled", 1));
        (await AccountAsync(host)).Timezone.Should().Be("Europe/Berlin");
        (await HabitAsync(host, habitId)).NextReminderUtc.Should().Be(new DateTime(2027, 1, 15, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task Timezone_show_names_the_stored_zone()
    {
        await using var host = await HostAsync();
        await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().SetTimezoneAsync(User, "Asia/Tokyo"));

        var replies = await DriveAsync(host, (h, rem, _, r) => new RecordingTimezoneModule(h, rem, r), m => m.ShowAsync(), slash: true);

        replies.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Format("timezone.show", "Asia/Tokyo"));
    }

    [Fact]
    public async Task Onboarding_timezone_pick_stores_the_zone_and_an_unknown_zone_or_region_answers_without_storing()
    {
        await using var host = await HostAsync();
        var habitId = await SeedHabitAsync(host);

        var unknownZone = await DriveAsync(host, (h, rem, _, r) => new RecordingOnboardingModule(h, rem, r), m => m.OnTimezoneSelectAsync(habitId.ToString(), ["Mars/Olympus"]));
        var unknownRegion = await DriveAsync(host, (h, rem, _, r) => new RecordingOnboardingModule(h, rem, r), m => m.OnRegionSelectAsync(habitId.ToString(), ["moon"]));
        (await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().HasTimezoneSetAsync(User))).Should().BeFalse();
        await DriveAsync(host, (h, rem, _, r) => new RecordingOnboardingModule(h, rem, r), m => m.OnTimezoneSelectAsync(habitId.ToString(), ["Pacific/Auckland"]));

        unknownZone.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("onboard.tz.unknown"));
        unknownRegion.Sent.Should().ContainSingle().Which.Text.Should().Be(English.Get("onboard.region.unknown"));
        (await AccountAsync(host)).Timezone.Should().Be("Pacific/Auckland");
    }
}
