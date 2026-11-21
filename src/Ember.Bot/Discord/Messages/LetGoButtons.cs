using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;

namespace Ember.Bot.Discord.Messages;

/// <summary>
/// The buttons of let-go habits: "I slipped" and "Made it today" under <c>/habit checkin</c> and <c>/habit list</c>, and the undo
/// of a made-it mark (<see cref="LetGoIds"/>, <see cref="HabitActionIds"/>). "Made it today" is offered only on a free day
/// (<see cref="LetGoDayState.Free"/>), the only day the mark counts on.
/// </summary>
internal static class LetGoButtons
{
    /// <summary>
    /// The buttons under a let-go habit picked in <c>/habit checkin</c>: "I slipped", and on a free <paramref name="today"/> "Made it
    /// today", or once today is marked, the same label in green with a check that takes the mark back.
    /// </summary>
    internal static MessageComponent Question(Texts t, Habit habit, LetGoDay today)
    {
        var builder = new ComponentBuilder()
            .WithButton(t.Get("habit.letgo.slip-button"), HabitActionIds.Slip(habit.Id), ButtonStyle.Secondary);
        if (today.State != LetGoDayState.Free) return builder.Build();
        return (today.MadeIt
                ? builder.WithButton($"{t.Get("habit.letgo.made-it-button")} ✓", LetGoIds.UndoMadeIt(habit.Id, today.Date), ButtonStyle.Success)
                : builder.WithButton(t.Get("habit.letgo.made-it-button"), HabitActionIds.MadeIt(habit.Id), ButtonStyle.Secondary))
            .Build();
    }

    /// <summary>
    /// Under one page of <c>/habit list</c> (at most eight habits): an "I slipped: {name}" button per let-go habit, then a "Made it
    /// today: {name}" button per let-go habit whose local <paramref name="today"/> is free and not marked yet. Null without let-go habits.
    /// </summary>
    internal static MessageComponent? List(Texts t, IReadOnlyList<Habit> habits, DateOnly today, TimeZoneInfo tz)
    {
        var letGo = habits.Where(h => h.Kind == HabitKind.LetGo).ToList();
        if (letGo.Count == 0) return null;
        var builder = new ComponentBuilder();
        for (var i = 0; i < letGo.Count; i++)
            builder.WithButton(Label(t.Format("habit.letgo.slip-button.named", letGo[i].Name)), LetGoIds.Slip(letGo[i].Id), ButtonStyle.Secondary, row: i / 5);

        var markable = letGo.Where(h => LetGoRules.DayOn(h, today, tz) is { State: LetGoDayState.Free, MadeIt: false }).ToList();
        var firstRow = (letGo.Count + 4) / 5;
        for (var i = 0; i < markable.Count; i++)
            builder.WithButton(Label(t.Format("habit.letgo.made-it-button.named", markable[i].Name)), LetGoIds.MadeIt(markable[i].Id),
                ButtonStyle.Secondary, row: firstRow + i / 5);
        return builder.Build();
    }

    /// <summary>The undo under the made-it answer: takes back the mark of <paramref name="date"/>, the day it was set on.</summary>
    internal static MessageComponent Undo(Texts t, int habitId, DateOnly date) => new ComponentBuilder()
        .WithButton(t.Get("habit.letgo.made-it.undo-button"), LetGoIds.UndoMadeIt(habitId, date), ButtonStyle.Secondary)
        .Build();

    private static string Label(string label)
        => label.Length > ButtonBuilder.MaxButtonLabelLength ? label[..(ButtonBuilder.MaxButtonLabelLength - 1)] + "…" : label;
}
