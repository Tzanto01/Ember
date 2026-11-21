using System.Globalization;
using Discord;
using Discord.Interactions;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Bot.Discord.Messages;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Modules;

[Group("habit", "Track your habits")]
public class HabitModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly ReminderService _reminders;
    private readonly IConfiguration _config;

    public HabitModule(HabitService habits, ConsentService consent, UserSettingsService settings, ReminderService reminders, IConfiguration config)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
        _reminders = reminders;
        _config = config;
    }

    // ── Habit select menu builder (shared by several commands) ───────────────

    internal static SelectMenuBuilder BuildHabitSelectMenu(
        Texts t,
        IReadOnlyList<Habit> habits,
        string customId,
        string placeholder,
        DateOnly today = default,
        TimeZoneInfo? tz = null)
    {
        var menu = new SelectMenuBuilder()
            .WithCustomId(customId)
            .WithPlaceholder(placeholder)
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var h in habits.Take(25))
        {
            var label = h.Name.Length > SelectMenuOptionBuilder.MaxSelectLabelLength
                ? h.Name[..SelectMenuOptionBuilder.MaxSelectLabelLength]
                : h.Name;
            menu.AddOption(label, h.Id.ToString(CultureInfo.InvariantCulture), h.Kind == HabitKind.LetGo && tz is not null
                ? HabitText.DaysFreeText(t, LetGoRules.StatsOn(h, today, tz).DaysFree)
                : HabitText.FrequencyLabel(t, h));
        }

        return menu;
    }

    /// <summary><see cref="BuildHabitSelectMenu"/> for the user of this interaction, with days free on let-go habits.</summary>
    private async Task<SelectMenuBuilder> HabitMenuAsync(Texts t, Guid userId, IReadOnlyList<Habit> habits, string customId, string placeholder)
        => BuildHabitSelectMenu(t, habits, customId, placeholder,
            await _settings.GetLocalTodayAsync(userId), await _settings.GetTimeZoneAsync(userId));

    // ── /habit add ────────────────────────────────────────────────────────────

    [SlashCommand("add", "Add a new habit to track")]
    public async Task AddAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!await _consent.HasHealthDataConsentAsync(userId))
        {
            await ConsentPrompt.RespondAsync(Context, _config, _habits, _consent);
            return;
        }

        var modal = new ModalBuilder()
            .WithTitle(t.Get("habit.add.modal.title"))
            .WithCustomId(HabitIds.AddModal)
            .AddTextInput(t.Get("habit.add.modal.label"), "habit_name",
                placeholder: t.Get("habit.add.modal.placeholder"),
                minLength: 1, maxLength: Limits.HabitNameLength, required: true)
            .Build();

        await RespondWithModalAsync(modal);
    }

    // ── /habit template ───────────────────────────────────────────────────────

    [SlashCommand("template", "Start a habit from a pre-built template")]
    public async Task TemplateAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!await _consent.HasHealthDataConsentAsync(userId))
        {
            await ConsentPrompt.RespondAsync(Context, _config, _habits, _consent);
            return;
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId(HabitIds.TemplateSelect)
            .WithPlaceholder(t.Get("habit.template.placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1);

        foreach (var template in HabitTemplates.All)
        {
            var freqHint = " · " + (template.Frequency == HabitTemplates.FrequencyHint.Weekly
                ? t.Format("habit.freq.weekly", template.WeeklyTarget)
                : t.Get("habit.freq.daily"));
            menu.AddOption(
                label: HabitText.TemplateName(t, template),
                value: template.Key,
                description: $"{HabitText.TemplateDescription(t, template)}{freqHint}");
        }

        var components = new ComponentBuilder().WithSelectMenu(menu).Build();

        await RespondAsync(
            t.Get("habit.template.prompt"),
            components: components,
            ephemeral: true);
    }

    // ── /habit list ───────────────────────────────────────────────────────────

    [SlashCommand("list", "See all your habits and their recent progress")]
    public async Task ListAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsAsync(userId);
        var grace = await _settings.GetGraceDaysAsync(userId);
        var today = await _settings.GetLocalTodayAsync(userId);
        var lowDays = await _habits.GetLowDaysAsync(userId);
        var tz = await _settings.GetTimeZoneAsync(userId);
        var showStreaks = await _settings.GetShowStreaksAsync(userId);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.none.start-both"), ephemeral: true);
            return;
        }

        const int pageSize = 8;
        var pages = (int)Math.Ceiling(habits.Count / (double)pageSize);

        for (int page = 0; page < pages; page++)
        {
            var slice = habits.Skip(page * pageSize).Take(pageSize).ToList();

            var embed = new EmbedBuilder()
                .WithColor(0xF4845F)
                .WithTitle(pages > 1 ? t.Format("habit.list.title.paged", page + 1, pages) : t.Get("habit.list.title"));

            foreach (var h in slice)
            {
                var todayMark = h.Kind == HabitKind.LetGo ? HabitText.LetGoMark(LetGoRules.DayOn(h, today, tz)) : HabitText.DayMark(h, today);
                var reminderStr = HabitText.FormatReminder(t, h.ReminderTimes);
                var pauseStr = HabitText.PausedSuffix(t, h, today);

                var progressStr = HabitText.ListProgress(t, h, today, grace, lowDays, tz, showStreaks);

                embed.AddField(
                    $"{todayMark} {h.Name}{pauseStr} _({HabitText.FrequencyLabel(t, h)})_",
                    $"{progressStr}\n{reminderStr}",
                    inline: false);
            }

            embed.WithFooter(t.Get("habit.list.footer"));
            var buttons = LetGoButtons.List(t, slice, today, tz);

            if (page == 0)
                await RespondAsync(embed: embed.Build(), components: buttons, ephemeral: true);
            else
                await FollowupAsync(embed: embed.Build(), components: buttons, ephemeral: true);
        }
    }

    // ── /habit checkin ────────────────────────────────────────────────────────

    [SlashCommand("checkin", "Log today's check-in for a habit")]
    public async Task CheckInAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.none.start"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectCheckIn, t.Get("habit.checkin.placeholder"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.checkin.prompt"), components: components, ephemeral: true);
    }

    // ── /habit history ────────────────────────────────────────────────────────

    [SlashCommand("history", "See and fill in the last two weeks of a habit, and mark low days")]
    public async Task HistoryAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.none.start"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectHistory, t.Get("habit.history.placeholder"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.history.prompt"), components: components, ephemeral: true);
    }

    // ── /habit streak ─────────────────────────────────────────────────────────

    [SlashCommand("streak", "See your personal habit stats")]
    public async Task StreakAsync()
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var (pages, toggle) = await StatsReply.RenderAsync(t, _habits, _settings, await _habits.FindDiscordUserAsync(Context.User.Id));

        if (pages.Count == 0)
        {
            await RespondAsync(t.Get("habit.stats.none"), ephemeral: true);
            return;
        }

        await RespondAsync(embed: pages[0], components: toggle, ephemeral: true);
        foreach (var page in pages.Skip(1))
            await FollowupAsync(embed: page, ephemeral: true);
    }

    // ── /habit share ──────────────────────────────────────────────────────────

    [SlashCommand("share", "Share a habit card publicly in this channel")]
    public async Task ShareAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.none.start"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectShare, t.Get("habit.share.prompt"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.share.prompt"), components: components, ephemeral: true);
    }

    // ── /habit delete ─────────────────────────────────────────────────────────

    [SlashCommand("delete", "Remove a habit and stop tracking it")]
    public async Task DeleteAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.delete.none"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectDelete, t.Get("habit.delete.placeholder"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.delete.prompt"), components: components, ephemeral: true);
    }

    // ── /habit edit ───────────────────────────────────────────────────────────

    [SlashCommand("edit", "Edit a habit's name or reminder time")]
    public async Task EditAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.edit.none"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectEdit, t.Get("habit.edit.prompt"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.edit.prompt"), components: components, ephemeral: true);
    }

    // ── /habit frequency ──────────────────────────────────────────────────────

    [SlashCommand("frequency", "Change how often a habit needs to be done")]
    public async Task FrequencyAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.none"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectFrequency, t.Get("habit.frequency.placeholder"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.frequency.prompt"), components: components, ephemeral: true);
    }

    // ── /habit pause ──────────────────────────────────────────────────────────

    [SlashCommand("pause", "Pause a habit temporarily — reminders stop while it's paused")]
    public async Task PauseAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsWithSlipsAsync(userId);

        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (habits.Count == 0)
        {
            await RespondAsync(t.Get("habit.pause.none"), ephemeral: true);
            return;
        }

        var menu = await HabitMenuAsync(t, userId, habits, HabitIds.SelectPause, t.Get("habit.pause.prompt"));
        var components = new ComponentBuilder().WithSelectMenu(menu).Build();
        await RespondAsync(t.Get("habit.pause.prompt"), components: components, ephemeral: true);
    }

    // ── /habit grace ──────────────────────────────────────────────────────────

    [SlashCommand("grace", "Set how many flex days per week you get before a streak breaks")]
    public async Task GraceAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var current = await _settings.GetGraceDaysAsync(userId);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        var components = new ComponentBuilder()
            .WithButton(t.Get("habit.grace.strict"), HabitIds.Grace(0), current == 0 ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton(t.Plural("habit.grace.button", 1), HabitIds.Grace(1), current == 1 ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton(t.Plural("habit.grace.button", 2), HabitIds.Grace(2), current == 2 ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .WithButton(t.Plural("habit.grace.button", 3), HabitIds.Grace(3), current == 3 ? ButtonStyle.Primary : ButtonStyle.Secondary)
            .Build();

        await RespondAsync(
            t.Plural("habit.grace.current", current),
            components: components,
            ephemeral: true);
    }

    // ── /habit second-reminder ────────────────────────────────────────────────

    [SlashCommand("second-reminder", "Get one more nudge after a reminder if you haven't checked in yet")]
    public async Task SecondReminderAsync([Summary("delay", "How long after a reminder")] SecondReminderDelay delay)
    {
        var minutes = (int)delay;
        await _reminders.SetSecondReminderMinutesAsync(await _habits.ResolveOrCreateDiscordUserAsync(Context.User.Id, Languages.FromLocale(Context.Interaction.UserLocale)), minutes);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        await RespondAsync(minutes == 0
                ? t.Get("habit.second-reminder.off")
                : t.Format("habit.second-reminder.on", SecondReminderLabel(t, delay)),
            ephemeral: true);
    }

    private static string SecondReminderLabel(Texts t, SecondReminderDelay delay) => t.Get(delay switch
    {
        SecondReminderDelay.FifteenMinutes => "duration.15m",
        SecondReminderDelay.ThirtyMinutes => "duration.30m",
        SecondReminderDelay.OneHour => "duration.1h",
        _ => "duration.2h",
    });
}

/// <summary>The <c>/habit second-reminder</c> choices; each value is <see cref="Core.Models.User.SecondReminderMinutes"/>.</summary>
public enum SecondReminderDelay
{
    [ChoiceDisplay("Off")] Off = 0,
    [ChoiceDisplay("15 minutes")] FifteenMinutes = 15,
    [ChoiceDisplay("30 minutes")] ThirtyMinutes = 30,
    [ChoiceDisplay("1 hour")] OneHour = 60,
    [ChoiceDisplay("2 hours")] TwoHours = 120,
}
