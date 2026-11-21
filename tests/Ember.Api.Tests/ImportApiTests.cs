using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary><c>POST /api/me/import</c> (contract "Export format v2").</summary>
public class ImportApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    private static readonly object Blob = new { v = 1, iv = B64(12), ct = B64(32) };

    private static readonly object Key = new
    {
        version = 4,
        turning_off = true,
        passphrase = new { salt = B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
        recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        password = ApiClient.Passphrase,
    };

    private static readonly string[] IgnoredSettings =
    [
        "dm_failure_count", "dm_reminders_paused_at", "dm_pause_notice_sent_at",
        "health_data_consent_at", "consent_policy_version", "consent_prompted_on", "two_factor_enabled",
    ];

    private static object HabitItem(int id, string name = "Water")
        => new { id, name, frequency = "daily", times_per_day = 1, created_at = "2026-10-20T08:00:00Z", reminder_time = "09:00", reminder_channels = new[] { "webpush" } };

    private static object Doc(object[]? habits = null, object[]? logs = null, object[]? moods = null, object[]? dayNotes = null,
        object? journalKey = null, int formatVersion = 2, string? padding = null, object? settings = null) => new
        {
            format_version = formatVersion,
            settings = settings ?? new { grace_days_per_week = 1 },
            habits = habits ?? [HabitItem(1)],
            logs = logs ?? [],
            day_marks = Array.Empty<object>(),
            routines = Array.Empty<object>(),
            moods = moods ?? [],
            day_notes = dayNotes ?? [],
            journal_key = journalKey,
            padding,
        };

    private static Task<HttpResponseMessage> Import(ApiClient user, object doc) => user.Post("/api/me/import", doc);

    private Task<int> HabitCountAsync(Guid userId) => Host.Db(db => db.Habits.CountAsync(h => h.UserId == userId));

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
        => (await response.ExpectProblem(HttpStatusCode.BadRequest, "validation")).GetProperty("errors");

    /// <summary>The export without what import ignores, with every id replaced by its index in its section.</summary>
    private static string Comparable(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        root.Remove("account");
        root.Remove("push_subscriptions");
        root.Remove("feedback");
        root.Remove("sessions");
        foreach (var field in IgnoredSettings) root["settings"]!.AsObject().Remove(field);

        var habits = Reindex(root["habits"]!);
        var routines = Reindex(root["routines"]!);
        var moods = Reindex(root["moods"]!);
        foreach (var habit in root["habits"]!.AsArray())
            if (habit!["routine_id"] is { } routineId) habit["routine_id"] = routines[(int)routineId];
        foreach (var log in root["logs"]!.AsArray())
            log!["habit_id"] = habits[(int)log["habit_id"]!];
        foreach (var note in root["day_notes"]!.AsArray())
            if ((string?)note!["mood"] is { } mood && mood.StartsWith("custom:"))
                note["mood"] = $"custom:{moods[int.Parse(mood[7..])]}";
        return root.ToJsonString();
    }

    private static Dictionary<int, int> Reindex(JsonNode section)
    {
        var map = new Dictionary<int, int>();
        foreach (var item in section.AsArray())
        {
            map[(int)item!["id"]!] = map.Count;
            item["id"] = map.Count - 1;
        }
        return map;
    }

    [DockerFact]
    public async Task An_export_imported_into_a_fresh_account_exports_the_same_data()
    {
        var source = await Host.SignedInAsync();
        var sourceId = await source.IdAsync();
        var today = Host.Today;
        await (await source.Patch("/api/me", new { graceDaysPerWeek = 2, theme = "tidepool", secondReminderMinutes = 30, showStreaks = true, language = "de", calmMode = true })).Expect(HttpStatusCode.OK);
        int Id(JsonElement e) => e.GetProperty("id").GetInt32();
        var water = Id(await (await source.Post("/api/habits", new { name = "Water", frequency = "daily", timesPerDay = 3, reminderTimes = new[] { "20:00", "09:00" } }))
            .ExpectJson(HttpStatusCode.Created));
        var gym = Id(await (await source.Post("/api/habits", new { name = "Gym", frequency = "weekdays", days = new[] { "mon", "wed", "fri" } }))
            .ExpectJson(HttpStatusCode.Created));
        var read = Id(await (await source.Post("/api/habits", new { name = "Read", frequency = "weekly", weeklyTarget = 2 }))
            .ExpectJson(HttpStatusCode.Created));
        await Host.Db(db => db.Habits.Where(h => h.UserId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(-10))));

        await (await source.Put($"/api/habits/{water}/days/{today.AddDays(-4).Iso()}", new { count = 3 })).Expect(HttpStatusCode.OK);
        await (await source.Patch($"/api/habits/{water}", new { timesPerDay = 4 })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{water}/days/{today.AddDays(-3).Iso()}", new { count = 2 })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{water}/days/{today.AddDays(-2).Iso()}", new { completed = false })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{gym}/days/{today.AddDays(-5).Iso()}", new { completed = true })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{read}/days/{today.AddDays(-1).Iso()}", new { completed = true })).Expect(HttpStatusCode.OK);
        await (await source.Post($"/api/habits/{gym}/pause", new { until = today.AddDays(3).Iso() })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/low-days/{today.AddDays(-6).Iso()}")).Expect(HttpStatusCode.NoContent);
        await (await source.Post("/api/routines", new
        {
            name = "Morning",
            reminderTime = "07:30",
            reminderChannels = new[] { "mobile" },
            habitIds = new[] { read, water }
        })).Expect(HttpStatusCode.Created);
        var proud = Id(await (await source.Post("/api/moods", new { label = "Proud", emoji = "🦁" })).ExpectJson(HttpStatusCode.Created));
        var tired = Id(await (await source.Post("/api/moods", new { label = "Tired" })).ExpectJson(HttpStatusCode.Created));
        await (await source.Put($"/api/day-notes/{today.AddDays(-3).Iso()}", new { text = "Long day", mood = $"custom:{tired}" })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/day-notes/{today.AddDays(-2).Iso()}", new { mood = $"custom:{proud}" })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/day-notes/{today.AddDays(-1).Iso()}", new { mood = "good" })).Expect(HttpStatusCode.OK);
        await (await source.Patch($"/api/moods/{tired}", new { archived = true })).Expect(HttpStatusCode.OK);
        var exported = await (await source.Get("/api/me/export?format=json")).Expect(HttpStatusCode.OK);

        var target = await Host.SignedInAsync();
        await (await Import(target, JsonElement.Parse(exported))).Expect(HttpStatusCode.NoContent);
        var reexported = await (await target.Get("/api/me/export?format=json")).Expect(HttpStatusCode.OK);

        var expected = Comparable(exported);
        JsonNode.Parse(expected)!["logs"]!.AsArray().Should().HaveCount(5);
        expected.Should().Contain("\"completed\":true,\"guild_id\":null,\"count\":3").And.Contain("\"routine_position\":1")
            .And.Contain("\"archived\":true").And.Contain("\"reminder_times\":[\"09:00:00\",\"20:00:00\"]").And.Contain("\"paused_until\"").And.Contain("\"next_reminder_utc\":\"20")
            .And.Contain("\"second_reminder_minutes\":30").And.Contain("\"show_streaks\":true").And.Contain("\"tips_seen\":[\"streaks\"]")
            .And.Contain("\"language\":\"de\"").And.Contain("\"calm_mode\":true").And.Contain("\"count\":0,\"skipped\":true");
        Comparable(reexported).Should().Be(expected);
    }

    [DockerFact]
    public async Task A_log_without_skipped_imports_as_not_skipped_a_partial_skip_imports_and_a_skip_of_a_done_day_is_refused()
    {
        var user = await Host.SignedInAsync();
        var water = new { id = 1, name = "Water", frequency = "daily", times_per_day = 2, created_at = "2026-10-20T08:00:00Z" };
        var refused = Doc(logs:
        [
            new { habit_id = 1, date = "2026-10-24", completed = false, count = 0, skipped = true },
            new { habit_id = 1, date = "2026-10-25", completed = false, count = 1, skipped = true },
            new { habit_id = 1, date = "2026-10-26", completed = true, count = 1, skipped = true },
            new { habit_id = 1, date = "2026-10-27", completed = false, count = 2, skipped = true },
        ], habits: [water]);

        var errors = await ErrorsAsync(await Import(user, refused));

        errors.Has("logs[0].skipped").Should().BeFalse();
        errors.Has("logs[1].skipped").Should().BeFalse();
        errors.Has("logs[2].skipped").Should().BeTrue();
        errors.Has("logs[3].skipped").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);

        await (await Import(user, Doc(logs:
        [
            new { habit_id = 1, date = "2026-10-24", completed = false, count = 0 },
            new { habit_id = 1, date = "2026-10-25", completed = false, count = 0, skipped = true },
            new { habit_id = 1, date = "2026-10-26", completed = false, count = 1, skipped = true },
        ], habits: [water]))).Expect(HttpStatusCode.NoContent);
        var userId = await user.IdAsync();
        (await Host.Db(db => db.HabitLogs.Where(l => l.Habit.UserId == userId).OrderBy(l => l.Date)
                .Select(l => new { l.Count, l.Completed, l.Skipped }).ToListAsync()))
            .Select(l => (l.Count, l.Completed, l.Skipped)).Should().Equal((0, false, false), (0, false, true), (1, false, true));
    }

    private static Task<HttpResponseMessage> SendFeedbackWithPicture(ApiClient user, string title)
        => user.Post("/api/feedback", new { title, text = "It closes right away", platform = "web", version = "1.2.3", images = new[] { SyntheticImages.Base64(SyntheticImages.Png()) } });

    [DockerFact]
    public async Task The_json_export_streams_the_callers_feedback_with_the_stored_picture_bytes_and_their_sessions_without_ids()
    {
        var user = await Host.SignedInAsync(consent: false);
        var userId = await user.IdAsync();
        var other = await Host.SignedInAsync(consent: false);
        await (await SendFeedbackWithPicture(user, "Crash on start")).Expect(HttpStatusCode.NoContent);
        await (await SendFeedbackWithPicture(other, "Someone else's report")).Expect(HttpStatusCode.NoContent);
        var stored = await Host.Db(db => db.Feedback.AsNoTracking().Include(f => f.Images).SingleAsync(f => f.UserId == userId));
        var sessionIds = await Host.Db(db => db.AuthSessions.Where(s => s.UserId == userId).Select(s => s.Id).ToListAsync());

        var response = await user.Get("/api/me/export?format=json");
        var body = await response.Expect(HttpStatusCode.OK);

        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition?.FileName.Should().MatchRegex(@"^ember-export-\d{4}-\d{2}-\d{2}\.json$");
        var root = JsonNode.Parse(body)!;
        var report = root["feedback"]!.AsArray().Should().ContainSingle().Subject!;
        ((string?)report["title"]).Should().Be("Crash on start");
        ((string?)report["status"]).Should().Be("open");
        var picture = report["images"]!.AsArray().Should().ContainSingle().Subject!;
        ((Guid)picture["id"]!).Should().Be(stored.Images.Single().Id);
        Convert.FromBase64String((string)picture["data"]!).Should().Equal(stored.Images.Single().Data);
        root["sessions"]!.AsArray().Should().HaveCount(sessionIds.Count).And.OnlyContain(s => !s!.AsObject().ContainsKey("id"));
        sessionIds.Should().NotBeEmpty().And.OnlyContain(id => !body.Contains(id.ToString()) && !body.Contains(id.ToString("N")));
        body.Should().NotContain("Someone else's report");
    }

    [DockerFact]
    public async Task An_export_with_feedback_a_picture_and_a_session_imports_into_a_fresh_account_without_creating_feedback_or_sessions()
    {
        var source = await Host.SignedInAsync();
        await source.CreateHabitAsync("Water");
        await (await SendFeedbackWithPicture(source, "Crash on start")).Expect(HttpStatusCode.NoContent);
        var exported = await (await source.Get("/api/me/export?format=json")).ExpectJson();
        exported.GetProperty("feedback").GetArrayLength().Should().Be(1);
        exported.GetProperty("sessions").GetArrayLength().Should().BeGreaterThan(0);

        var target = await Host.SignedInAsync();
        var targetId = await target.IdAsync();
        var sessionsBefore = await Host.Db(db => db.AuthSessions.CountAsync(s => s.UserId == targetId));
        await (await Import(target, exported)).Expect(HttpStatusCode.NoContent);

        (await HabitCountAsync(targetId)).Should().Be(1);
        (await Host.Db(db => db.Feedback.CountAsync(f => f.UserId == targetId))).Should().Be(0);
        (await Host.Db(db => db.AuthSessions.CountAsync(s => s.UserId == targetId))).Should().Be(sessionsBefore);
    }

    private static object LetGoItem(int id, string name = "Coffee")
        => new { id, name, kind = "let_go", frequency = "daily", times_per_day = 1, created_at = "2026-10-20T08:00:00Z" };

    private static JsonElement WithSlips(object doc, params object[] slips)
    {
        var node = JsonSerializer.SerializeToNode(doc)!;
        node["slips"] = JsonSerializer.SerializeToNode(slips);
        return JsonSerializer.SerializeToElement(node);
    }

    [DockerFact]
    public async Task A_let_go_habit_and_its_slips_survive_an_export_and_import_into_a_fresh_account()
    {
        var source = await Host.SignedInAsync();
        var id = (await (await source.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        await Host.Db(db => db.Habits.ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(-10))));
        var today = Host.Today;
        await (await source.Post($"/api/habits/{id}/slips", new { date = today.AddDays(-3).Iso(), reason = "tired" })).ExpectJson();
        await (await source.Post($"/api/habits/{id}/slips", new { date = today.AddDays(-3).Iso(), reason = "bored" })).ExpectJson();
        await (await source.Post($"/api/habits/{id}/slips", new { date = today.Iso() })).ExpectJson();
        var exported = await (await source.Get("/api/me/export?format=json")).ExpectJson();

        exported.GetProperty("habits").EnumerateArray().Single().GetProperty("kind").GetString().Should().Be("let_go");
        exported.GetProperty("slips").EnumerateArray().Select(s => (s.GetProperty("habit_id").GetInt32(), s.GetProperty("count").GetInt32()))
            .Should().Equal((id, 2), (id, 1));

        var target = await Host.SignedInAsync();
        await (await Import(target, exported)).Expect(HttpStatusCode.NoContent);

        var habit = (await (await target.Get("/api/habits")).ExpectJson()).EnumerateArray().Single();
        habit.GetProperty("kind").GetString().Should().Be("letGo");
        habit.GetProperty("today").GetProperty("state").GetString().Should().Be("slipped");
        habit.GetProperty("stats").GetProperty("slipDaysLast30").GetInt32().Should().Be(2);
        var slips = await (await target.Get($"/api/habits/{habit.GetProperty("id").GetInt32()}/slips?from={today.AddDays(-5).Iso()}")).ExpectJson();
        slips.EnumerateArray().Select(s => (s.GetProperty("date").GetString(), s.GetProperty("count").GetInt32(), string.Join(",", s.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()))))
            .Should().Equal((today.AddDays(-3).Iso(), 2, "tired,bored"), (today.Iso(), 1, ""));
    }

    private static JsonElement WithMadeItDays(object doc, params object[] days)
    {
        var node = JsonSerializer.SerializeToNode(doc)!;
        node["made_it_days"] = JsonSerializer.SerializeToNode(days);
        return JsonSerializer.SerializeToElement(node);
    }

    [DockerFact]
    public async Task Made_it_marks_survive_an_export_and_import_and_the_slip_day_stays_unmarked()
    {
        var source = await Host.SignedInAsync();
        var id = (await (await source.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created))
            .GetProperty("id").GetInt32();
        await Host.Db(db => db.Habits.ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(-10))));
        var today = Host.Today;
        await (await source.Post($"/api/habits/{id}/slips", new { date = today.AddDays(-3).Iso() })).ExpectJson();
        await (await source.Put($"/api/habits/{id}/made-it/{today.AddDays(-2).Iso()}")).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{id}/made-it/{today.Iso()}")).Expect(HttpStatusCode.OK);
        var exported = await (await source.Get("/api/me/export?format=json")).ExpectJson();

        var target = await Host.SignedInAsync();
        await (await Import(target, exported)).Expect(HttpStatusCode.NoContent);

        var habit = (await (await target.Get("/api/habits")).ExpectJson()).EnumerateArray().Single();
        habit.GetProperty("today").GetProperty("madeIt").GetBoolean().Should().BeTrue();
        var days = await (await target.Get($"/api/habits/{habit.GetProperty("id").GetInt32()}/made-it?from={today.AddDays(-5).Iso()}")).ExpectJson();
        days.EnumerateArray().Select(d => d.GetProperty("date").GetString()).Should().Equal(today.AddDays(-2).Iso(), today.Iso());
    }

    [DockerFact]
    public async Task Import_refuses_made_it_days_that_are_not_on_a_free_day_of_a_let_go_habit_and_writes_nothing()
    {
        var user = await Host.SignedInAsync();

        var onBuild = await ErrorsAsync(await Import(user, WithMadeItDays(Doc(habits: [HabitItem(1)]), new { habit_id = 1, date = "2026-10-25" })));
        var bad = await ErrorsAsync(await Import(user, WithMadeItDays(Doc(habits: [LetGoItem(1)]),
            new { habit_id = 1, date = "2026-10-25" },
            new { habit_id = 1, date = "2026-10-25" },
            new { habit_id = 1, date = "2026-10-19" },
            new { habit_id = 1, date = "2026-12-25" },
            new { habit_id = 9, date = "2026-10-27" })));

        onBuild.Has("made_it_days[0].habit_id").Should().BeTrue();
        bad.EnumerateObject().Select(e => e.Name).Should().BeEquivalentTo(
            "made_it_days[1].date", "made_it_days[2].date", "made_it_days[3].date", "made_it_days[4].habit_id");
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        (await Host.Db(db => db.MadeItDays.CountAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task A_made_it_day_on_a_day_the_file_has_a_slip_for_is_skipped_and_the_rest_imports()
    {
        var user = await Host.SignedInAsync();
        var doc = WithMadeItDays(WithSlips(Doc(habits: [LetGoItem(1)]), new { habit_id = 1, date = "2026-10-26", count = 1, reasons = Array.Empty<string>() }),
            new { habit_id = 1, date = "2026-10-26" },
            new { habit_id = 1, date = "2026-10-25" });

        await (await Import(user, doc)).Expect(HttpStatusCode.NoContent);

        (await Host.Db(db => db.HabitSlips.Select(s => s.Date).ToListAsync())).Should().Equal(new DateOnly(2026, 10, 26));
        (await Host.Db(db => db.MadeItDays.Select(m => m.Date).ToListAsync())).Should().Equal(new DateOnly(2026, 10, 25));
    }

    [DockerFact]
    public async Task Habit_notes_the_offer_setting_and_the_today_order_follow_their_habits_new_ids()
    {
        var source = await Host.SignedInAsync();
        int Id(JsonElement e) => e.GetProperty("id").GetInt32();
        var walk = Id(await source.CreateHabitAsync("Walk"));
        var read = Id(await source.CreateHabitAsync("Read"));
        await Host.Db(db => db.Habits.Where(h => h.Id == walk || h.Id == read)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(-5))));
        var today = Host.Today;
        await (await source.Put($"/api/habits/{walk}/notes/{today.AddDays(-2).Iso()}", new { text = "Short loop" })).Expect(HttpStatusCode.OK);
        await (await source.Put($"/api/habits/{read}/notes/{today.Iso()}", new { text = "Two chapters" })).Expect(HttpStatusCode.OK);
        await (await source.Put("/api/me/today-order", new { order = new[] { $"h:{read}", $"h:{walk}" } })).Expect(HttpStatusCode.OK);
        await (await source.Patch("/api/me", new { offerHabitNote = true })).Expect(HttpStatusCode.OK);
        var exported = await (await source.Get("/api/me/export?format=json")).ExpectJson();
        exported.GetProperty("settings").GetProperty("today_order").GetArrayLength().Should().Be(2);

        var target = await Host.SignedInAsync();
        await (await Import(target, exported)).Expect(HttpStatusCode.NoContent);

        var me = await (await target.Get("/api/me")).ExpectJson();
        me.GetProperty("offerHabitNote").GetBoolean().Should().BeTrue();
        var names = (await (await target.Get("/api/habits")).ExpectJson()).EnumerateArray()
            .ToDictionary(h => Id(h), h => h.GetProperty("name").GetString());
        names.Keys.Should().NotContain([walk, read]);
        var newId = names.ToDictionary(n => n.Value!, n => n.Key);
        me.GetProperty("todayOrder").EnumerateArray().Select(k => k.GetString())
            .Should().Equal($"h:{newId["Read"]}", $"h:{newId["Walk"]}");
        var notes = (await (await target.Get("/api/habit-notes")).ExpectJson()).EnumerateArray()
            .Select(n => (names[n.GetProperty("habitId").GetInt32()], n.GetProperty("date").GetString(), n.GetProperty("text").GetString()));
        notes.Should().Equal(("Walk", today.AddDays(-2).Iso(), "Short loop"), ("Read", today.Iso(), "Two chapters"));
    }

    private static JsonElement WithRoutines(object doc, params object[] routines)
    {
        var node = JsonSerializer.SerializeToNode(doc)!;
        node["routines"] = JsonSerializer.SerializeToNode(routines);
        return JsonSerializer.SerializeToElement(node);
    }

    [DockerFact]
    public async Task The_today_order_keeps_only_its_valid_keys_in_order_with_the_new_ids()
    {
        var user = await Host.SignedInAsync();
        var member = new { id = 3, name = "Stretch", frequency = "daily", times_per_day = 1, created_at = "2026-10-20T08:00:00Z", routine_id = 7, routine_position = 0 };
        var order = new[] { "h:2", "r:7", "h:3", "h:99", "x:1", "h:", "h:02", "h:1", "h:2" };

        await (await Import(user, WithRoutines(Doc(habits: [HabitItem(1, "Water"), HabitItem(2, "Read"), member], settings: new { today_order = order }),
            new { id = 7, name = "Mornings" }))).Expect(HttpStatusCode.NoContent);

        var habits = (await (await user.Get("/api/habits")).ExpectJson()).EnumerateArray()
            .ToDictionary(h => h.GetProperty("name").GetString()!, h => h.GetProperty("id").GetInt32());
        var routine = (await (await user.Get("/api/routines")).ExpectJson()).EnumerateArray().Single().GetProperty("id").GetInt32();
        var me = await (await user.Get("/api/me")).ExpectJson();
        me.GetProperty("todayOrder").EnumerateArray().Select(k => k.GetString())
            .Should().Equal($"h:{habits["Read"]}", $"r:{routine}", $"h:{habits["Water"]}");
    }

    [DockerFact]
    public async Task More_than_500_today_order_entries_is_a_400_and_writes_nothing()
    {
        var user = await Host.SignedInAsync();
        var order = Enumerable.Range(0, 501).Select(_ => "h:1").ToArray();

        var errors = await ErrorsAsync(await Import(user, Doc(settings: new { today_order = order })));

        errors.Has("settings.today_order").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    private static JsonElement WithHabitNotes(object doc, params object[] notes)
    {
        var node = JsonSerializer.SerializeToNode(doc)!;
        node["habit_notes"] = JsonSerializer.SerializeToNode(notes);
        return JsonSerializer.SerializeToElement(node);
    }

    [DockerFact]
    public async Task Invalid_habit_notes_are_a_400_with_their_path_and_write_nothing()
    {
        var user = await Host.SignedInAsync();

        var errors = await ErrorsAsync(await Import(user, WithHabitNotes(Doc(),
            new { habit_id = 1, date = "2026-10-25", text = "Fine" },
            new { habit_id = 1, date = "2026-10-25", text = "Same day" },
            new { habit_id = 9, date = "2026-10-26", text = "No habit" },
            new { habit_id = 1, date = "2026-10-19", text = "Before start" },
            new { habit_id = 1, date = "2026-12-25", text = "Future" },
            new { habit_id = 1, date = "2026-10-27", text = new string('a', 281) },
            new { habit_id = 1, date = "2026-10-28", encrypted = Blob },
            new { habit_id = 1, date = "2026-10-29" })));

        errors.EnumerateObject().Select(e => e.Name).Should().BeEquivalentTo(
            "habit_notes[1].date", "habit_notes[2].habit_id", "habit_notes[3].date", "habit_notes[4].date",
            "habit_notes[5].text", "habit_notes[6].encrypted", "habit_notes[7].text");
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        (await Host.Db(db => db.HabitNotes.CountAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task An_encrypted_habit_note_imports_under_the_new_habit_id_with_its_iv_and_ct_and_ct_over_4096_characters_is_a_400()
    {
        var user = await Host.SignedInAsync();
        var iv = Convert.ToBase64String(Enumerable.Range(1, 12).Select(i => (byte)i).ToArray());
        var ct = Convert.ToBase64String(Enumerable.Range(0, 3072).Select(i => (byte)(i % 251)).ToArray());
        ct.Length.Should().Be(4096);
        object Note(string cipherText) => new { habit_id = 1, date = "2026-10-25", encrypted = new { v = 1, iv, ct = cipherText } };

        var tooLong = await ErrorsAsync(await Import(user, WithHabitNotes(Doc(journalKey: Key), Note(B64(3075)))));
        tooLong.EnumerateObject().Select(e => e.Name).Should().Contain(name => name.StartsWith("habit_notes[0].encrypted"));
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        (await Host.Db(db => db.HabitNotes.CountAsync())).Should().Be(0);

        await (await Import(user, WithHabitNotes(Doc(journalKey: Key), Note(ct)))).Expect(HttpStatusCode.NoContent);

        var habitId = (await (await user.Get("/api/habits")).ExpectJson()).EnumerateArray().Single().GetProperty("id").GetInt32();
        var stored = (await (await user.Get("/api/habit-notes")).ExpectJson()).EnumerateArray().Single();
        stored.GetProperty("habitId").GetInt32().Should().Be(habitId);
        stored.GetProperty("date").GetString().Should().Be("2026-10-25");
        stored.Has("text").Should().BeFalse();
        var encrypted = stored.GetProperty("encrypted");
        (encrypted.GetProperty("iv").GetString(), encrypted.GetProperty("ct").GetString()).Should().Be((iv, ct));
    }

    [DockerFact]
    public async Task Import_refuses_slips_on_a_build_habit_and_logs_on_a_let_go_habit_and_writes_nothing()
    {
        var user = await Host.SignedInAsync();
        var slip = new { habit_id = 1, date = "2026-10-25", count = 1, reasons = Array.Empty<string>() };

        var slipOnBuild = await ErrorsAsync(await Import(user, WithSlips(Doc(habits: [HabitItem(1)]), slip)));
        var logOnLetGo = await ErrorsAsync(await Import(user, Doc(habits: [LetGoItem(1)],
            logs: [new { habit_id = 1, date = "2026-10-25", completed = true, count = 1 }])));
        var badSlips = await ErrorsAsync(await Import(user, WithSlips(Doc(habits: [LetGoItem(1)]),
            new { habit_id = 1, date = "2026-10-25", count = 0, reasons = Array.Empty<string>() },
            new { habit_id = 1, date = "2026-10-26", count = 1, reasons = new[] { "hungry" } },
            new { habit_id = 1, date = "2026-10-19", count = 1, reasons = Array.Empty<string>() },
            new { habit_id = 1, date = "2026-12-25", count = 1, reasons = Array.Empty<string>() },
            new { habit_id = 9, date = "2026-10-27", count = 1, reasons = Array.Empty<string>() })));
        var invalidLetGo = await ErrorsAsync(await Import(user, Doc(habits: [new { id = 1, name = "Coffee", kind = "let_go", frequency = "weekly", weekly_target = 2, created_at = "2026-10-20T08:00:00Z" }])));

        slipOnBuild.Has("slips[0].habit_id").Should().BeTrue();
        logOnLetGo.Has("logs[0].habit_id").Should().BeTrue();
        badSlips.Has("slips[0].count").Should().BeTrue();
        badSlips.Has("slips[1].reasons").Should().BeTrue();
        badSlips.Has("slips[2].date").Should().BeTrue();
        badSlips.Has("slips[3].date").Should().BeTrue();
        badSlips.Has("slips[4].habit_id").Should().BeTrue();
        invalidLetGo.Has("habits[0].frequency").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        (await Host.Db(db => db.HabitSlips.CountAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task An_account_with_data_is_a_409_and_keeps_its_data()
    {
        var user = await Host.SignedInAsync();
        await user.CreateHabitAsync("Existing");

        await (await Import(user, Doc())).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        (await (await user.Get("/api/habits")).ExpectJson()).EnumerateArray().Select(h => h.GetProperty("name").GetString())
            .Should().Equal("Existing");
    }

    [DockerFact]
    public async Task A_leftover_low_day_makes_the_account_not_empty()
    {
        var user = await Host.SignedInAsync();
        var habit = await user.CreateHabitAsync();
        var today = DateOnly.Parse((await (await user.Get("/api/me")).ExpectJson()).GetProperty("localToday").GetString()!);
        await (await user.Put($"/api/low-days/{today.Iso()}")).Expect(HttpStatusCode.NoContent);
        await (await user.Delete($"/api/habits/{habit.GetProperty("id").GetInt32()}")).Expect(HttpStatusCode.NoContent);

        await (await Import(user, Doc())).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task Only_format_version_2_is_accepted()
    {
        var user = await Host.SignedInAsync();

        (await ErrorsAsync(await Import(user, Doc(formatVersion: 1)))).Has("format_version").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task Import_needs_consent()
    {
        var user = await Host.SignedInAsync(consent: false);

        await (await Import(user, Doc())).ExpectProblem(HttpStatusCode.Forbidden, "consent_required");
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task An_invalid_nested_field_is_a_400_with_its_path_and_writes_nothing()
    {
        var user = await Host.SignedInAsync();
        var doc = Doc(
            habits: [HabitItem(1), HabitItem(2, "   ")],
            logs: [new { habit_id = 1, date = "2026-10-25", completed = true, count = 1 }, new { habit_id = 1, date = "2026-10-26", completed = false, count = 2 }]);

        var errors = await ErrorsAsync(await Import(user, doc));

        errors.Has("habits[1].name").Should().BeTrue();
        errors.Has("logs[1].count").Should().BeTrue();
        errors.Has("habits[0].name").Should().BeFalse();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task More_than_25_habits_is_a_409_habit_limit()
    {
        var user = await Host.SignedInAsync();

        await (await Import(user, Doc(habits: Enumerable.Range(1, 26).Select(i => HabitItem(i, $"Habit {i}")).ToArray())))
            .ExpectProblem(HttpStatusCode.Conflict, "habit_limit");
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task Encrypted_notes_and_moods_import_with_their_journal_key_which_starts_at_version_1()
    {
        var user = await Host.SignedInAsync();
        var moods = new object[] { new { id = 9, encrypted = Blob, archived = false } };
        var note = new { date = "2026-10-25", encrypted = Blob };

        var mixed = await ErrorsAsync(await Import(user, Doc(moods: moods, journalKey: Key,
            dayNotes: [note, new { date = "2026-10-26", text = "Plain", mood = (string?)null }])));
        mixed.Has("day_notes[1].encrypted").Should().BeTrue();

        await (await Import(user, Doc(moods: moods, dayNotes: [note], journalKey: Key))).Expect(HttpStatusCode.NoContent);

        var key = await (await user.Get("/api/me/journal-key")).ExpectJson();
        (key.GetProperty("version").GetInt32(), key.GetProperty("turningOff").GetBoolean()).Should().Be((1, false));
        var notes = await (await user.Get("/api/day-notes?from=2026-10-20")).ExpectJson();
        notes.EnumerateArray().Single().GetProperty("encrypted").GetProperty("ct").GetString().Should().Be(B64(32));
        (await (await user.Get("/api/moods")).ExpectJson()).EnumerateArray().Single().Has("encrypted").Should().BeTrue();
    }

    [DockerFact]
    public async Task A_journal_key_imports_only_with_the_login_password()
    {
        var user = await Host.SignedInAsync();
        var keyWithoutPassword = new
        {
            passphrase = new { salt = B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
            recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        };

        (await ErrorsAsync(await Import(user, Doc(journalKey: keyWithoutPassword)))).Has("password").Should().BeTrue();
        await (await user.Get("/api/me/journal-key")).Expect(HttpStatusCode.NotFound);
    }

    [DockerFact]
    public async Task Encrypted_notes_without_a_journal_key_are_a_400()
    {
        var user = await Host.SignedInAsync();

        var errors = await ErrorsAsync(await Import(user, Doc(dayNotes: [new { date = "2026-10-25", encrypted = Blob }])));

        errors.Has("day_notes[0].encrypted").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task Import_bodies_may_be_up_to_5_mib_while_other_endpoints_keep_64_kib()
    {
        Host.UseKestrel(0);
        var user = await KestrelUserAsync();

        (await user.PostAsJsonAsync("/api/me/import", Doc(padding: new string('x', 70 * 1024)))).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await (await user.GetAsync("/api/habits")).ExpectJson()).GetArrayLength().Should().Be(1);
        (await user.PutAsJsonAsync("/api/day-notes/2026-10-25", new { text = "x", padding = new string('x', 70 * 1024) }))
            .StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var other = await KestrelUserAsync();
        var huge = new HttpRequestMessage(HttpMethod.Post, "/api/me/import")
        {
            Content = new StringContent(JsonSerializer.Serialize(Doc(padding: new string('x', 5 * 1024 * 1024))), Encoding.UTF8, "application/json"),
        };
        huge.Headers.ExpectContinue = true;
        (await other.SendAsync(huge)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await (await other.GetAsync("/api/habits")).ExpectJson()).GetArrayLength().Should().Be(0);
    }

    /// <summary>A confirmed, consented user signed in with a bearer token on a client of the Kestrel server (the test server has no body size limit).</summary>
    private async Task<HttpClient> KestrelUserAsync()
    {
        var http = Host.CreateClient();
        var email = ApiHost.NewEmail();
        await (await http.PostAsJsonAsync("/api/auth/register", new Credentials(email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var link = await Host.OldConfirmLinkAsync((await Host.Mail.WaitForAsync(email, 1)).Single());
        await (await http.PostAsJsonAsync("/api/auth/confirmEmail",
            new ConfirmRequest(link.UserId, link.Code, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var tokens = await (await http.PostAsJsonAsync("/api/auth/login", new Credentials(email, ApiClient.Passphrase))).ExpectJson();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("accessToken").GetString());
        await (await http.PostAsJsonAsync("/api/me/consent", new { policyVersion = Ember.Core.Services.ConsentService.CurrentConsentPolicyVersion, ageConfirmed = true })).Expect(HttpStatusCode.NoContent);
        return http;
    }

    [DockerFact]
    public async Task A_missing_second_reminder_setting_imports_as_off_and_an_invalid_one_is_a_400()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { secondReminderMinutes = 60 })).Expect(HttpStatusCode.OK);

        var errors = await ErrorsAsync(await Import(user, Doc(settings: new { second_reminder_minutes = 45 })));

        errors.Has("settings.second_reminder_minutes").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        await (await Import(user, Doc())).Expect(HttpStatusCode.NoContent);
        (await (await user.Get("/api/me")).ExpectJson()).GetProperty("secondReminderMinutes").GetInt32().Should().Be(0);
    }

    [DockerFact]
    public async Task A_missing_show_streaks_setting_keeps_the_accounts_value_and_a_non_bool_is_a_400()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { showStreaks = true })).Expect(HttpStatusCode.OK);

        var errors = await ErrorsAsync(await Import(user, Doc(settings: new { show_streaks = "yes" })));

        errors.Has("settings.show_streaks").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        await (await Import(user, Doc())).Expect(HttpStatusCode.NoContent);
        (await (await user.Get("/api/me")).ExpectJson()).GetProperty("showStreaks").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Missing_language_and_calm_mode_keep_the_accounts_values_and_invalid_ones_are_a_400()
    {
        var user = await Host.SignedInAsync();
        await (await user.Patch("/api/me", new { language = "de", calmMode = true })).Expect(HttpStatusCode.OK);

        var language = await ErrorsAsync(await Import(user, Doc(settings: new { language = "fr" })));
        var calmMode = await ErrorsAsync(await Import(user, Doc(settings: new { calm_mode = "yes" })));

        language.Has("settings.language").Should().BeTrue();
        calmMode.Has("settings.calm_mode").Should().BeTrue();
        (await HabitCountAsync(await user.IdAsync())).Should().Be(0);
        await (await Import(user, Doc())).Expect(HttpStatusCode.NoContent);
        var me = await (await user.Get("/api/me")).ExpectJson();
        me.GetProperty("language").GetString().Should().Be("de");
        me.GetProperty("calmMode").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Imported_language_and_calm_mode_replace_the_accounts_values()
    {
        var user = await Host.SignedInAsync();

        await (await Import(user, Doc(settings: new { language = "de", calm_mode = true }))).Expect(HttpStatusCode.NoContent);

        var me = await (await user.Get("/api/me")).ExpectJson();
        me.GetProperty("language").GetString().Should().Be("de");
        me.GetProperty("calmMode").GetBoolean().Should().BeTrue();
    }

    [DockerFact]
    public async Task Imported_tips_seen_join_the_accounts_own_known_ids_only_and_a_missing_list_keeps_them()
    {
        var user = await Host.SignedInAsync();
        var keeps = await Host.SignedInAsync();
        foreach (var account in new[] { user, keeps })
            await (await account.Put("/api/me/tips/routines")).Expect(HttpStatusCode.NoContent);

        await (await Import(user, Doc(settings: new { tips_seen = new[] { "android", "leaderboard", "routines" } }))).Expect(HttpStatusCode.NoContent);
        await (await Import(keeps, Doc())).Expect(HttpStatusCode.NoContent);

        (await (await user.Get("/api/me")).ExpectJson()).GetProperty("tipsSeen").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("routines", "android");
        (await (await keeps.Get("/api/me")).ExpectJson()).GetProperty("tipsSeen").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("routines");
    }
}
