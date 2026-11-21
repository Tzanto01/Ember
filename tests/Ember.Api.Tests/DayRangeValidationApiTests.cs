using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>The from/to range of the per-day reads, the journal reads and low days: exact messages, order and edges.</summary>
public class DayRangeValidationApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string BadDate = "Must be a date as yyyy-MM-dd.";

    private sealed record Range(string Name, string Url, string? TooLong, bool FutureTo);

    private sealed record Setup(ApiClient User, DateOnly Today, int BuildId, int LetGoId)
    {
        public Range[] Ranges =>
        [
            new("habit days", $"/api/habits/{BuildId}/days", "At most 366 days per request.", false),
            new("made-it", $"/api/habits/{LetGoId}/made-it", "At most 366 days per request.", false),
            new("stats days", "/api/stats/days", "At most 366 days per request.", false),
            new("day notes", "/api/day-notes", "At most 366 days.", true),
            new("habit notes", "/api/habit-notes", "At most 366 days.", true),
            new("low days", "/api/low-days", null, true),
        ];
    }

    private async Task<Setup> SetupAsync()
    {
        var user = await Host.SignedInAsync();
        var userId = await user.IdAsync();
        var build = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        var letGo = (await (await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        await Host.Db(db => db.Habits.Where(h => h.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(-400))));
        var today = DateOnly.Parse((await (await user.Get("/api/me")).ExpectJson()).GetProperty("localToday").GetString()!);
        return new Setup(user, today, build, letGo);
    }

    private static string Query(Range range, DateOnly? from, DateOnly? to)
    {
        var parts = new List<string>();
        if (from is { } f) parts.Add($"from={f.Iso()}");
        if (to is { } t) parts.Add($"to={t.Iso()}");
        return $"{range.Url}?{string.Join('&', parts)}";
    }

    private static async Task<JsonElement> OkAsync(ApiClient user, string url) => await (await user.Get(url)).ExpectJson();

    [DockerFact]
    public async Task A_malformed_from_or_to_is_a_400_with_the_date_message_and_to_listed_first_on_every_range_endpoint()
    {
        var s = await SetupAsync();

        foreach (var range in s.Ranges)
        {
            await (await s.User.Get($"{range.Url}?from=2026-13-01&to=tomorrow")).ExpectErrors($$"""{"to":["{{BadDate}}"],"from":["{{BadDate}}"]}""");
            await (await s.User.Get($"{range.Url}?from=1.11.2026")).ExpectErrors($$"""{"from":["{{BadDate}}"]}""");
            await (await s.User.Get($"{range.Url}?to=2026-02-30")).ExpectErrors($$"""{"to":["{{BadDate}}"]}""");
        }
    }

    [DockerFact]
    public async Task A_to_after_today_is_a_400_on_the_per_day_reads_and_allowed_on_the_journal_and_low_days()
    {
        var s = await SetupAsync();

        foreach (var range in s.Ranges)
        {
            var future = await s.User.Get(Query(range, null, s.Today.AddDays(1)));
            if (range.FutureTo)
                await future.Expect(HttpStatusCode.OK);
            else
            {
                await future.ExpectErrors("""{"to":["Can't be after today."]}""");
                await (await s.User.Get(Query(range, s.Today.AddDays(2), s.Today.AddDays(1)))).ExpectErrors("""{"to":["Can't be after today."]}""");
            }
        }
    }

    [DockerFact]
    public async Task A_from_after_to_is_a_400_on_every_range_endpoint()
    {
        var s = await SetupAsync();

        foreach (var range in s.Ranges)
            await (await s.User.Get(Query(range, s.Today, s.Today.AddDays(-1)))).ExpectErrors("""{"from":["Can't be after to."]}""");
    }

    [DockerFact]
    public async Task A_range_of_exactly_366_days_is_a_200_and_367_days_is_a_400_with_the_endpoints_own_message_whichever_day_it_ends_on()
    {
        var s = await SetupAsync();

        foreach (var end in new[] { s.Today, s.Today.AddDays(-10) })
            foreach (var range in s.Ranges)
            {
                await (await s.User.Get(Query(range, end.AddDays(-365), end))).Expect(HttpStatusCode.OK);
                var tooLong = await s.User.Get(Query(range, end.AddDays(-366), end));
                if (range.TooLong is { } message)
                    await tooLong.ExpectErrors($$"""{"from":["{{message}}"]}""");
                else
                    await tooLong.Expect(HttpStatusCode.OK);
            }

        await (await s.User.Get(Query(s.Ranges[5], s.Today.AddDays(-5000), s.Today.AddDays(30)))).Expect(HttpStatusCode.OK);
        (await OkAsync(s.User, Query(s.Ranges[0], s.Today.AddDays(-365), s.Today))).GetArrayLength().Should().Be(366);
        (await OkAsync(s.User, Query(s.Ranges[2], s.Today.AddDays(-365), s.Today))).GetArrayLength().Should().Be(366);
    }

    [DockerFact]
    public async Task An_omitted_from_defaults_to_14_days_ending_at_to_on_habit_days_and_to_366_days_on_stats_days()
    {
        var s = await SetupAsync();
        var to = s.Today.AddDays(-3);

        var days = await OkAsync(s.User, s.Ranges[0].Url);
        days.GetArrayLength().Should().Be(14);
        days[0].GetProperty("date").GetString().Should().Be(s.Today.AddDays(-13).Iso());
        days[13].GetProperty("date").GetString().Should().Be(s.Today.Iso());

        var daysBefore = await OkAsync(s.User, Query(s.Ranges[0], null, to));
        daysBefore.GetArrayLength().Should().Be(14);
        daysBefore[0].GetProperty("date").GetString().Should().Be(to.AddDays(-13).Iso());
        daysBefore[13].GetProperty("date").GetString().Should().Be(to.Iso());

        var stats = await OkAsync(s.User, s.Ranges[2].Url);
        stats.GetArrayLength().Should().Be(366);
        stats[0].GetProperty("date").GetString().Should().Be(s.Today.AddDays(-365).Iso());
        stats[365].GetProperty("date").GetString().Should().Be(s.Today.Iso());

        var statsBefore = await OkAsync(s.User, Query(s.Ranges[2], null, to));
        statsBefore.GetArrayLength().Should().Be(366);
        statsBefore[365].GetProperty("date").GetString().Should().Be(to.Iso());
    }

    [DockerFact]
    public async Task An_omitted_from_reaches_back_14_days_for_made_it_and_366_days_for_notes_and_low_days()
    {
        var s = await SetupAsync();
        string D(int back) => s.Today.AddDays(-back).Iso();
        foreach (var back in new[] { 13, 14 })
            await (await s.User.Put($"/api/habits/{s.LetGoId}/made-it/{D(back)}")).Expect(HttpStatusCode.OK);
        foreach (var back in new[] { 365, 366 })
        {
            await (await s.User.Put($"/api/day-notes/{D(back)}", new { text = "note" })).Expect(HttpStatusCode.OK);
            await (await s.User.Put($"/api/habits/{s.BuildId}/notes/{D(back)}", new { text = "note" })).Expect(HttpStatusCode.OK);
            await (await s.User.Put($"/api/low-days/{D(back)}")).Expect(HttpStatusCode.NoContent);
        }

        static string[] Dates(JsonElement list, string property = "date")
            => list.EnumerateArray().Select(e => property.Length == 0 ? e.GetString()! : e.GetProperty(property).GetString()!).ToArray();

        Dates(await OkAsync(s.User, s.Ranges[1].Url)).Should().Equal(D(13));
        Dates(await OkAsync(s.User, s.Ranges[3].Url)).Should().Equal(D(365));
        Dates(await OkAsync(s.User, s.Ranges[4].Url)).Should().Equal(D(365));
        Dates(await OkAsync(s.User, s.Ranges[5].Url), "").Should().Equal(D(365));

        Dates(await OkAsync(s.User, Query(s.Ranges[3], null, s.Today.AddDays(-366)))).Should().Equal(D(366));
        Dates(await OkAsync(s.User, Query(s.Ranges[5], null, s.Today.AddDays(-1))), "").Should().Equal(D(366), D(365));
    }
}
