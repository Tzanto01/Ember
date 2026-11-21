using System.Globalization;
using Discord;
using Discord.Interactions;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
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
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
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
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
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
                label: TemplateSelectModule.Name(t, template),
                value: template.Key,
                description: $"{TemplateSelectModule.Description(t, template)}{freqHint}");
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
                var todayMark = h.Kind == HabitKind.LetGo ? HabitText.LetGoMark(LetGoRules.DayOn(h, today, tz).State) : HabitText.DayMark(h, today);
                var reminderStr = HabitText.FormatReminder(t, h.ReminderTimes);
                var pauseStr = h.PausedUntil.HasValue && h.PausedUntil >= today
                    ? " · " + t.Format("habit.list.paused", h.PausedUntil.Value.ToString(t.Get("date.month-day"), t.Culture)) : "";

                var progressStr = HabitText.ListProgress(t, h, today, grace, lowDays, tz, showStreaks);

                embed.AddField(
                    $"{todayMark} {h.Name}{pauseStr} _({HabitText.FrequencyLabel(t, h)})_",
                    $"{progressStr}\n{reminderStr}",
                    inline: false);
            }

            embed.WithFooter(t.Get("habit.list.footer"));
            var slips = SlipButtons(t, slice);

            if (page == 0)
                await RespondAsync(embed: embed.Build(), components: slips, ephemeral: true);
            else
                await FollowupAsync(embed: embed.Build(), components: slips, ephemeral: true);
        }
    }

    /// <summary>One "I slipped: {name}" button per let-go habit in <paramref name="habits"/> (<c>letgo:slip:{habitId}</c>), null without any.</summary>
    internal static MessageComponent? SlipButtons(Texts t, IReadOnlyList<Habit> habits)
    {
        var letGo = habits.Where(h => h.Kind == HabitKind.LetGo).Take(25).ToList();
        if (letGo.Count == 0) return null;
        var builder = new ComponentBuilder();
        for (var i = 0; i < letGo.Count; i++)
        {
            var label = t.Format("habit.letgo.slip-button.named", letGo[i].Name);
            if (label.Length > ButtonBuilder.MaxButtonLabelLength) label = label[..(ButtonBuilder.MaxButtonLabelLength - 1)] + "…";
            builder.WithButton(label, LetGoIds.Slip(letGo[i].Id), ButtonStyle.Secondary, row: i / 5);
        }
        return builder.Build();
    }

    // ── /habit checkin ────────────────────────────────────────────────────────

    [SlashCommand("checkin", "Log today's check-in for a habit")]
    public async Task CheckInAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsAsync(userId);

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
        var habits = await _habits.GetHabitsAsync(userId);

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
        var (pages, toggle) = await RenderStatsAsync(t, _habits, _settings, await _habits.FindDiscordUserAsync(Context.User.Id));

        if (pages.Count == 0)
        {
            await RespondAsync(t.Get("habit.stats.none"), ephemeral: true);
            return;
        }

        await RespondAsync(embed: pages[0], components: toggle, ephemeral: true);
        foreach (var page in pages.Skip(1))
            await FollowupAsync(embed: page, ephemeral: true);
    }

    /// <summary>
    /// The <c>/habit streak</c> reply: one stats embed per page of habits (none without habits) and the button that flips
    /// <see cref="User.ShowStreaks"/> ("habit:streaks:on" / "habit:streaks:off"). With streaks off the stats have no streak lines.
    /// </summary>
    internal static async Task<(List<Embed> Pages, MessageComponent Toggle)> RenderStatsAsync(Texts t, HabitService service, UserSettingsService settings,
        Guid userId)
    {
        var habits = await service.GetHabitsAsync(userId);
        var grace = await settings.GetGraceDaysAsync(userId);
        var today = await settings.GetLocalTodayAsync(userId);
        var lowDays = await service.GetLowDaysAsync(userId);
        var tz = await settings.GetTimeZoneAsync(userId);
        var showStreaks = await settings.GetShowStreaksAsync(userId);

        var toggle = new ComponentBuilder()
            .WithButton(t.Get(showStreaks ? "habit.stats.hide-streaks" : "habit.stats.show-streaks"), HabitIds.Streaks(show: !showStreaks), ButtonStyle.Secondary)
            .Build();

        const int pageSize = 6;
        var pages = (int)Math.Ceiling(habits.Count / (double)pageSize);
        var embeds = new List<Embed>();

        for (int page = 0; page < pages; page++)
        {
            var slice = habits.Skip(page * pageSize).Take(pageSize).ToList();

            var embed = new EmbedBuilder()
                .WithColor(0xF4845F)
                .WithTitle(pages > 1 ? t.Format("habit.stats.title.paged", page + 1, pages) : t.Get("habit.stats.title"));

            foreach (var h in slice)
            {
                string statsText;
                if (h.Kind == HabitKind.LetGo)
                {
                    var stats = LetGoRules.StatsOn(h, today, tz);
                    statsText = t.Format("habit.letgo.stats", stats.DaysFree, stats.LongestStretch, stats.FreeDaysThisMonth);
                }
                else if (h.FrequencyType == FrequencyType.Weekly)
                {
                    var (done, target) = HabitRules.WeeklyProgress(h, today);
                    var total = h.Logs.Count(l => l.Completed);
                    var last28 = HabitRules.FlexibleStreak(h, today, 28, lowDays: null, TimeZoneInfo.Utc);
                    statsText = t.Format("habit.stats.weekly", done, target, last28, total);
                }
                else if (!showStreaks)
                {
                    var last7 = HabitRules.FlexibleStreak(h, today, 7, lowDays, tz);
                    var last30 = HabitRules.FlexibleStreak(h, today, 30, lowDays, tz);
                    statsText = t.Format("habit.stats.flexible", last7, last30, h.Logs.Count(l => l.Completed));
                }
                else
                {
                    var (streak, graceUsed) = HabitRules.ConsecutiveStreak(h, today, grace, lowDays);
                    var last7 = HabitRules.FlexibleStreak(h, today, 7, lowDays, tz);
                    var last30 = HabitRules.FlexibleStreak(h, today, 30, lowDays, tz);
                    var best = HabitRules.BestStreak(h, today, grace, lowDays);
                    var total = h.Logs.Count(l => l.Completed);
                    var graceStr = graceUsed > 0 ? "\n" + t.Plural("habit.stats.grace-used", graceUsed) : "";
                    statsText = t.Format("habit.stats.streak", HabitText.StreakText(t, streak), graceStr, last7, last30, best, total);
                }

                embed.AddField(
                    $"{(h.Kind == HabitKind.LetGo ? "🌿" : "🔥")} {h.Name} _({HabitText.FrequencyLabel(t, h)})_",
                    statsText,
                    inline: true);
            }

            embed.WithFooter(showStreaks ? t.Format("habit.stats.footer.grace", grace) : t.Get("habit.stats.footer"));
            embeds.Add(embed.Build());
        }

        return (embeds, toggle);
    }

    // ── /habit share ──────────────────────────────────────────────────────────

    [SlashCommand("share", "Share a habit card publicly in this channel")]
    public async Task ShareAsync()
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var habits = await _habits.GetHabitsAsync(userId);

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
        var habits = await _habits.GetHabitsAsync(userId);

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
        var habits = await _habits.GetHabitsAsync(userId);

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
        var habits = await _habits.GetHabitsAsync(userId);

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
        var habits = await _habits.GetHabitsAsync(userId);

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
