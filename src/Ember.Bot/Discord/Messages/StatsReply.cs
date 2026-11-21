using Discord;
using Ember.Bot.Discord.CustomIds;
using Ember.Bot.Discord.Formatting;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;

namespace Ember.Bot.Discord.Messages;

/// <summary>The <c>/habit streak</c> stats, shared by the command and its streaks toggle (<see cref="HabitIds.Streaks"/>).</summary>
internal static class StatsReply
{
    /// <summary>
    /// The <c>/habit streak</c> reply: one stats embed per page of habits (none without habits) and the button that flips
    /// <see cref="User.ShowStreaks"/> ("habit:streaks:on" / "habit:streaks:off"). With streaks off the stats have no streak lines.
    /// </summary>
    internal static async Task<(List<Embed> Pages, MessageComponent Toggle)> RenderAsync(Texts t, HabitService service, UserSettingsService settings,
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
}
