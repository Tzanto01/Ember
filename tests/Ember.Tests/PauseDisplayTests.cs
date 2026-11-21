using Discord;
using Ember.Bot.Discord.Formatting;
using Ember.Bot.Modules;
using FluentAssertions;

namespace Ember.Tests;

/// <summary>The bot shows a habit as paused only on the days Core counts as paused (<c>HabitRules.IsPausedOn</c>).</summary>
public class PauseDisplayTests
{
    private static IEnumerable<string> ButtonLabels(MessageComponent components)
        => components.Components.OfType<ActionRowComponent>().SelectMany(r => r.Components).OfType<ButtonComponent>().Select(b => b.Label);

    [Theory]
    [InlineData(-2, 3, " · ⏸️ paused until Jun 21")]
    [InlineData(0, 3, " · ⏸️ paused until Jun 21")]
    [InlineData(2, 5, "")]
    [InlineData(-5, -1, "")]
    public void The_habit_list_shows_paused_until_only_from_the_pause_start(int fromDaysAhead, int untilDaysAhead, string expected)
    {
        var habit = HabitFactory.Make();
        habit.PausedFrom = HabitFactory.Today.AddDays(fromDaysAhead);
        habit.PausedUntil = HabitFactory.Today.AddDays(untilDaysAhead);

        HabitText.PausedSuffix(Ember.Bot.BotTexts.English, habit, HabitFactory.Today).Should().Be(expected);
    }

    [Theory]
    [InlineData(-2, true)]
    [InlineData(2, false)]
    public void The_pause_menu_offers_resume_only_from_the_pause_start(int fromDaysAhead, bool resume)
    {
        var habit = HabitFactory.Make();
        habit.PausedFrom = HabitFactory.Today.AddDays(fromDaysAhead);
        habit.PausedUntil = HabitFactory.Today.AddDays(5);

        ButtonLabels(HabitSelectModule.PauseComponents(Ember.Bot.BotTexts.English, habit, HabitFactory.Today))
            .Contains(Ember.Bot.BotTexts.English.Get("habit.pause.resume-button")).Should().Be(resume);
    }
}
