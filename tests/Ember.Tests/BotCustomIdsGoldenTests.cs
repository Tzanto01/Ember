using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot;
using Ember.Bot.Discord.CustomIds;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Tests;

/// <summary>
/// Discord stores the custom ids of messages already sent, so the bot must keep producing the same ids and keep routing them. The golden
/// lists one id per family, built by the same builders the bot uses, with the handler pattern Discord.Net routes it to. The fake context
/// is not a socket context, so a routed handler fails before its body runs.
/// </summary>
[Collection("Integration")]
public class BotCustomIdsGoldenTests
{
    private const int Habit = 12;
    private const ulong Owner = 345;
    private const long SentUnix = 1_800_000_000;
    private const ulong SourceMessage = 678;
    private static readonly DateOnly Day = new(2027, 1, 15);

    private static readonly string[] Components =
    [
        CheckInIds.Done(Habit, SentUnix), CheckInIds.Skip(Habit, SentUnix), CheckInIds.Snooze(Habit, SentUnix),
        ConsentIds.Agree(Owner), ConsentIds.Decline(Owner),
        DiscordReminderIds.Yes(Owner), DiscordReminderIds.No(Owner), DiscordReminderIds.Stop(Owner),
        DmRetryIds.Retry(Owner),
        FiresideIds.To, FiresideIds.Message(4), FiresideIds.Back(4), FiresideIds.Ok(4, null), FiresideIds.Ok(4, 9),
        FrequencyIds.Weekly(Habit, 3), FrequencyIds.Menu(Habit), FrequencyIds.Weekdays(Habit), FrequencyIds.Days(Habit),
        FrequencyIds.Times(Habit), FrequencyIds.TimesPerDay(Habit),
        HabitIds.SelectCheckIn, HabitIds.SelectShare, HabitIds.SelectDelete, HabitIds.SelectEdit, HabitIds.SelectPause,
        HabitIds.SelectFrequency, HabitIds.SelectHistory, HabitIds.TemplateSelect,
        HabitIds.Grace(0), HabitIds.Grace(3), HabitIds.Streaks(show: true), HabitIds.Streaks(show: false),
        HabitActionIds.CheckInDone(Habit), HabitActionIds.CheckInSkip(Habit), HabitActionIds.Slip(Habit),
        HabitActionIds.DeleteConfirm(Habit), HabitActionIds.DeleteCancel, HabitActionIds.Rename(Habit),
        HabitActionIds.Pause(Habit, "today"), HabitActionIds.Pause(Habit, "3days"), HabitActionIds.Pause(Habit, "1week"),
        HabitActionIds.Pause(Habit, "resume"), HabitActionIds.Daily(Habit),
        HistoryIds.Day(Owner, Habit, Day), HistoryIds.Low(Owner, Habit),
        LetGoIds.Slip(Habit),
        OnboardingIds.Timezone(Habit), OnboardingIds.Region("12"), OnboardingIds.TimezoneSelect("12"),
        PrivacyIds.DeleteStart(Owner), PrivacyIds.DeleteConfirm(Owner), PrivacyIds.DeleteCancel(Owner),
        PrivacyIds.WithdrawConfirm(Owner), PrivacyIds.WithdrawCancel(Owner),
        ReflectIds.Dismiss(Owner), ReflectIds.Log(Owner, "20270115"), ReflectIds.Pick(Owner, "20270115"), ReflectIds.Low(Owner, "20270115"),
        ReminderIds.Set(Habit), ReminderIds.Time("12"), ReminderIds.Preset(Habit, new TimeOnly(7, 30)), ReminderIds.Skip(Habit),
        ReminderIds.Clear(Habit), ReminderIds.Cancel(Habit),
        TipIds.Streaks(show: true), TipIds.Streaks(show: false),
    ];

    private static readonly string[] Modals =
    [
        FeedbackIds.Modal, HabitIds.AddModal, HabitIds.RenameModal("12", SourceMessage), ReminderIds.Modal("12", SourceMessage),
    ];

    /// <summary>Produced on buttons that are always disabled, so no handler serves it.</summary>
    private static readonly string[] Unserved = [HistoryIds.LetGo(Habit, Day)];

    [Fact]
    public async Task Every_produced_custom_id_matches_the_reviewed_golden_and_reaches_a_handler()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false);
        using var scope = host.Host.Services.CreateScope();
        var services = scope.ServiceProvider;
        using var interactions = new InteractionService(new DiscordSocketClient(), new InteractionServiceConfig { DefaultRunMode = RunMode.Sync, ThrowOnError = false });
        await interactions.AddModulesAsync(typeof(AppHost).Assembly, services);
        string? routedTo = null;
        interactions.InteractionExecuted += (command, _, _) =>
        {
            routedTo = command?.Name;
            return Task.CompletedTask;
        };

        async Task<string?> RouteAsync(IDiscordInteraction interaction)
        {
            routedTo = null;
            var result = await interactions.ExecuteCommandAsync(new FakeInteractionContext(interaction), services);
            return result.Error == InteractionCommandError.UnknownCommand ? null : routedTo;
        }

        var lines = new List<string>();
        foreach (var id in Components)
            lines.Add($"component {id} -> {await RouteAsync(FakeInteraction.Component(Owner, id)) ?? "NO HANDLER"}");
        foreach (var id in Modals)
            lines.Add($"modal {id} -> {await RouteAsync(FakeInteraction.Modal(Owner, id)) ?? "NO HANDLER"}");
        foreach (var id in Unserved)
            lines.Add($"component {id} -> {await RouteAsync(FakeInteraction.Component(Owner, id)) ?? "(disabled, no handler)"}");

        lines.Should().NotContain(line => line.EndsWith("NO HANDLER"), "every produced id needs a handler");
        GoldenFile.AssertMatches("bot-custom-ids.txt", string.Join('\n', lines) + "\n");
    }
}
