using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>Notes on habits (contract "Today widget v1.x": <c>/api/habit-notes</c>, <c>/api/habits/{id}/notes/{date}</c>).</summary>
[Collection("Postgres")]
public class HabitNoteApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    private static readonly object Blob = new { v = 1, iv = B64(12), ct = B64(32) };

    private static object KeyBody() => new
    {
        passphrase = new { salt = B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
        recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        password = ApiClient.Passphrase,
    };

    private static async Task<int> HabitIdAsync(ApiClient user, string? name = null)
        => (await user.CreateHabitAsync(name)).GetProperty("id").GetInt32();

    private static async Task<List<JsonElement>> NotesAsync(ApiClient user, string query = "")
        => (await (await user.Get($"/api/habit-notes{query}")).ExpectJson()).EnumerateArray().ToList();

    private static async Task ExpectFieldError(HttpResponseMessage response, string field)
        => (await response.ExpectProblem(HttpStatusCode.BadRequest, "validation")).GetProperty("errors").Has(field).Should().BeTrue();

    private static async Task<JsonElement> JournalAsync(ApiClient user)
        => (await (await user.Get("/api/me")).ExpectJson()).GetProperty("journal");

    [DockerFact]
    public async Task Put_trims_get_filters_by_habit_and_delete_removes_it()
    {
        var user = await Host.SignedInAsync();
        var walk = await HabitIdAsync(user);
        var read = await HabitIdAsync(user);
        var today = Host.Today.Iso();

        var saved = await (await user.Put($"/api/habits/{walk}/notes/{today}", new { text = "  Short loop.\nRain.  " })).ExpectJson();
        await (await user.Put($"/api/habits/{read}/notes/{today}", new { text = "Two chapters" })).Expect(HttpStatusCode.OK);

        saved.GetProperty("habitId").GetInt32().Should().Be(walk);
        saved.GetProperty("date").GetString().Should().Be(today);
        saved.GetProperty("text").GetString().Should().Be("Short loop.\nRain.");
        (await NotesAsync(user)).Should().HaveCount(2);
        var filtered = (await NotesAsync(user, $"?habitId={read}")).Single();
        filtered.GetProperty("habitId").GetInt32().Should().Be(read);
        filtered.GetProperty("text").GetString().Should().Be("Two chapters");

        await (await user.Delete($"/api/habits/{walk}/notes/{today}")).Expect(HttpStatusCode.NoContent);
        (await NotesAsync(user)).Select(n => n.GetProperty("habitId").GetInt32()).Should().Equal(read);
    }

    [DockerFact]
    public async Task Text_is_at_most_280_characters_after_trimming_and_an_empty_put_is_refused()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        var today = Host.Today.Iso();

        await ExpectFieldError(await user.Put($"/api/habits/{habit}/notes/{today}", new { text = new string('a', 281) }), "text");
        await ExpectFieldError(await user.Put($"/api/habits/{habit}/notes/{today}", new { text = "  \n " }), "text");
        await ExpectFieldError(await user.Put($"/api/habits/{habit}/notes/{today}", new { }), "text");
        await (await user.Put($"/api/habits/{habit}/notes/{today}", new { text = $"  {new string('a', 280)}  " })).Expect(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task With_the_private_journal_on_notes_must_be_encrypted_and_ct_is_at_most_4096_characters()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        var url = $"/api/habits/{habit}/notes/{Host.Today.Iso()}";
        await ExpectFieldError(await user.Put(url, new { encrypted = Blob }), "encrypted");
        await (await user.Put("/api/me/journal-key", KeyBody())).Expect(HttpStatusCode.OK);

        await ExpectFieldError(await user.Put(url, new { text = "Plain" }), "encrypted");
        await ExpectFieldError(await user.Put(url, new { text = "Plain", encrypted = Blob }), "encrypted");
        await ExpectFieldError(await user.Put(url, new { encrypted = new { v = 1, iv = B64(12), ct = B64(3075) } }), "encrypted.ct");
        await (await user.Put(url, new { encrypted = new { v = 1, iv = B64(12), ct = B64(3072) } })).Expect(HttpStatusCode.OK);
        var saved = await (await user.Put(url, new { encrypted = Blob })).ExpectJson();

        saved.Has("text").Should().BeFalse();
        var note = (await NotesAsync(user)).Single();
        note.GetProperty("habitId").GetInt32().Should().Be(habit);
        note.GetProperty("encrypted").GetProperty("v").GetInt32().Should().Be(1);
        note.GetProperty("encrypted").GetProperty("ct").GetString().Should().Be(B64(32));
        note.Has("text").Should().BeFalse();
    }

    [DockerFact]
    public async Task Writes_need_consent_but_reads_and_delete_dont()
    {
        var user = await Host.SignedInAsync();
        var userId = await user.IdAsync();
        var habit = await HabitIdAsync(user);
        var today = Host.Today.Iso();
        await (await user.Put($"/api/habits/{habit}/notes/{today}", new { text = "Hi" })).Expect(HttpStatusCode.OK);
        await Host.Db(db => db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s.SetProperty(u => u.HealthDataConsentAt, (DateTime?)null)));

        await (await user.Put($"/api/habits/{habit}/notes/{today}", new { text = "Again" }))
            .ExpectProblem(HttpStatusCode.Forbidden, "consent_required");
        (await NotesAsync(user)).Single().GetProperty("text").GetString().Should().Be("Hi");
        await (await user.Delete($"/api/habits/{habit}/notes/{today}")).Expect(HttpStatusCode.NoContent);
        (await NotesAsync(user)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Another_users_habit_is_a_404_for_put_and_delete_its_note_stays_and_get_leaves_it_out()
    {
        var owner = await Host.SignedInAsync();
        var other = await Host.SignedInAsync();
        var habit = await HabitIdAsync(owner);
        var today = Host.Today.Iso();
        await (await owner.Put($"/api/habits/{habit}/notes/{today}", new { text = "Mine" })).Expect(HttpStatusCode.OK);

        await (await other.Put($"/api/habits/{habit}/notes/{today}", new { text = "Theirs" })).ExpectProblem(HttpStatusCode.NotFound, "not_found");
        await (await other.Delete($"/api/habits/{habit}/notes/{today}")).ExpectProblem(HttpStatusCode.NotFound, "not_found");
        await (await other.Put($"/api/habits/999999/notes/{today}", new { text = "Nobody" })).ExpectProblem(HttpStatusCode.NotFound, "not_found");
        (await NotesAsync(other, $"?habitId={habit}")).Should().BeEmpty();
        (await NotesAsync(owner)).Single().GetProperty("text").GetString().Should().Be("Mine");
    }

    [DockerFact]
    public async Task Dates_cant_be_in_the_future_or_before_the_habits_start_unless_a_note_is_already_there()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        var day1 = Host.Today;
        await (await user.Put($"/api/habits/{habit}/notes/{day1.Iso()}", new { text = "First" })).Expect(HttpStatusCode.OK);

        await (await user.Put($"/api/habits/{habit}/notes/{day1.AddDays(1).Iso()}", new { text = "Soon" }))
            .ExpectProblem(HttpStatusCode.UnprocessableEntity, "in_future");
        await (await user.Put($"/api/habits/{habit}/notes/{day1.AddDays(-1).Iso()}", new { text = "Before" }))
            .ExpectProblem(HttpStatusCode.UnprocessableEntity, "before_habit_start");

        Host.Time.Advance(TimeSpan.FromDays(2));
        await Host.Db(db => db.Habits.Where(h => h.Id == habit)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.CreatedAt, h => h.CreatedAt.AddDays(1))));
        await (await user.Put($"/api/habits/{habit}/notes/{day1.Iso()}", new { text = "Rewritten" })).Expect(HttpStatusCode.OK);
        await (await user.Put($"/api/habits/{habit}/notes/{day1.AddDays(-1).Iso()}", new { text = "Before" }))
            .ExpectProblem(HttpStatusCode.UnprocessableEntity, "before_habit_start");
    }

    [DockerFact]
    public async Task Journal_counts_habit_notes_and_the_key_goes_only_after_their_encrypted_ones_are_discarded()
    {
        var user = await Host.SignedInAsync();
        var plain = await HabitIdAsync(user);
        var secret = await HabitIdAsync(user);
        var today = Host.Today.Iso();
        await (await user.Put($"/api/habits/{plain}/notes/{today}", new { text = "Plain" })).Expect(HttpStatusCode.OK);
        await (await user.Put("/api/me/journal-key", KeyBody())).Expect(HttpStatusCode.OK);
        await (await user.Put($"/api/habits/{secret}/notes/{today}", new { encrypted = Blob })).Expect(HttpStatusCode.OK);

        var journal = await JournalAsync(user);
        journal.GetProperty("plaintextLeft").GetInt32().Should().Be(1);
        journal.GetProperty("encryptedLeft").GetInt32().Should().Be(1);

        await (await user.Send(HttpMethod.Delete, "/api/me/journal-key", new { password = ApiClient.Passphrase, discardEncrypted = false }))
            .ExpectProblem(HttpStatusCode.Conflict, "conflict");
        await (await user.Send(HttpMethod.Delete, "/api/me/journal-key", new { password = ApiClient.Passphrase, discardEncrypted = true }))
            .Expect(HttpStatusCode.NoContent);

        (await NotesAsync(user)).Select(n => n.GetProperty("habitId").GetInt32()).Should().Equal(plain);
        (await JournalAsync(user)).GetProperty("encryptedLeft").GetInt32().Should().Be(0);
    }

    [DockerFact]
    public async Task Deleting_the_habit_or_the_account_deletes_its_notes()
    {
        var user = await Host.SignedInAsync();
        var gone = await HabitIdAsync(user);
        var kept = await HabitIdAsync(user);
        var today = Host.Today.Iso();
        await (await user.Put($"/api/habits/{gone}/notes/{today}", new { text = "Bye" })).Expect(HttpStatusCode.OK);
        await (await user.Put($"/api/habits/{kept}/notes/{today}", new { text = "Stay" })).Expect(HttpStatusCode.OK);

        await (await user.Delete($"/api/habits/{gone}")).Expect(HttpStatusCode.NoContent);
        (await Host.Db(db => db.HabitNotes.CountAsync(n => n.HabitId == gone))).Should().Be(0);
        (await Host.Db(db => db.HabitNotes.CountAsync(n => n.HabitId == kept))).Should().Be(1);

        await (await user.Post("/api/me/delete", new { password = ApiClient.Passphrase, confirm = "DELETE" })).Expect(HttpStatusCode.NoContent);
        (await Host.Db(db => db.HabitNotes.CountAsync(n => n.HabitId == kept))).Should().Be(0);
    }

    [DockerFact]
    public async Task The_full_export_carries_habit_notes_in_json_and_csv()
    {
        var user = await Host.SignedInAsync();
        var habit = await HabitIdAsync(user);
        var today = Host.Today.Iso();
        await (await user.Put($"/api/habits/{habit}/notes/{today}", new { text = "Felt good" })).Expect(HttpStatusCode.OK);

        var json = await (await user.Get("/api/me/export?format=json")).ExpectJson();
        var csv = await (await user.Get("/api/me/export?format=csv")).Expect(HttpStatusCode.OK);

        var note = json.GetProperty("habit_notes").EnumerateArray().Single();
        note.GetProperty("habit_id").GetInt32().Should().Be(habit);
        note.GetProperty("date").GetString().Should().Be(today);
        note.GetProperty("text").GetString().Should().Be("Felt good");
        csv.Split("\r\n").Should().ContainSingle(l => l.StartsWith("habit_note,") && l.Contains($",{today},") && l.Contains(",Felt good,"));
    }
}
