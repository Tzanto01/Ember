using System.Net;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>The exact errors of <c>POST /api/me/import</c> from the validators it shares with the habit, routine, settings, mood and journal endpoints.</summary>
public class ImportValidationApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Created = "2026-10-20T08:00:00Z";

    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    private static object Habit(int id, string name = "Water", string frequency = "daily", string? kind = null, int? weeklyTarget = null,
        string[]? days = null, int? timesPerDay = null, string[]? reminderTimes = null)
        => new { id, name, frequency, kind, weekly_target = weeklyTarget, days, times_per_day = timesPerDay, reminder_times = reminderTimes, created_at = Created };

    private static object Doc(object[]? habits = null, object? settings = null, object[]? routines = null, object[]? moods = null,
        object[]? dayNotes = null, object[]? habitNotes = null, object? journalKey = null) => new
        {
            format_version = 2,
            settings = settings ?? new { grace_days_per_week = 1 },
            habits = habits ?? [Habit(1)],
            logs = Array.Empty<object>(),
            day_marks = Array.Empty<object>(),
            routines = routines ?? [],
            moods = moods ?? [],
            day_notes = dayNotes ?? [],
            habit_notes = habitNotes ?? [],
            journal_key = journalKey,
        };

    private static object JournalKey(string? password = ApiClient.Passphrase, string? salt = null) => new
    {
        passphrase = new { salt = salt ?? B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
        recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        password,
    };

    private async Task AssertNothingImportedAsync(ApiClient user)
    {
        var userId = await user.IdAsync();
        (await Host.Db(db => db.Habits.CountAsync(h => h.UserId == userId))).Should().Be(0);
    }

    [DockerFact]
    public async Task Invalid_settings_routines_habits_moods_and_notes_are_reported_with_their_exact_paths_messages_and_order()
    {
        var user = await Host.SignedInAsync();
        var doc = Doc(
            settings: new { grace_days_per_week = 4, theme = "neon", second_reminder_minutes = 45, language = "fr" },
            routines: [new { id = 1, name = "  " }, new { id = 2, name = new string('r', 51) }],
            habits:
            [
                Habit(1, name: "  ", frequency: "hourly"),
                Habit(2, name: new string('h', 101), weeklyTarget: 3, days: ["mon"], timesPerDay: 11),
                Habit(3, frequency: "weekly", weeklyTarget: 7, timesPerDay: 2),
                Habit(4, frequency: "weekly", weeklyTarget: 0),
                Habit(5, frequency: "weekdays", days: ["mon", "mon"]),
                Habit(6, frequency: "weekdays", days: ["mon", "tue", "wed", "thu", "fri", "sat", "sun"]),
                Habit(7, frequency: "weekdays", days: []),
                Habit(8, frequency: "weekdays"),
            ],
            moods: [new { id = 1, label = "", emoji = "ab" }, new { id = 2, label = new string('m', 25), emoji = "x" }],
            dayNotes:
            [
                new { date = "2026-10-25", text = new string('n', 2001) },
                new { date = "2026-10-26", mood = "custom:99" },
                new { date = "2026-10-27", mood = "great", text = "ok" },
                new { date = "2026-10-28", mood = "bogus" },
            ],
            habitNotes: [new { habit_id = 1, date = "2026-10-25", text = new string('h', 281) }]);

        await (await user.Post("/api/me/import", doc)).ExpectErrors("""
            {
              "settings.grace_days_per_week":["Must be between 0 and 3."],
              "settings.theme":["Unknown theme."],
              "settings.second_reminder_minutes":["Must be 0, 15, 30, 60 or 120."],
              "settings.language":["Must be en or de."],
              "routines[0].name":["Must be 1 to 50 characters."],
              "routines[1].name":["Must be 1 to 50 characters."],
              "habits[0].name":["Must be 1 to 100 characters."],
              "habits[0].frequency":["Must be daily, weekly or weekdays."],
              "habits[1].name":["Must be 1 to 100 characters."],
              "habits[1].weekly_target":["Only for weekly habits."],
              "habits[1].days":["Only for weekdays habits."],
              "habits[1].times_per_day":["Must be between 1 and 10."],
              "habits[2].weekly_target":["Must be between 1 and 6."],
              "habits[2].times_per_day":["Always 1 for weekly habits."],
              "habits[3].weekly_target":["Must be between 1 and 6."],
              "habits[4].days":["Each must be one of mon, tue, wed, thu, fri, sat, sun, without repeats."],
              "habits[5].days":["All seven days: use daily instead."],
              "habits[6].days":["Pick 1 to 6 days."],
              "habits[7].days":["Required for weekdays habits."],
              "moods[0].label":["Must be 1 to 24 characters."],
              "moods[0].emoji":["Must be a single emoji."],
              "moods[1].label":["Must be 1 to 24 characters."],
              "moods[1].emoji":["Must be a single emoji."],
              "day_notes[0].text":["Must be at most 2000 characters."],
              "day_notes[1].mood":["Must be low, meh, okay, good, great or custom:<id> of a mood in this file."],
              "day_notes[3].mood":["Must be low, meh, okay, good, great or custom:<id> of a mood in this file."],
              "habit_notes[0].text":["Must be at most 280 characters."]
            }
            """);
        await AssertNothingImportedAsync(user);
    }

    [DockerFact]
    public async Task Let_go_habits_are_checked_against_the_let_go_rules_with_their_exact_messages()
    {
        var user = await Host.SignedInAsync();
        var doc = Doc(habits:
        [
            Habit(1, kind: "let_go", timesPerDay: 3, reminderTimes: ["08:00", "09:00"]),
            Habit(2, kind: "let_go", frequency: "weekdays", weeklyTarget: 2, days: ["mon"]),
            Habit(3, kind: "let_go", frequency: "weekly", weeklyTarget: 3, timesPerDay: 2, days: ["tue"], reminderTimes: ["08:00", "09:00"]),
        ]);

        await (await user.Post("/api/me/import", doc)).ExpectErrors("""
            {
              "habits[0].times_per_day":["Always 1 for let-go habits."],
              "habits[0].reminder_times":["At most one for let-go habits."],
              "habits[1].weekly_target":["Not for let-go habits."],
              "habits[1].frequency":["Let-go habits are daily."],
              "habits[1].days":["Not for let-go habits."],
              "habits[2].days":["Not for let-go habits."],
              "habits[2].times_per_day":["Always 1 for let-go habits."],
              "habits[2].reminder_times":["At most one for let-go habits."],
              "habits[2].frequency":["Let-go habits are daily."],
              "habits[2].weekly_target":["Not for let-go habits."]
            }
            """);
        await AssertNothingImportedAsync(user);
    }

    [DockerFact]
    public async Task An_invalid_journal_key_in_the_file_is_reported_per_field_under_journal_key()
    {
        var user = await Host.SignedInAsync();

        await (await user.Post("/api/me/import", Doc(journalKey: new
        {
            passphrase = new { salt = B64(15), iterations = 599_999, iv = B64(11), wrapped = B64(47) },
            recovery = new { salt = B64(15), iv = B64(11), wrapped = B64(47) },
            password = ApiClient.Passphrase,
        }))).ExpectErrors("""
            {
              "journal_key.passphrase.salt":["Must be base64 of 16 bytes."],
              "journal_key.passphrase.iterations":["Must be between 600000 and 10000000."],
              "journal_key.passphrase.iv":["Must be base64 of 12 bytes."],
              "journal_key.passphrase.wrapped":["Must be base64 of 48 bytes."],
              "journal_key.recovery.salt":["Must be base64 of 16 bytes."],
              "journal_key.recovery.iv":["Must be base64 of 12 bytes."],
              "journal_key.recovery.wrapped":["Must be base64 of 48 bytes."]
            }
            """);
        await AssertNothingImportedAsync(user);
    }

    [DockerFact]
    public async Task A_journal_key_in_the_file_with_a_wrong_or_missing_login_password_is_a_400_on_password_before_anything_else_is_checked()
    {
        var user = await Host.SignedInAsync();
        var invalidHabit = Doc(habits: [Habit(1, name: "")], journalKey: JournalKey("wrong-passphrase"));

        var wrong = await user.Post("/api/me/import", invalidHabit);
        var missing = await user.Post("/api/me/import", Doc(journalKey: JournalKey(null)));

        const string expected = """{"type":"validation","title":"One or more validation errors occurred.","status":400,"errors":{"password":["The password is not correct."]}}""";
        await wrong.ExpectExactProblem(HttpStatusCode.BadRequest, expected);
        await missing.ExpectExactProblem(HttpStatusCode.BadRequest, expected);
        await AssertNothingImportedAsync(user);
    }
}
