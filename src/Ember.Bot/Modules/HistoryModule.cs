using System.Globalization;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Core.Localization;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Modules;

/// <summary>
/// The /habit history grid: the last <see cref="Days"/> local days of one habit as buttons, plus a low-day menu.
/// Custom IDs: "history:day:{ownerId}:{habitId}:{yyyyMMdd}" (cycles not logged → one more check-in until done → not done → not logged)
/// and the select "history:low:{ownerId}:{habitId}" with yyyyMMdd values (toggles a low day).
/// </summary>
public class HistoryModule : InteractionModuleBase<SocketInteractionContext>
{
    internal const int Days = 14;
    private const string DateFormat = HistoryIds.DateFormat;

    private readonly HabitService _habits;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly IConfiguration _config;

    public HistoryModule(HabitService habits, ConsentService consent, UserSettingsService settings, IConfiguration config)
    {
        _habits = habits;
        _consent = consent;
        _settings = settings;
        _config = config;
    }

    [ComponentInteraction(HabitIds.SelectHistory)]
    public async Task OnSelectHistoryAsync(string[] values)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!int.TryParse(values[0], out var habitId)) { await BadIdAsync(t); return; }
        await RenderAsync(t, await _habits.FindDiscordUserAsync(Context.User.Id), habitId);
    }

    [ComponentInteraction(HistoryIds.DayPattern)]
    public async Task OnDayAsync(string ownerIdStr, string habitIdStr, string dateStr)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!IsOwner(ownerIdStr)) { await NotYoursAsync(t); return; }
        if (!int.TryParse(habitIdStr, out var habitId) || !TryParseDate(dateStr, out var date)) { await BadIdAsync(t); return; }

        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        switch (await CycleDayAsync(_habits, _settings, userId, habitId, date, Context.Guild?.Id))
        {
            case DayEditResult.ConsentRequired:
                await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
                return;
            case DayEditResult.HabitNotFound:
                await RespondAsync(t.Get("common.not-found"), ephemeral: true);
                return;
            case DayEditResult.BeforeHabitStart:
                await RespondAsync(t.Get("history.before-start"), ephemeral: true);
                return;
            case DayEditResult.InFuture:
                await RespondAsync(t.Get("history.in-future"), ephemeral: true);
                return;
        }

        await RenderAsync(t, userId, habitId);
    }

    [ComponentInteraction(HistoryIds.LowPattern)]
    public async Task OnLowDayAsync(string ownerIdStr, string habitIdStr, string[] values)
    {
        var t = await BotTexts.ForAsync(_habits, Context.Interaction);
        if (!IsOwner(ownerIdStr)) { await NotYoursAsync(t); return; }
        if (!int.TryParse(habitIdStr, out var habitId) || !TryParseDate(values[0], out var date)) { await BadIdAsync(t); return; }

        var userId = await _habits.FindDiscordUserAsync(Context.User.Id);
        switch (await ToggleLowDayAsync(_habits, userId, date))
        {
            case DayEditResult.ConsentRequired:
                await ConsentModule.RespondWithPromptAsync(Context, _config, _habits, _consent);
                return;
            case DayEditResult.InFuture:
                await RespondAsync(t.Get("history.in-future"), ephemeral: true);
                return;
        }

        await RenderAsync(t, userId, habitId);
    }

    private async Task RenderAsync(Texts t, Guid userId, int habitId)
    {
        var habit = await _habits.GetHabitAsync(userId, habitId);
        if (habit?.Kind == Ember.Core.Models.HabitKind.LetGo)
        {
            var today = await _settings.GetLocalTodayAsync(userId);
            var tz = await _settings.GetTimeZoneAsync(userId);
            await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
            {
                m.Content = t.Format("history.letgo.grid", habit.Name, Days,
                    HabitText.DaysFreeText(t, LetGoRules.StatsOn(habit, today, tz).DaysFree));
                m.Components = BuildLetGoGrid(t, habit, today, tz);
            });
            return;
        }
        var days = await _habits.GetHabitDaysAsync(userId, habitId, Days);
        if (habit is null || days is null)
        {
            await RespondAsync(t.Get("common.not-found"), ephemeral: true);
            return;
        }

        var lowDays = await _habits.GetLowDaysAsync(userId);
        await ((SocketMessageComponent)Context.Interaction).UpdateAsync(m =>
        {
            m.Content = t.Format("history.grid", habit.Name, Days);
            m.Components = BuildGrid(t, Context.User.Id, habitId, days, lowDays);
        });
    }

    /// <summary>Moves the day's log one step along not logged → one more check-in (until done) → not done → not logged.</summary>
    internal static async Task<DayEditResult> CycleDayAsync(HabitService habits, UserSettingsService settings, Guid userId, int habitId, DateOnly date,
        ulong? guildId)
    {
        var today = await settings.GetLocalTodayAsync(userId);
        if (date > today) return DayEditResult.InFuture;

        var days = await habits.GetHabitDaysAsync(userId, habitId, today.DayNumber - date.DayNumber + 1);
        if (days is null) return DayEditResult.HabitNotFound;

        return days[0].Completed switch
        {
            null => await habits.AddCheckInAsync(userId, habitId, date, guildId),
            true => await habits.CheckInAsync(userId, habitId, date, completed: false, guildId),
            false => await habits.ClearDayAsync(userId, habitId, date),
        };
    }

    /// <summary>Sets a low day on <paramref name="date"/>, or removes it when it is already one.</summary>
    internal static async Task<DayEditResult> ToggleLowDayAsync(HabitService habits, Guid userId, DateOnly date)
    {
        if (!(await habits.GetLowDaysAsync(userId)).Contains(date))
            return await habits.SetLowDayAsync(userId, date);

        await habits.ClearLowDayAsync(userId, date);
        return DayEditResult.Saved;
    }

    /// <summary>Builds the day buttons (5 per row) and the low-day menu for <paramref name="days"/>, oldest first.</summary>
    internal static MessageComponent BuildGrid(Texts t, ulong userId, int habitId, IReadOnlyList<HabitDay> days, IReadOnlySet<DateOnly> lowDays)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            builder.WithButton(
                DayLabel(t, day),
                HistoryIds.Day(userId, habitId, day.Date),
                day.State == DayState.Done ? ButtonStyle.Success : ButtonStyle.Secondary,
                disabled: day.State == DayState.BeforeStart,
                row: i / 5);
        }

        var menu = new SelectMenuBuilder()
            .WithCustomId(HistoryIds.Low(userId, habitId))
            .WithPlaceholder(t.Get("history.low.placeholder"))
            .WithMinValues(1)
            .WithMaxValues(1);
        foreach (var day in days.Reverse())
            menu.AddOption(
                day.Date.ToString(t.Get("date.short"), t.Culture),
                day.Date.ToString(DateFormat, CultureInfo.InvariantCulture),
                lowDays.Contains(day.Date) ? t.Get("history.low.marked") : null);

        return builder.WithSelectMenu(menu, row: (days.Count + 4) / 5).Build();
    }

    /// <summary>The last <see cref="Days"/> local days of a let-go habit as read-only buttons with soft marks (<see cref="HabitText.LetGoMark"/>), oldest first.</summary>
    internal static MessageComponent BuildLetGoGrid(Texts t, Core.Models.Habit habit, DateOnly today, TimeZoneInfo tz)
    {
        var builder = new ComponentBuilder();
        for (var i = 0; i < Days; i++)
        {
            var day = LetGoRules.DayOn(habit, today.AddDays(i + 1 - Days), tz);
            var date = day.Date.ToString(t.Get("date.button"), t.Culture);
            builder.WithButton(
                day.State == LetGoDayState.BeforeStart ? date : $"{date} {HabitText.LetGoMark(day.State)}",
                HistoryIds.LetGo(habit.Id, day.Date),
                ButtonStyle.Secondary,
                disabled: true,
                row: i / 5);
        }
        return builder.Build();
    }

    private static string DayLabel(Texts t, HabitDay day)
    {
        var date = day.Date.ToString(t.Get("date.button"), t.Culture);
        var unlogged = day.Completed is null && day.Count == 0;
        var log = day.Completed switch { true => "✅", false => "➖", null => unlogged ? "▫️" : $"🔸{day.Count}" };
        var symbol = day.State switch
        {
            DayState.BeforeStart => "",
            DayState.LowDay => unlogged ? "🌙" : "🌙" + log,
            DayState.Paused => unlogged ? "⏸️" : "⏸️" + log,
            DayState.Offday => "💤",
            _ => log,
        };
        return symbol.Length == 0 ? date : $"{date} {symbol}";
    }

    private bool IsOwner(string ownerIdStr) => ulong.TryParse(ownerIdStr, out var ownerId) && ownerId == Context.User.Id;

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    private async Task NotYoursAsync(Texts t) =>
        await RespondAsync(t.Get("common.not-yours"), ephemeral: true);

    private async Task BadIdAsync(Texts t) =>
        await RespondAsync(t.Get("common.retry.comma"), ephemeral: true);
}
