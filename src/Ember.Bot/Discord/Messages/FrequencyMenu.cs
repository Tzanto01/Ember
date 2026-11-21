using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Core.Localization;

namespace Ember.Bot.Discord.Messages;

/// <summary>The schedule menu under <c>/habit frequency</c> and a habit's "Days and times" button (<see cref="FrequencyIds"/>).</summary>
internal static class FrequencyMenu
{
    /// <summary>The schedule buttons: daily, 1–6× per week, specific weekdays and times per day.</summary>
    internal static MessageComponent Buttons(Texts t, int habitId) =>
        new ComponentBuilder()
            .WithButton(t.Get("freq.button.daily"), HabitActionIds.Daily(habitId), ButtonStyle.Primary, row: 0)
            .WithButton(t.Format("freq.button.weekly", 1), FrequencyIds.Weekly(habitId, 1), ButtonStyle.Secondary, row: 0)
            .WithButton(t.Format("freq.button.weekly", 2), FrequencyIds.Weekly(habitId, 2), ButtonStyle.Secondary, row: 0)
            .WithButton(t.Format("freq.button.weekly", 3), FrequencyIds.Weekly(habitId, 3), ButtonStyle.Secondary, row: 0)
            .WithButton(t.Format("freq.button.weekly", 4), FrequencyIds.Weekly(habitId, 4), ButtonStyle.Secondary, row: 0)
            .WithButton(t.Format("freq.button.weekly", 5), FrequencyIds.Weekly(habitId, 5), ButtonStyle.Secondary, row: 1)
            .WithButton(t.Format("freq.button.weekly", 6), FrequencyIds.Weekly(habitId, 6), ButtonStyle.Secondary, row: 1)
            .WithButton(t.Get("freq.button.weekdays"), FrequencyIds.Weekdays(habitId), ButtonStyle.Secondary, new Emoji("📅"), row: 2)
            .WithButton(t.Get("freq.button.times"), FrequencyIds.Times(habitId), ButtonStyle.Secondary, new Emoji("🔁"), row: 2)
            .Build();
}
