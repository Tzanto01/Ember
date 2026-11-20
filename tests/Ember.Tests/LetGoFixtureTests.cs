using System.Text.Json;
using System.Text.Json.Nodes;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

/// <summary>
/// Checks Core's let-go rules against docs/contracts/fixtures/letgo-v1.json, the hand-computed cases the no-account client also
/// has to match. The fixture is the truth: it is never rewritten from Core.
/// </summary>
public class LetGoFixtureTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record SlipInput(DateOnly Date, int Count, List<string> Reasons);

    private sealed record HabitInput(DateOnly StartDate, DateOnly? PausedFrom, DateOnly? PausedUntil, List<SlipInput> Slips);

    private sealed record CaseInput(string Name, DateOnly Today, DateOnly From, string Timezone, HabitInput Habit);

    private sealed record DayOut(DateOnly Date, string State, int Slips);

    private sealed record StatsOut(int DaysFree, int LongestStretch, int FreeDaysThisMonth, int SlipDaysLast30);

    private sealed record ExpectedOut(StatsOut Stats, DayOut Today, List<DayOut> Days);

    [Fact]
    public void Core_matches_the_let_go_fixtures()
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "contracts", "fixtures", "letgo-v1.json")))!;
        root["version"]!.GetValue<int>().Should().Be(1);
        var cases = root["cases"]!.AsArray();
        cases.Should().NotBeEmpty();

        var failures = new List<string>();
        foreach (var node in cases)
        {
            var input = node.Deserialize<CaseInput>(Json)!;
            var computed = JsonSerializer.SerializeToNode(Compute(input), Json)!;
            foreach (var (section, value) in computed.AsObject())
                if (!JsonNode.DeepEquals(node!["expected"]?[section], value))
                    failures.Add($"case \"{input.Name}\": {section} expected {node["expected"]?[section]?.ToJsonString()} but Core computed {value?.ToJsonString()}");
        }

        failures.Should().BeEmpty("letgo-v1.json is the hand-computed truth for the let-go rules");
    }

    private static ExpectedOut Compute(CaseInput input)
    {
        var tz = TimeZones.Find(input.Timezone) ?? throw new InvalidOperationException($"Unknown timezone {input.Timezone}.");
        var habit = new Habit
        {
            Id = 1,
            Name = "Let go",
            Kind = HabitKind.LetGo,
            CreatedAt = input.Habit.StartDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc),
            PausedFrom = input.Habit.PausedFrom,
            PausedUntil = input.Habit.PausedUntil,
        };
        foreach (var slip in input.Habit.Slips)
            habit.Slips.Add(new HabitSlip { Date = slip.Date, Count = slip.Count, Reasons = slip.Reasons });

        var s = LetGoRules.StatsOn(habit, input.Today, tz);
        return new ExpectedOut(
            new StatsOut(s.DaysFree, s.LongestStretch, s.FreeDaysThisMonth, s.SlipDaysLast30),
            Day(LetGoRules.DayOn(habit, input.Today, tz)),
            Enumerable.Range(0, input.Today.DayNumber - input.From.DayNumber + 1)
                .Select(i => Day(LetGoRules.DayOn(habit, input.From.AddDays(i), tz))).ToList());
    }

    private static DayOut Day(LetGoDay day) => new(day.Date, day.State.ToString().ToLowerInvariant(), day.Slips);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ember.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Ember.slnx not found above the test output directory.");
    }
}
