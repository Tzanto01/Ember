using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Ember.Bot.Modules;

/// <summary>
/// Handles all post-select-menu button interactions and modals for habit management.
/// Custom ID patterns:
///   habitaction:checkin:done:{habitId}
///   habitaction:checkin:skip:{habitId}
///   habitaction:slip:{habitId}  (a let-go habit's "I slipped" from /habit checkin: today's slip, then the gentle line in place)
///   letgo:slip:{habitId}  (the same from a /habit list button, answered as a new ephemeral message)
///   habitaction:delete:confirm:{habitId}
///   habitaction:delete:cancel
///   habitaction:edit:rename:{habitId}
///   habitaction:pause:{habitId}:{duration}   (today | 3days | 1week | resume)
///   habitaction:freq:{habitId}:daily
///   habit:grace:{days}
///   habit:streaks:{on|off}  (sets the account's ShowStreaks and re-renders the /habit streak reply)
///   tip:streaks:{on|off}  (answers the one-time streaks question: sets ShowStreaks and drops the buttons)
///   habit:add:modal  (modal submit)
///   habit:rename:modal:{habitId}  (modal submit)
/// </summary>
public class HabitActionModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly IConfiguration _config;
    private readonly ILogger<HabitActionModule> _logger;

    public HabitActionModule(HabitService habits, ConsentService consent, UserSettingsService settings, IConfiguration config, ILogger<HabitActionModule> logger)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
        _config = config;
        _logger = logger;
    }

    // ── habit:add:modal ───────────────────────────────────────────────────────

    [ModalInteraction(HabitIds.AddModal)]
    public async Task OnAddModalAsync(HabitAddModal modal)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var name = modal.HabitName.Trim();
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        if (string.IsNullOrWhiteSpace(name))
        {
            await RespondAsync(t.Get("habit.add.empty"), ephemeral: true);
            return;
        }

        var (result, added) = await _habits.AddHabitAsync(userId, name, reminderTime: null);
        if (result == HabitWriteResult.ConsentRequired)
        {
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
            return;
        }
        if (result == HabitWriteResult.LimitReached)
        {
            await RespondAsync(t.Format("habit.limit", Limits.HabitsPerUser), ephemeral: true);
            return;
        }
        var habit = added!;
        var hasTimezone = await _settings.HasTimezoneSetAsync(userId);

        var embed = new EmbedBuilder()
            .WithColor(0xE8873A)
            .WithTitle(t.Get("habit.add.started.title"))
            .WithDescription(t.Format("habit.add.started.body", habit.Name))
            .WithFooter(t.Get("habit.add.started.footer"))
            .Build();

        ComponentBuilder components;

        if (!hasTimezone)
        {
            components = new ComponentBuilder()
                .WithButton(t.Get("common.timezone-first"), OnboardingIds.Timezone(habit.Id), ButtonStyle.Primary, new Emoji("🌍"))
                .WithButton(t.Get("common.skip-for-now"), ReminderIds.Skip(habit.Id), ButtonStyle.Secondary);
        }
        else
        {
            components = new ComponentBuilder()
                .WithButton(t.Get("common.set-reminder"), ReminderIds.Set(habit.Id), ButtonStyle.Primary, new Emoji("⏰"))
                .WithButton(t.Get("common.not-now"), ReminderIds.Skip(habit.Id), ButtonStyle.Secondary);
        }

        components.WithButton(t.Get("common.days-and-times"), FrequencyIds.Menu(habit.Id), ButtonStyle.Secondary, new Emoji("📅"), row: 1);
        await RespondAsync(embed: embed, components: components.Build(), ephemeral: true);

        // DM confirmation / onboarding
        var allHabits = await _habits.GetHabitsAsync(userId);
        if (allHabits.Count == 1)
        {
            try
            {
                var dmEmbed = new EmbedBuilder()
                    .WithColor(0xE8873A)
                    .WithTitle(t.Get("habit.welcome.title"))
                    .WithDescription(t.Format("habit.welcome.body", habit.Name))
                    .WithFooter(t.Get("habit.welcome.footer"))
                    .Build();
                await SendWelcomeDmAsync(dmEmbed);
            }
            catch (Exception ex) when (DiscordFailures.IsRequestFailure(ex))
            {
                _logger.LogDebug(ex, "Could not send the first-habit welcome DM for interaction {InteractionId}.", Context.Interaction.Id);
            }
        }

        await AskStreaksOnceAsync(Context.Interaction, _settings, userId, t);
    }

    // ── habitaction:checkin:done:{habitId} ────────────────────────────────────

    [ComponentInteraction(HabitActionIds.CheckInDonePattern)]
    public async Task OnCheckinDoneAsync(string habitIdStr)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var guildId = Context.Guild?.Id;
        var today = await _settings.GetLocalTodayAsync(userId);
        var result = await _habits.AddCheckInAsync(userId, habitId, today, guildId);
        if (result == DayEditResult.ConsentRequired)
        {
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
            return;
        }
        if (result != DayEditResult.Saved)
        {
            await NotFoundAsync(t);
            return;
        }

        var text = HabitText.CheckInText(t, await _habits.GetHabitAsync(userId, habitId), today, t.Get("common.logged-today"));
        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = text;
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:checkin:skip:{habitId} ────────────────────────────────────

    [ComponentInteraction(HabitActionIds.CheckInSkipPattern)]
    public async Task OnCheckinSkipAsync(string habitIdStr)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var guildId = Context.Guild?.Id;
        var today = await _settings.GetLocalTodayAsync(userId);
        var result = await _habits.CheckInAsync(userId, habitId, today, completed: false, guildId);
        if (result == DayEditResult.ConsentRequired)
        {
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
            return;
        }
        if (result != DayEditResult.Saved)
        {
            await NotFoundAsync(t);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Get("common.skipped-today");
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:slip:{habitId} / letgo:slip:{habitId} ──────────────────────

    [ComponentInteraction(HabitActionIds.SlipPattern)]
    public async Task OnSlipAsync(string habitIdStr) => await SlipAsync(habitIdStr, inPlace: true);

    [ComponentInteraction(LetGoIds.SlipPattern)]
    public async Task OnListSlipAsync(string habitIdStr) => await SlipAsync(habitIdStr, inPlace: false);

    /// <summary>Records one slip without a reason on the user's local today and answers with <see cref="HabitText.SlipNotedText"/>.</summary>
    private async Task SlipAsync(string habitIdStr, bool inPlace)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var today = await _settings.GetLocalTodayAsync(userId);
        var result = await _habits.AddSlipAsync(userId, habitId, today);
        if (result == SlipResult.ConsentRequired)
        {
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
            return;
        }
        if (result != SlipResult.Saved || await _habits.GetHabitAsync(userId, habitId) is not { } habit)
        {
            await NotFoundAsync(t);
            return;
        }

        var text = HabitText.SlipNotedText(t,
            LetGoRules.StatsOn(habit, today, await _settings.GetTimeZoneAsync(userId)).LongestStretch);
        if (!inPlace)
        {
            await RespondAsync(text, ephemeral: true);
            return;
        }

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = text;
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:delete:confirm:{habitId} ──────────────────────────────────

    [ComponentInteraction(HabitActionIds.DeleteConfirmPattern)]
    public async Task OnDeleteConfirmAsync(string habitIdStr)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var deleted = await _habits.DeleteHabitAsync(userId, habitId);

        if (!deleted) { await NotFoundAsync(t); return; }

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Get("habit.delete.done");
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:delete:cancel ─────────────────────────────────────────────

    [ComponentInteraction(HabitActionIds.DeleteCancel)]
    public async Task OnDeleteCancelAsync()
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Get("habit.delete.cancelled");
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:edit:rename:{habitId} ─────────────────────────────────────

    [ComponentInteraction(HabitActionIds.RenamePattern)]
    public async Task OnEditRenameAsync(string habitIdStr)
    {
        var sourceMessageId = ((SocketMessageComponent)Context.Interaction).Message.Id;
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        var modal = new ModalBuilder()
            .WithTitle(t.Get("habit.rename.modal.title"))
            .WithCustomId(HabitIds.RenameModal(habitIdStr, sourceMessageId))
            .AddTextInput(t.Get("habit.rename.modal.label"), "habit_name",
                minLength: 1, maxLength: Limits.HabitNameLength, required: true)
            .Build();

        await RespondWithModalAsync(modal);
        try
        {
            await DeleteOriginalResponseAsync();
        }
        catch (Exception ex) when (DiscordFailures.IsRequestFailure(ex))
        {
            _logger.LogDebug(ex, "Could not delete the rename message for interaction {InteractionId}.", Context.Interaction.Id);
        }
    }

    // ── habit:rename:modal:{habitId}:{sourceMessageId} ────────────────────────

    [ModalInteraction(HabitIds.RenameModalPattern)]
    public async Task OnRenameModalAsync(string habitIdStr, string _, HabitRenameModal modal)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var name = modal.HabitName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            await RespondAsync(t.Get("habit.rename.empty"), ephemeral: true);
            return;
        }

        var (result, habit) = await _habits.EditHabitAsync(userId, habitId, newName: name, newReminderTime: null, clearReminder: false);
        if (result == HabitWriteResult.ConsentRequired)
        {
            await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
            return;
        }

        if (habit is null) { await NotFoundAsync(t); return; }

        var embed = new EmbedBuilder()
            .WithColor(0xF4845F)
            .WithTitle(t.Get("habit.rename.done.title"))
            .WithDescription(t.Format("habit.rename.done.body", habit.Name))
            .Build();

        await RespondAsync(embed: embed, ephemeral: true);
    }

    // ── habitaction:pause:{habitId}:{duration} ────────────────────────────────

    [ComponentInteraction(HabitActionIds.PausePattern)]
    public async Task OnPauseAsync(string habitIdStr, string duration)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }

        var today = await _settings.GetLocalTodayAsync(userId);
        DateOnly? pausedUntil = duration switch
        {
            "today" => (DateOnly?)today,
            "3days" => today.AddDays(2),
            "1week" => today.AddDays(6),
            "resume" => null,
            _ => today.AddDays(6)
        };

        var habit = await _habits.PauseHabitAsync(userId, habitId, pausedUntil);

        if (habit is null) { await NotFoundAsync(t); return; }

        string message = duration == "resume"
            ? t.Format("habit.pause.resumed", habit.Name)
            : t.Format("habit.pause.paused", habit.Name, pausedUntil?.ToString(t.Get("date.month-day"), t.Culture));

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = message;
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habitaction:freq:{habitId}:daily ─────────────────────────────────────

    [ComponentInteraction(HabitActionIds.FrequencyPattern)]
    public async Task OnFreqDailyAsync(string habitIdStr, string freqType)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(habitIdStr, out var habitId)) { await BadIdAsync(t); return; }
        if (freqType != "daily") { await BadIdAsync(t); return; }

        var current = await _habits.GetHabitAsync(userId, habitId);
        var habit = current is null ? null
            : await _habits.SetScheduleAsync(userId, habitId, FrequencyType.Daily, null, null, current.TimesPerDay);

        if (habit is null) { await NotFoundAsync(t); return; }

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.frequency.daily-set", habit.Name);
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habit:grace:{days} ────────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.GracePattern)]
    public async Task OnGraceAsync(string daysStr)
    {
        if (!int.TryParse(daysStr, out var days) || days < 0 || days > 3)
        {
            await RespondAsync((await BotTexts.ForAsync(_habits, Context.Interaction)).Get("habit.grace.invalid"), ephemeral: true);
            return;
        }

        await _settings.SetGraceDaysAsync(await _habits.ResolveOrCreateDiscordUserAsync(Context.User.Id, Languages.FromLocale(Context.Interaction.UserLocale)), days);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.grace.set", days, t.Get($"habit.grace.desc.{days}"));
                m.Components = new ComponentBuilder().Build();
            });
    }

    // ── habit:streaks:{on|off} ────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.StreaksPattern)]
    public async Task OnStreaksAsync(string state)
    {
        if (state is not ("on" or "off")) { await BadIdAsync(await BotTexts.ForAsync(_habits, Context.Interaction)); return; }

        var userId = await _habits.ResolveOrCreateDiscordUserAsync(Context.User.Id, Languages.FromLocale(Context.Interaction.UserLocale));
        await _settings.SetShowStreaksAsync(userId, state == "on");
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        var (pages, toggle) = await HabitModule.RenderStatsAsync(t, _habits, _settings, userId);

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = pages.Count == 0 ? t.Get("habit.stats.none") : null;
                m.Embeds = pages.Take(1).ToArray();
                m.Components = pages.Count == 0 ? new ComponentBuilder().Build() : toggle;
            });
        foreach (var page in pages.Skip(1))
            await FollowupAsync(embed: page, ephemeral: true);
    }

    // ── tip:streaks:{on|off} ──────────────────────────────────────────────────

    [ComponentInteraction(TipIds.StreaksPattern)]
    public async Task OnStreaksTipAsync(string state)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (state is not ("on" or "off")) { await BadIdAsync(t); return; }

        await _settings.SetShowStreaksAsync(await _habits.ResolveOrCreateDiscordUserAsync(Context.User.Id, Languages.FromLocale(Context.Interaction.UserLocale)), state == "on");
        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Get(state == "on" ? "habit.streaks-tip.on" : "habit.streaks-tip.off");
                m.Components = new ComponentBuilder().Build();
            });
    }

    /// <summary>
    /// Asks the one-time streaks question as an ephemeral follow-up when <see cref="UserSettingsService.NeedsStreaksQuestionAsync"/>
    /// says the account needs it; the answer comes back as <c>tip:streaks:{on|off}</c>.
    /// </summary>
    internal static async Task AskStreaksOnceAsync(IDiscordInteraction interaction, UserSettingsService settings, Guid userId, Texts t)
    {
        if (!await settings.NeedsStreaksQuestionAsync(userId)) return;

        var buttons = new ComponentBuilder()
            .WithButton(t.Get("habit.streaks-tip.show"), TipIds.Streaks(show: true), ButtonStyle.Primary)
            .WithButton(t.Get("habit.streaks-tip.not-for-me"), TipIds.Streaks(show: false), ButtonStyle.Secondary)
            .Build();
        await interaction.FollowupAsync(t.Get("habit.streaks-tip.question"), components: buttons, ephemeral: true);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Sends the welcome DM for the user's first habit.</summary>
    protected virtual async Task SendWelcomeDmAsync(Embed embed)
    {
        var dm = await Context.User.CreateDMChannelAsync();
        await dm.SendMessageAsync(embed: embed);
    }

    private async Task BadIdAsync(Texts t) =>
        await RespondAsync(t.Get("common.retry.dash"), ephemeral: true);

    private async Task NotFoundAsync(Texts t) =>
        await RespondAsync(t.Get("common.not-found"), ephemeral: true);
}

public class HabitAddModal : IModal
{
    public string Title => "Add a new habit";

    [InputLabel("What habit do you want to track?")]
    [ModalTextInput("habit_name", placeholder: "e.g. Morning walk, Read 10 pages, Drink water")]
    public string HabitName { get; set; } = "";
}

public class HabitRenameModal : IModal
{
    public string Title => "Rename habit";

    [InputLabel("New name")]
    [ModalTextInput("habit_name")]
    public string HabitName { get; set; } = "";
}
