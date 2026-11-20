using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>
/// Keeps docs/contracts/fixtures/rules-v1.json (the rules the no-account client reimplements) equal to what Core computes.
/// With <c>EMBER_WRITE_FIXTURES=1</c> it rewrites each case's <c>expected</c> from the case's inputs instead of asserting.
/// The edit cases run through <see cref="HabitService"/> on an in-memory SQLite host, hence the integration collection.
/// </summary>
[Collection("Integration")]
public partial class RulesFixtureTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record LogInput(DateOnly Date, int Count, bool Completed);

    private sealed record HabitInput(int Id, string Frequency, int? WeeklyTarget, List<string>? Days, int TimesPerDay, DateOnly StartDate,
        DateOnly? PausedFrom, DateOnly? PausedUntil, int? RoutineId, List<LogInput> Logs);

    private sealed record RoutineInput(int Id, List<int> HabitIds);

    private sealed record CaseInput(string Name, DateOnly Today, DateOnly From, string Timezone, int GraceDaysPerWeek, List<DateOnly> LowDays,
        List<HabitInput> Habits, List<RoutineInput> Routines);

    private sealed record DayOut(DateOnly Date, string State, bool? Completed, int Count);

    private sealed record WeekOut(int Done, int Target);

    private sealed record StatsOut(int? Streak, int? GraceUsed, int? BestStreak, int Last7, WeekOut? Week);

    private sealed record DayStatsOut(DateOnly Date, int Scheduled, int Done, int Partial, bool ShowedUp);

    private sealed record WeeklyHabitOut(int HabitId, string Name, int Done, int Possible, int? Streak);

    private sealed record WeeklyOut(int TotalDone, int TotalPossible, string Headline, string Footer, List<WeeklyHabitOut> Habits);

    private sealed record ExpectedOut(Dictionary<string, List<DayOut>> Days, Dictionary<string, StatsOut> Stats, List<DayStatsOut> DayStats,
        Dictionary<string, List<int>> RoutinePending, WeeklyOut Weekly);

    private sealed record PauseOut(DateOnly? PausedFrom, DateOnly? PausedUntil);

    private sealed record PauseCase(string Name, DateOnly? PausedFrom, DateOnly? PausedUntil, DateOnly? Until);

    private sealed record LogOut(DateOnly Date, int Count, bool Completed);

    private sealed record DayEdit(bool? Completed, int? Count);

    private sealed record WriteDayCase(string Name, int TimesPerDay, DateOnly Date, LogInput? Log, DayEdit Edit);

    private sealed record RetargetCase(string Name, int TimesPerDay, int NewTimesPerDay, List<LogInput> Logs);

    private static readonly DateOnly EditStart = new(2027, 1, 1);

    [Fact]
    public void Core_matches_the_shared_rules_fixtures()
    {
        var root = LoadFixture();
        var cases = root["cases"]!.AsArray();
        cases.Should().NotBeEmpty();

        var failures = new List<string>();
        foreach (var node in cases)
        {
            var input = node.Deserialize<CaseInput>(Json)!;
            var computed = JsonSerializer.SerializeToNode(Compute(input), Json)!;
            if (Write)
                node!["expected"] = computed;
            else
                failures.AddRange(Differences(input.Name, node!["expected"], computed));
        }

        SaveOrAssert(root, failures);
    }

    [Fact]
    public async Task Core_edits_match_the_shared_rules_fixtures()
    {
        var root = LoadFixture();
        var edits = root["edits"]!.AsObject();
        var today = edits["today"].Deserialize<DateOnly>(Json);
        var time = new FakeTimeProvider(today.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: time);
        var failures = new List<string>();
        var nextUser = 9_100UL;

        async Task Run<TCase>(string section, Func<ulong, TCase, Task<object?>> compute) where TCase : class
        {
            var cases = edits[section]!.AsArray();
            cases.Should().NotBeEmpty();
            foreach (var node in cases)
            {
                var input = node.Deserialize<TCase>(Json)!;
                var computed = JsonSerializer.SerializeToNode(await compute(nextUser++, input), Json);
                var name = node!["name"]!.GetValue<string>();
                if (Write)
                    node["expected"] = computed;
                else if (FirstDifference(node["expected"], computed) is { } diff)
                    failures.Add($"edits.{section} \"{name}\": {diff}");
            }
        }

        await Run<PauseCase>("pause", async (user, c) =>
        {
            var habitId = await EditHabitAsync(host, user, timesPerDay: 1, h => (h.PausedFrom, h.PausedUntil) = (c.PausedFrom, c.PausedUntil));
            var habit = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().PauseHabitAsync(user, habitId, c.Until));
            return new PauseOut(habit!.PausedFrom, habit.PausedUntil);
        });

        await Run<WriteDayCase>("writeDay", async (user, c) =>
        {
            var habitId = await EditHabitAsync(host, user, c.TimesPerDay, logs: c.Log is { } log ? [log] : []);
            var result = await host.WithScopeAsync(async sp =>
            {
                var service = sp.GetRequiredService<HabitService>();
                return c.Edit switch
                {
                    { Count: { } count } => await service.SetCountAsync(await service.FindDiscordUserAsync(user), habitId, c.Date, count),
                    { Completed: { } completed } => await service.CheckInAsync(user, habitId, c.Date, completed),
                    _ => await service.ClearDayAsync(user, habitId, c.Date),
                };
            });
            result.Should().Be(DayEditResult.Saved, $"writeDay \"{c.Name}\" edits an editable day");
            return (await LogsAsync(host, habitId)).SingleOrDefault();
        });

        await Run<RetargetCase>("retarget", async (user, c) =>
        {
            var habitId = await EditHabitAsync(host, user, c.TimesPerDay, logs: c.Logs);
            var habit = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>()
                .SetScheduleAsync(user, habitId, FrequencyType.Daily, null, null, c.NewTimesPerDay));
            habit.Should().NotBeNull();
            return await LogsAsync(host, habitId);
        });

        SaveOrAssert(root, failures);
    }

    [Fact]
    public void Core_limits_match_the_shared_rules_fixtures()
    {
        var root = LoadFixture();
        using var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseSqlite("Data Source=:memory:").Options);
        int MaxLength<T>(string property) => db.Model.FindEntityType(typeof(T))!.FindProperty(property)!.GetMaxLength()!.Value;

        var computed = JsonSerializer.SerializeToNode(new
        {
            Local = new
            {
                Habits = Limits.HabitsPerUser,
                Routines = Limits.RoutinesPerUser,
                ActiveMoods = JournalService.MaxActiveMoodsPerUser,
                HabitName = Limits.HabitNameLength,
                RoutineName = Limits.RoutineNameLength,
                MoodLabel = MaxLength<Mood>(nameof(Mood.Label)),
                NoteText = MaxLength<DayNote>(nameof(DayNote.Text)),
                HabitNoteText = MaxLength<HabitNote>(nameof(HabitNote.Text)),
                TodayOrder = User.MaxTodayOrderEntries,
                TimesPerDay = Limits.TimesPerDay,
                WeeklyTarget = Limits.WeeklyTarget,
            },
            Fireside = new
            {
                Name = FiresideService.MaxNameLength,
                CodeLength = FiresideService.CodeLength,
                CodeMinutes = (int)FiresideService.CodeLifetime.TotalMinutes,
                Friends = FiresideService.MaxFriends,
            },
        }, Json)!;

        var failures = new List<string>();
        if (Write)
            root["limits"] = computed;
        else if (FirstDifference(root["limits"], computed) is { } diff)
            failures.Add($"limits: {diff}");
        SaveOrAssert(root, failures);
    }

    private static bool Write => Environment.GetEnvironmentVariable("EMBER_WRITE_FIXTURES") == "1";

    private static string FixturePath => Path.Combine(RepoRoot(), "docs", "contracts", "fixtures", "rules-v1.json");

    private static JsonNode LoadFixture()
    {
        var root = JsonNode.Parse(File.ReadAllText(FixturePath))!;
        root["version"]!.GetValue<int>().Should().Be(1);
        return root;
    }

    private static void SaveOrAssert(JsonNode root, List<string> failures)
    {
        if (Write)
            File.WriteAllText(FixturePath, root.ToJsonString(Json) + "\n");
        failures.Should().BeEmpty("rules-v1.json must match Core; after a deliberate rule change regenerate it with EMBER_WRITE_FIXTURES=1");
    }

    private static async Task<int> EditHabitAsync(IntegrationTestHost host, ulong user, int timesPerDay, Action<Habit>? configure = null,
        IReadOnlyList<LogInput>? logs = null)
    {
        return await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            await service.GiveHealthDataConsentAsync(user);
            var habitId = (await service.AddHabitAsync(user, "Edit", null)).Habit!.Id;
            var db = sp.GetRequiredService<EmberDbContext>();
            var habit = await db.Habits.SingleAsync(h => h.Id == habitId);
            habit.CreatedAt = EditStart.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
            habit.TimesPerDay = timesPerDay;
            configure?.Invoke(habit);
            foreach (var log in logs ?? [])
                db.HabitLogs.Add(new HabitLog { HabitId = habitId, Date = log.Date, Count = log.Count, Completed = log.Completed });
            await db.SaveChangesAsync();
            return habitId;
        });
    }

    private static Task<List<LogOut>> LogsAsync(IntegrationTestHost host, int habitId)
        => host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().HabitLogs.AsNoTracking()
            .Where(l => l.HabitId == habitId).OrderBy(l => l.Date).Select(l => new LogOut(l.Date, l.Count, l.Completed)).ToListAsync());

    private static ExpectedOut Compute(CaseInput input)
    {
        var tz = TimeZones.Find(input.Timezone) ?? throw new InvalidOperationException($"Unknown timezone {input.Timezone}.");
        var lowDays = input.LowDays.ToHashSet();
        var habits = input.Habits.Select(ToHabit).ToList();
        foreach (var routine in input.Routines)
            for (var i = 0; i < routine.HabitIds.Count; i++)
            {
                var member = habits.Single(h => h.Id == routine.HabitIds[i]);
                member.RoutineId.Should().Be(routine.Id, $"case \"{input.Name}\": habit {member.Id} is listed in routine {routine.Id}");
                member.RoutinePosition = i;
            }

        var dates = Enumerable.Range(0, input.Today.DayNumber - input.From.DayNumber + 1).Select(input.From.AddDays).ToList();
        return new ExpectedOut(
            habits.ToDictionary(h => Key(h.Id), h => dates.Select(d => HabitRules.DayOn(h, d, input.Today, tz, lowDays))
                .Select(d => new DayOut(d.Date, d.State.ToString().ToLowerInvariant(), d.Completed, d.Count)).ToList()),
            habits.ToDictionary(h => Key(h.Id), h => ToStats(HabitRules.StatsOn(h, input.Today, input.GraceDaysPerWeek, lowDays, tz))),
            dates.Select(d => HabitRules.DayStatsOn(habits, d, tz))
                .Select(s => new DayStatsOut(s.Date, s.Scheduled, s.Done, s.Partial, s.ShowedUp)).ToList(),
            input.Routines.ToDictionary(r => Key(r.Id), r => HabitRules.PendingRoutineHabits(
                habits.Where(h => h.RoutineId == r.Id), input.Today, tz, lowDays).Select(h => h.Id).ToList()),
            Weekly(input, habits));
    }

    /// <summary>What <c>GET /api/stats/weekly</c> returns on <c>today</c>, from <see cref="WeeklySummary"/> as the API maps it.</summary>
    private static WeeklyOut Weekly(CaseInput input, List<Habit> habits)
    {
        var user = new User { Timezone = input.Timezone, GraceDaysPerWeek = input.GraceDaysPerWeek, Language = Languages.English };
        foreach (var habit in habits) user.Habits.Add(habit);
        foreach (var day in input.LowDays) user.DayMarks.Add(new DayMark { Date = day, Kind = DayMarkKind.LowDay });

        var summary = WeeklySummary.Build(user, input.Today.AddDays(-1));
        var (headline, footer) = summary.HasSummarableWeek
            ? WeeklySummary.ToneLines(summary.TotalDone, summary.TotalPossible)
            : ("", "");
        return new WeeklyOut(summary.TotalDone, summary.TotalPossible, PlainText(headline), PlainText(footer),
            summary.Habits.Select(h => new WeeklyHabitOut(h.Habit.Id, h.Habit.Name, h.Done, h.Possible,
                h.Habit.FrequencyType != FrequencyType.Weekly ? h.CurrentStreak : null)).ToList());
    }

    private static string PlainText(string discordMarkdown) => DiscordMarkdown().Replace(discordMarkdown, "");

    [GeneratedRegex(@"\*\*|__|~~|\|\||`")]
    private static partial Regex DiscordMarkdown();

    private static Habit ToHabit(HabitInput input)
    {
        var frequency = Enum.Parse<FrequencyType>(input.Frequency, ignoreCase: true);
        ScheduleDays? days = null;
        if (input.Days is not null)
        {
            ScheduleDayNames.TryParse(input.Days, out var parsed).Should().BeTrue($"habit {input.Id} has valid days");
            days = parsed;
        }

        var habit = new Habit
        {
            Id = input.Id,
            Name = $"Habit {input.Id}",
            FrequencyType = frequency,
            WeeklyTarget = input.WeeklyTarget,
            Days = days,
            TimesPerDay = input.TimesPerDay,
            CreatedAt = input.StartDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            PausedFrom = input.PausedFrom,
            PausedUntil = input.PausedUntil,
            RoutineId = input.RoutineId,
        };
        foreach (var log in input.Logs)
            habit.Logs.Add(new HabitLog { Date = log.Date, Count = log.Count, Completed = log.Completed });
        return habit;
    }

    private static StatsOut ToStats(HabitStats s)
        => new(s.Streak, s.GraceUsed, s.BestStreak, s.Last7, s.Week is { } w ? new WeekOut(w.Done, w.Target) : null);

    private static string Key(int id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static IEnumerable<string> Differences(string name, JsonNode? expected, JsonNode computed)
    {
        if (expected is not JsonObject expectedObject)
        {
            yield return $"case \"{name}\": expected is missing";
            yield break;
        }

        foreach (var (section, value) in computed.AsObject())
        {
            var want = expectedObject[section];
            if (value is JsonObject byId)
            {
                if (want is not JsonObject wantById || !wantById.Select(p => p.Key).Order().SequenceEqual(byId.Select(p => p.Key).Order()))
                {
                    yield return $"case \"{name}\": {section} expected {want?.ToJsonString()} but Core computed {value.ToJsonString()}";
                    continue;
                }
                foreach (var (id, got) in byId)
                    if (FirstDifference(wantById[id], got) is { } diff)
                        yield return $"case \"{name}\": {section}[{id}] {diff}";
            }
            else if (FirstDifference(want, value) is { } diff)
            {
                yield return $"case \"{name}\": {section} {diff}";
            }
        }
    }

    private static string? FirstDifference(JsonNode? want, JsonNode? got)
    {
        if (JsonNode.DeepEquals(want, got)) return null;
        if (want is JsonArray wantArray && got is JsonArray gotArray && wantArray.Count == gotArray.Count)
            for (var i = 0; i < wantArray.Count; i++)
                if (!JsonNode.DeepEquals(wantArray[i], gotArray[i]))
                    return $"expected {wantArray[i]?.ToJsonString()} but Core computed {gotArray[i]?.ToJsonString()}";
        return $"expected {want?.ToJsonString()} but Core computed {got?.ToJsonString()}";
    }

    /// <summary>The repository root: the nearest folder above the test output that holds <c>Ember.slnx</c>.</summary>
    internal static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
