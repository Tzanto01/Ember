using Discord;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Bot.Discord.Messages;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;

namespace Ember.Bot.Modules;

/// <summary>
/// Handles habit:select:{action} select menus produced by HabitModule commands.
/// Routes to the appropriate follow-up buttons, modals, or immediate actions.
/// </summary>
public class HabitSelectModule : InteractionModuleBase<SocketInteractionContext>
{
    private readonly HabitService _habits;
    private readonly UserSettingsService _settings;

    public HabitSelectModule(HabitService habits, UserSettingsService settings)
    {
        _habits = habits;
        _settings = settings;
    }

    // ── habit:select:checkin ──────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectCheckIn)]
    public async Task OnSelectCheckinAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit is null) { await NotFoundAsync(t); return; }

        if (habit.Kind == HabitKind.LetGo)
        {
            var today = await _settings.GetLocalTodayAsync(userId);
            var tz = await _settings.GetTimeZoneAsync(userId);
            var daysFree = LetGoRules.StatsOn(habit, today, tz).DaysFree;
            await ((SocketMessageComponent)Context.Interaction)
                .UpdateAsync(m =>
                {
                    m.Content = t.Format("habit.letgo.question", habit.Name, HabitText.DaysFreeText(t, daysFree));
                    m.Components = LetGoButtons.Question(t, habit, LetGoRules.DayOn(habit, today, tz));
                });
            return;
        }

        var components = new ComponentBuilder()
            .WithButton(t.Get("habit.checkin.done-button"), HabitActionIds.CheckInDone(habitId), ButtonStyle.Success)
            .WithButton(ReminderDm.SkipLabel(t), HabitActionIds.CheckInSkip(habitId), ButtonStyle.Secondary)
            .Build();

        var progress = habit.TimesPerDay > 1
            ? " " + t.Format("habit.checkin.so-far", HabitText.DayMark(habit, await _settings.GetLocalTodayAsync(userId)))
            : "";
        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.checkin.question", habit.Name) + progress;
                m.Components = components;
            });
    }

    // ── habit:select:share ────────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectShare)]
    public async Task OnSelectShareAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        var grace = await _settings.GetGraceDaysAsync(userId);

        if (habit is null) { await NotFoundAsync(t); return; }

        var today = await _settings.GetLocalTodayAsync(userId);
        var embed = BuildShareEmbed(t, habit, grace, Context.User.Username, today, await _habits.GetLowDaysAsync(userId),
            await _settings.GetShowStreaksAsync(userId), await _settings.GetTimeZoneAsync(userId));
        var component = (SocketMessageComponent)Context.Interaction;

        await component.DeferAsync();
        string reply;
        try
        {
            await component.FollowupAsync(embed: embed);
            reply = t.Get("habit.share.posted");
        }
        catch (HttpException)
        {
            reply = t.Get("habit.share.failed");
        }

        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = reply;
            m.Components = new ComponentBuilder().Build();
        });
    }

    // ── habit:select:delete ───────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectDelete)]
    public async Task OnSelectDeleteAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit is null) { await NotFoundAsync(t); return; }

        var components = new ComponentBuilder()
            .WithButton(t.Get("habit.delete.confirm-button"), HabitActionIds.DeleteConfirm(habitId), ButtonStyle.Danger)
            .WithButton(t.Get("common.cancel"), HabitActionIds.DeleteCancel, ButtonStyle.Secondary)
            .Build();

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.delete.confirm", habit.Name);
                m.Components = components;
            });
    }

    // ── habit:select:edit ─────────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectEdit)]
    public async Task OnSelectEditAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit is null) { await NotFoundAsync(t); return; }
        if (habit.Kind == HabitKind.LetGo) { await LetGoWebOnlyAsync(t); return; }

        var components = new ComponentBuilder()
            .WithButton(t.Get("habit.edit.rename"), HabitActionIds.Rename(habitId), ButtonStyle.Primary, new Emoji("✏️"))
            .WithButton(t.Get("habit.edit.change-reminder"), ReminderIds.Set(habitId), ButtonStyle.Secondary, new Emoji("⏰"))
            .WithButton(t.Get("habit.edit.remove-reminder"), ReminderIds.Clear(habitId), ButtonStyle.Danger)
            .WithButton(t.Get("common.cancel"), ReminderIds.Cancel(habitId), ButtonStyle.Secondary)
            .Build();

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.edit.question", habit.Name);
                m.Components = components;
            });
    }

    // ── habit:select:pause ────────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectPause)]
    public async Task OnSelectPauseAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit is null) { await NotFoundAsync(t); return; }

        var components = PauseComponents(t, habit, await _settings.GetLocalTodayAsync(userId));

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.pause.question", habit.Name);
                m.Components = components;
            });
    }

    /// <summary>
    /// The pause buttons (today, 3 days, 1 week), plus Resume while the habit is paused on the user's local <paramref name="today"/>
    /// (<see cref="HabitRules.IsPausedOn"/>), not before a pause that starts later.
    /// </summary>
    internal static MessageComponent PauseComponents(Texts t, Habit habit, DateOnly today)
    {
        var builder = new ComponentBuilder()
            .WithButton(t.Get("habit.pause.today"), HabitActionIds.Pause(habit.Id, "today"), ButtonStyle.Secondary)
            .WithButton(t.Get("habit.pause.3days"), HabitActionIds.Pause(habit.Id, "3days"), ButtonStyle.Secondary)
            .WithButton(t.Get("habit.pause.1week"), HabitActionIds.Pause(habit.Id, "1week"), ButtonStyle.Primary);

        if (HabitRules.IsPausedOn(habit, today))
            builder.WithButton(t.Get("habit.pause.resume-button"), HabitActionIds.Pause(habit.Id, "resume"), ButtonStyle.Success);
        return builder.Build();
    }

    // ── habit:select:frequency ────────────────────────────────────────────────

    [ComponentInteraction(HabitIds.SelectFrequency)]
    public async Task OnSelectFrequencyAsync(string[] values)
    {
        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }

        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit is null) { await NotFoundAsync(t); return; }
        if (habit.Kind == HabitKind.LetGo) { await LetGoWebOnlyAsync(t); return; }

        var components = FrequencyMenu.Buttons(t, habitId);

        await ((SocketMessageComponent)Context.Interaction)
            .UpdateAsync(m =>
            {
                m.Content = t.Format("habit.frequency.question", habit.Name);
                m.Components = components;
            });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task BadIdAsync(Texts t) =>
        await RespondAsync(t.Get("common.retry.dash"), ephemeral: true);

    private async Task NotFoundAsync(Texts t) =>
        await RespondAsync(t.Get("common.not-found"), ephemeral: true);

    private async Task LetGoWebOnlyAsync(Texts t) =>
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = HabitText.LetGoWebOnlyText(t);
            m.Components = new ComponentBuilder().Build();
        });

    private static EmbedBuilder BuildShareEmbedBase(Texts t, Habit habit, int grace, string username, DateOnly today, IReadOnlySet<DateOnly> lowDays,
        bool showStreaks, TimeZoneInfo tz)
    {
        if (habit.Kind == HabitKind.LetGo)
        {
            var stats = LetGoRules.StatsOn(habit, today, tz);
            return new EmbedBuilder()
                .WithColor(0xE8873A)
                .WithTitle($"🌿 {habit.Name} _({HabitText.FrequencyLabel(t, habit)})_")
                .WithDescription($"**{HabitText.DaysFreeText(t, stats.DaysFree)}**\n\n{t.Format("habit.letgo.share.stats", stats.LongestStretch, stats.FreeDaysThisMonth)}")
                .WithFooter(t.Format("habit.share.footer", username))
                .WithTimestamp(DateTimeOffset.UtcNow);
        }

        var from = today.AddDays(-29);

        var completed = habit.Logs
            .Where(l => l.Completed && l.Date >= from)
            .Select(l => l.Date)
            .ToHashSet();

        var rows = new List<string>();
        for (int row = 0; row < 3; row++)
        {
            var dots = new System.Text.StringBuilder();
            for (int col = 0; col < 10; col++)
            {
                var d = from.AddDays(row * 10 + col);
                dots.Append(completed.Contains(d) ? "🟩"
                    : !HabitRules.IsScheduledOn(habit, d) ? "▫️"
                    : HabitRules.IsOpen(d, today) ? "⬜"
                    : "⬛");
            }
            rows.Add(dots.ToString());
        }
        var grid = string.Join("\n", rows);

        string titleLine, statsLine;

        if (habit.FrequencyType == FrequencyType.Weekly)
        {
            var (done, target) = HabitRules.WeeklyProgress(habit, today);
            var total = habit.Logs.Count(l => l.Completed);
            titleLine = t.Format("habit.share.weekly.title", done, target);
            statsLine = t.Format("habit.share.weekly.stats", HabitRules.FlexibleStreak(habit, today, 28, lowDays: null, TimeZoneInfo.Utc), total);
        }
        else if (!showStreaks)
        {
            var last30 = HabitRules.FlexibleStreak(habit, today, 30, lowDays, tz);
            titleLine = t.Format("habit.share.flexible.title", last30);
            statsLine = t.Format("habit.share.flexible.stats", last30, habit.Logs.Count(l => l.Completed));
        }
        else
        {
            var (streak, graceUsed) = HabitRules.ConsecutiveStreak(habit, today, grace, lowDays);
            var best = HabitRules.BestStreak(habit, today, grace, lowDays);
            var last30 = HabitRules.FlexibleStreak(habit, today, 30, lowDays, tz);
            var total = habit.Logs.Count(l => l.Completed);
            var graceStr = graceUsed > 0 ? " " + t.Plural("habit.progress.grace", graceUsed) : "";
            var fireCount = Math.Min(streak / 7 + 1, 5);
            titleLine = $"{t.Format("habit.share.streak", streak)}{graceStr} {string.Concat(Enumerable.Repeat("🔥", fireCount))}";
            statsLine = t.Format("habit.share.streak.stats", last30, best, total);
        }

        return new EmbedBuilder()
            .WithColor(0xE8873A)
            .WithTitle($"🔥 {habit.Name} _({HabitText.FrequencyLabel(t, habit)})_")
            .WithDescription($"{titleLine}\n\n{grid}\n\n{statsLine}")
            .WithFooter(t.Format("habit.share.footer", username))
            .WithTimestamp(DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// The public share card. Its "last 30 days" counts the user's <paramref name="lowDays"/> as kept, like <c>/habit streak</c>, while its
    /// grid shows them like any gap. A let-go habit's card shows days free, longest stretch and free days this month in <paramref name="tz"/>,
    /// never a streak.
    /// </summary>
    internal static Embed BuildShareEmbed(Texts t, Habit habit, int grace, string username, DateOnly today, IReadOnlySet<DateOnly> lowDays,
        bool showStreaks, TimeZoneInfo tz) =>
        BuildShareEmbedBase(t, habit, grace, username, today, lowDays, showStreaks, tz).Build();
}
