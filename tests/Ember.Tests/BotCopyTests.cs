using Ember.Bot;
using Ember.Bot.Discord.Formatting;
using Ember.Bot.Modules;
using Ember.Core.Localization;
using Ember.Core.Models;
using FluentAssertions;

namespace Ember.Tests;

public class BotCopyTests
{
    [Theory]
    [InlineData("9pm", 21, 0)]
    [InlineData("9:30 PM", 21, 30)]
    [InlineData("9:30pm", 21, 30)]
    [InlineData("12am", 0, 0)]
    [InlineData(" 21:30 ", 21, 30)]
    [InlineData("9:05", 9, 5)]
    public void ReminderTime_AcceptsTwelveAndTwentyFourHourInput(string input, int hour, int minute)
    {
        ReminderSetupModule.TryParseTime(input, out var time).Should().BeTrue();
        time.Should().Be(new TimeOnly(hour, minute));
    }

    [Theory]
    [InlineData("25:00")]
    [InlineData("13pm")]
    [InlineData("soon")]
    public void ReminderTime_RefusesWhatIsNoClockTime(string input)
        => ReminderSetupModule.TryParseTime(input, out _).Should().BeFalse();

    public static TheoryData<string> BothLanguages => [Languages.English, Languages.German];

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void ReminderConfirmation_NamesTheWeekdaysOfAWeekdaysHabit(string language)
    {
        var t = BotTexts.For(language);
        var weekdays = new Habit { FrequencyType = FrequencyType.Weekdays, Days = ScheduleDays.Mon | ScheduleDays.Wed };

        ReminderSetupModule.ScheduleText(t, weekdays)
            .Should().Be(Filled(t, "reminder.schedule.weekdays", $"{Text(t, "weekday.mon")}, {Text(t, "weekday.wed")}"));
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void ReminderConfirmation_SaysEveryDayForADailyHabit(string language)
    {
        var t = BotTexts.For(language);

        ReminderSetupModule.ScheduleText(t, new Habit()).Should().Be(Text(t, "reminder.schedule.daily"));
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void ReminderConfirmation_CountsTheWeeklyTargetInItsPluralForm(string language)
    {
        var t = BotTexts.For(language);

        ReminderSetupModule.ScheduleText(t, new Habit { FrequencyType = FrequencyType.Weekly, WeeklyTarget = 3 })
            .Should().Be(Filled(t, "reminder.schedule.weekly.other", 3));
        ReminderSetupModule.ScheduleText(t, new Habit { FrequencyType = FrequencyType.Weekly, WeeklyTarget = 1 })
            .Should().Be(Filled(t, "reminder.schedule.weekly.one", 1));
    }

    [Theory]
    [MemberData(nameof(BothLanguages))]
    public void Streak_OfOneDay_IsSingular(string language)
    {
        var t = BotTexts.For(language);

        HabitText.StreakText(t, 1).Should().Be(Filled(t, "habit.streak.one", 1));
        HabitText.StreakText(t, 2).Should().Be(Filled(t, "habit.streak.other", 2));
    }

    /// <summary>The bot's own text for <paramref name="key"/>; a key missing from the resources or an empty text fails here.</summary>
    private static string Text(Texts t, string key)
    {
        var text = t.Get(key);
        text.Should().NotBeNullOrWhiteSpace($"'{key}' is shown to {t.Language} users");
        return text;
    }

    /// <summary><see cref="Text"/> with <paramref name="arg"/> as its <c>{0}</c>, which the text must keep.</summary>
    private static string Filled(Texts t, string key, object arg)
    {
        var text = Text(t, key);
        text.Should().Contain("{0}", $"'{key}' shows {arg}");
        return string.Format(t.Culture, text, arg);
    }
}
