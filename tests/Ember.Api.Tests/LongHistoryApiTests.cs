using System.Net;
using System.Text.Json;
using Ember.Core.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>
/// Streaks and days free are computed from a habit's whole history, so the habit reads must keep loading logs and slips older than
/// the 366 days a per-day read may span.
/// </summary>
public class LongHistoryApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private async Task StartedDaysAgoAsync(int habitId, int days)
    {
        var createdAt = Host.Today.AddDays(-days).ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc);
        await Host.Db(db => db.Habits.Where(h => h.Id == habitId).ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, createdAt)));
    }

    private async Task LogDoneAsync(int habitId, int fromDaysAgo, int toDaysAgo)
    {
        await Host.Db(async db =>
        {
            for (var ago = fromDaysAgo; ago >= toDaysAgo; ago--)
                db.HabitLogs.Add(new HabitLog { HabitId = habitId, Date = Host.Today.AddDays(-ago), Completed = true, Count = 1 });
            return await db.SaveChangesAsync();
        });
    }

    private static JsonElement Stats(JsonElement habit) => habit.GetProperty("stats");

    [DockerFact]
    public async Task A_best_streak_that_ended_over_a_year_ago_and_a_current_streak_longer_than_a_year_both_count_in_full()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { graceDaysPerWeek = 0 })).Expect(HttpStatusCode.OK);
        var id = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        await StartedDaysAgoAsync(id, 900);
        await LogDoneAsync(id, 899, 480);
        await LogDoneAsync(id, 400, 0);

        var listed = (await (await user.Get("/api/habits")).ExpectJson()).EnumerateArray().Single();
        var single = await (await user.Get($"/api/habits/{id}")).ExpectJson();

        foreach (var habit in new[] { listed, single })
        {
            Stats(habit).GetProperty("streak").GetInt32().Should().Be(401);
            Stats(habit).GetProperty("bestStreak").GetInt32().Should().Be(420);
            Stats(habit).GetProperty("last7").GetInt32().Should().Be(7);
        }
    }

    [DockerFact]
    public async Task Days_free_and_the_longest_stretch_count_slips_from_over_a_year_ago()
    {
        var user = await Host.SignedInAsync();
        var id = (await (await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" }))
            .ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        await StartedDaysAgoAsync(id, 900);
        await Host.Db(async db =>
        {
            foreach (var ago in new[] { 880, 400 })
                db.HabitSlips.Add(new HabitSlip { HabitId = id, Date = Host.Today.AddDays(-ago), Count = 1, UpdatedAt = Host.Time.GetUtcNow().UtcDateTime });
            return await db.SaveChangesAsync();
        });

        var listed = (await (await user.Get("/api/habits")).ExpectJson()).EnumerateArray().Single();
        var single = await (await user.Get($"/api/habits/{id}")).ExpectJson();

        foreach (var habit in new[] { listed, single })
        {
            Stats(habit).GetProperty("daysFree").GetInt32().Should().Be(400);
            Stats(habit).GetProperty("longestStretch").GetInt32().Should().Be(479);
            Stats(habit).GetProperty("slipDaysLast30").GetInt32().Should().Be(0);
        }
    }
}
