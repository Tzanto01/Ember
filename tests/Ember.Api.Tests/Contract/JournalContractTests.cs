using System.Net;

namespace Ember.Api.Tests.Contract;

/// <summary>Journal notes, habit notes, moods, the private-journal key (plaintext and encrypted forms), "What helps me" and stats.</summary>
[Collection("Postgres")]
public class JournalContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "journal")
{
    private const string DayNotes = "/api/day-notes";
    private const string DayNote = "/api/day-notes/{date}";
    private const string HabitNotes = "/api/habit-notes";
    private const string HabitNote = "/api/habits/{id:int}/notes/{date}";
    private const string Moods = "/api/moods";
    private const string Mood = "/api/moods/{id:int}";
    private const string JournalKey = "/api/me/journal-key";
    private const string Helps = "/api/me/helps";
    private const string Wrong = "wrong-long-passphrase";

    private sealed record KeyBody(object Passphrase, object Recovery, string? Password, int? IfVersion);

    private sealed record TurnOffBody(string? Password);

    private sealed record DeleteKeyBody(string? Password, bool DiscardEncrypted);

    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    private static object Blob(int ct = 32) => new { v = 1, iv = B64(12), ct = B64(ct) };

    private static KeyBody Key(string? secret = ApiClient.Passphrase, int? ifVersion = null, int iterations = 600_000) => new(
        new { salt = B64(16), iterations, iv = B64(12), wrapped = B64(48) },
        new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        secret, ifVersion);

    private string Url(string template, int daysAgo) => template.Replace("{date}", Host.Today.AddDays(-daysAgo).Iso());

    private static async Task<int> IdOf(HttpResponseMessage response)
        => (await response.ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    [DockerFact]
    public async Task Journal_exchanges_match_the_golden()
    {
        var user = await Host.SignedInAsync();
        var habit = (await user.CreateHabitAsync("Water")).GetProperty("id").GetInt32();
        Host.Time.Advance(TimeSpan.FromDays(3));

        await Stats(user, habit);
        var mood = await Plaintext(user, habit);
        await Encrypted(user, habit, mood);
        await Helps_(user);
        await ImportEncrypted();
        Verify();
    }

    private async Task Stats(ApiClient user, int habit)
    {
        await Rec("stats.weekly.no-checkins", "/api/stats/weekly", user.Get("/api/stats/weekly"));
        await user.Put($"/api/habits/{habit}/days/{Host.Today.AddDays(-1).Iso()}", new { completed = true });
        await Rec("stats.weekly.ok", "/api/stats/weekly", user.Get("/api/stats/weekly"));
        await Rec("stats.weekly.unauthenticated", "/api/stats/weekly", Host.NewClient().Get("/api/stats/weekly"));
        await Rec("stats.days.ok", "/api/stats/days", user.Get("/api/stats/days"));
        await Rec("stats.days.range", "/api/stats/days", user.Get($"/api/stats/days?from={Host.Today.AddDays(-2).Iso()}&to={Host.Today.Iso()}"));
        await Rec("stats.days.bad-date", "/api/stats/days", user.Get("/api/stats/days?from=lately"));
        await Rec("stats.days.future", "/api/stats/days", user.Get($"/api/stats/days?to={Host.Today.AddDays(1).Iso()}"));
        await Rec("stats.days.inverted", "/api/stats/days", user.Get($"/api/stats/days?from={Host.Today.Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("stats.days.too-long", "/api/stats/days", user.Get($"/api/stats/days?from={Host.Today.AddDays(-500).Iso()}"));
    }

    private async Task<int> Plaintext(ApiClient user, int habit)
    {
        await Rec("day-notes.get.empty", DayNotes, user.Get(DayNotes));
        await Rec("moods.get.empty", Moods, user.Get(Moods));
        var mood = await IdOf(await Rec("mood.post.ok", Moods, user.Post(Moods, new { label = "Proud", emoji = "\U0001F31F" })));
        await Rec("mood.post.label-only", Moods, user.Post(Moods, new { label = "Calm" }));
        await Rec("mood.post.archived", Moods, user.Post(Moods, new { label = "Old", archived = true }));
        await Rec("mood.post.invalid", Moods, user.Post(Moods, new { label = "", emoji = "abc" }));
        await Rec("mood.post.encrypted-without-key", Moods, user.Post(Moods, new { encrypted = Blob() }));

        await Rec("day-note.put.text-and-mood", DayNote, user.Put(Url(DayNote, 1), new { text = "  Good walk  ", mood = "good" }));
        await Rec("day-note.put.custom-mood", DayNote, user.Put(Url(DayNote, 2), new { mood = $"custom:{mood}" }));
        await Rec("day-note.put.today", DayNote, user.Put(Url(DayNote, 0), new { text = "Today" }));
        await Rec("day-note.put.empty", DayNote, user.Put(Url(DayNote, 3), new { }));
        await Rec("day-note.put.invalid", DayNote, user.Put(Url(DayNote, 1), new { text = new string('a', 2001), mood = "ecstatic" }));
        await Rec("day-note.put.encrypted-without-key", DayNote, user.Put(Url(DayNote, 1), new { encrypted = Blob() }));
        await Rec("day-note.put.bad-date", DayNote, user.Put("/api/day-notes/yesterday", new { text = "x" }));
        await Rec("day-note.put.in-future", DayNote, user.Put($"{DayNotes}/{Host.Today.AddDays(1).Iso()}", new { text = "x" }));
        await Rec("day-note.put.before-start", DayNote, user.Put(Url(DayNote, 30), new { text = "x" }));
        await Rec("day-notes.get.ok", DayNotes, user.Get(DayNotes));
        await Rec("day-notes.get.range", DayNotes, user.Get($"{DayNotes}?from={Host.Today.AddDays(-1).Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("day-notes.get.bad-date", DayNotes, user.Get($"{DayNotes}?from=soon"));
        await Rec("day-notes.get.inverted", DayNotes, user.Get($"{DayNotes}?from={Host.Today.Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("day-notes.get.too-long", DayNotes, user.Get($"{DayNotes}?from={Host.Today.AddDays(-400).Iso()}"));
        await Rec("day-note.delete.ok", DayNote, user.Delete(Url(DayNote, 3)));
        await Rec("day-note.delete.bad-date", DayNote, user.Delete("/api/day-notes/yesterday"));

        await Rec("habit-notes.get.empty", HabitNotes, user.Get(HabitNotes));
        await Rec("habit-note.put.ok", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.AddDays(-1).Iso()}", new { text = "  Felt easy  " }));
        await Rec("habit-note.put.missing-text", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.Iso()}", new { }));
        await Rec("habit-note.put.too-long", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.Iso()}", new { text = new string('a', 281) }));
        await Rec("habit-note.put.not-found", HabitNote, user.Put($"/api/habits/9999/notes/{Host.Today.Iso()}", new { text = "x" }));
        await Rec("habit-note.put.bad-date", HabitNote, user.Put($"/api/habits/{habit}/notes/yesterday", new { text = "x" }));
        await Rec("habit-note.put.in-future", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.AddDays(1).Iso()}", new { text = "x" }));
        await Rec("habit-note.put.before-start", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.AddDays(-30).Iso()}", new { text = "x" }));
        await Rec("habit-notes.get.ok", HabitNotes, user.Get(HabitNotes));
        await Rec("habit-notes.get.filtered", HabitNotes, user.Get($"{HabitNotes}?habitId={habit}"));
        await Rec("habit-notes.get.bad-date", HabitNotes, user.Get($"{HabitNotes}?to=soon"));
        await Rec("habit-note.delete.ok", HabitNote, user.Delete($"/api/habits/{habit}/notes/{Host.Today.AddDays(-1).Iso()}"));
        await Rec("habit-note.delete.again", HabitNote, user.Delete($"/api/habits/{habit}/notes/{Host.Today.AddDays(-1).Iso()}"));
        await Rec("habit-note.put.again", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.AddDays(-1).Iso()}", new { text = "Back again" }));

        await Rec("moods.get.ok", Moods, user.Get(Moods));
        await Rec("mood.patch.label", Mood, user.Patch($"{Moods}/{mood}", new { label = "Very proud" }));
        await Rec("mood.patch.clear-emoji", Mood, user.Patch($"{Moods}/{mood}", new { emoji = (string?)null }));
        await Rec("mood.patch.archive", Mood, user.Patch($"{Moods}/{mood}", new { archived = true }));
        await Rec("mood.patch.invalid", Mood, user.Patch($"{Moods}/{mood}", new { label = new string('x', 25), emoji = "no" }));
        await Rec("mood.patch.not-an-object", Mood, user.Patch($"{Moods}/{mood}", new[] { 1 }));
        await Rec("mood.patch.not-found", Mood, user.Patch($"{Moods}/9999", new { label = "x" }));
        await Rec("mood.patch.restore", Mood, user.Patch($"{Moods}/{mood}", new { archived = false }));

        var full = await Host.SignedInAsync();
        for (var i = 0; i < 20; i++) await full.Post(Moods, new { label = $"Mood {i}" });
        await Rec("mood.post.limit", Moods, full.Post(Moods, new { label = "One too many" }));
        await Rec("mood.patch.limit", Mood, full.Patch($"{Moods}/{await IdOf(await full.Post(Moods, new { label = "Archived", archived = true }))}", new { archived = false }));

        var withdrawn = await Host.SignedInAsync();
        await withdrawn.CreateHabitAsync("Water");
        Host.Time.Advance(TimeSpan.FromDays(1));
        await withdrawn.Post("/api/me/consent/withdraw");
        await Rec("day-note.put.consent-required", DayNote, withdrawn.Put(Url(DayNote, 0), new { text = "x" }));
        await Rec("habit-note.put.consent-required", HabitNote, withdrawn.Put($"/api/habits/1/notes/{Host.Today.Iso()}", new { text = "x" }));
        await Rec("mood.post.consent-required", Moods, withdrawn.Post(Moods, new { label = "x" }));
        await Rec("mood.patch.consent-required", Mood, withdrawn.Patch($"{Moods}/1", new { label = "x" }));
        await Rec("day-notes.get.no-consent", DayNotes, withdrawn.Get(DayNotes));
        await Rec("unauthenticated.day-notes", DayNotes, Host.NewClient().Get(DayNotes));
        await Rec("unauthenticated.moods", Moods, Host.NewClient().Get(Moods));
        return mood;
    }

    private async Task Encrypted(ApiClient user, int habit, int plainMood)
    {
        await Rec("journal-key.get.none", JournalKey, user.Get(JournalKey));
        await Rec("journal-key.put.invalid", JournalKey, user.Put(JournalKey, Key(iterations: 1000)));
        await Rec("journal-key.put.wrong-password", JournalKey, user.Put(JournalKey, Key(Wrong)));
        await Rec("journal-key.turn-off.none", JournalKey + "/turn-off", user.Post($"{JournalKey}/turn-off", new TurnOffBody(ApiClient.Passphrase)));
        await Rec("journal-key.put.ok", JournalKey, user.Put(JournalKey, Key()));
        await Rec("journal-key.put.stale", JournalKey, user.Put(JournalKey, Key(ifVersion: 7)));
        await Rec("journal-key.put.replace", JournalKey, user.Put(JournalKey, Key(ifVersion: 1)));
        await Rec("journal-key.get.ok", JournalKey, user.Get(JournalKey));
        await Rec("me.journal-on", "/api/me", user.Get("/api/me"));

        await Rec("day-note.put.plaintext-while-on", DayNote, user.Put(Url(DayNote, 1), new { text = "plain" }));
        await Rec("day-note.put.mixed", DayNote, user.Put(Url(DayNote, 1), new { text = "plain", encrypted = Blob() }));
        await Rec("day-note.put.bad-blob", DayNote, user.Put(Url(DayNote, 1), new { encrypted = new { v = 2, iv = "x", ct = "y" } }));
        await Rec("day-note.put.encrypted", DayNote, user.Put(Url(DayNote, 1), new { encrypted = Blob() }));
        await Rec("day-notes.get.mixed", DayNotes, user.Get(DayNotes));
        await Rec("habit-note.put.plaintext-while-on", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.Iso()}", new { text = "plain" }));
        await Rec("habit-note.put.bad-blob", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.Iso()}", new { encrypted = new { v = 1, iv = B64(12), ct = B64(3075) } }));
        await Rec("habit-note.put.encrypted", HabitNote, user.Put($"/api/habits/{habit}/notes/{Host.Today.Iso()}", new { encrypted = Blob() }));
        await Rec("habit-notes.get.mixed", HabitNotes, user.Get(HabitNotes));
        await Rec("mood.post.plaintext-while-on", Moods, user.Post(Moods, new { label = "plain" }));
        var secret = await IdOf(await Rec("mood.post.encrypted", Moods, user.Post(Moods, new { encrypted = Blob() })));
        await Rec("mood.patch.encrypted", Mood, user.Patch($"{Moods}/{secret}", new { encrypted = Blob(), archived = true }));
        await Rec("mood.patch.plaintext-on-encrypted", Mood, user.Patch($"{Moods}/{secret}", new { label = "plain" }));
        await Rec("moods.get.mixed", Moods, user.Get(Moods));

        await Rec("journal-key.turn-off.wrong-password", JournalKey + "/turn-off", user.Post($"{JournalKey}/turn-off", new TurnOffBody(Wrong)));
        await Rec("journal-key.turn-off.ok", JournalKey + "/turn-off", user.Post($"{JournalKey}/turn-off", new TurnOffBody(ApiClient.Passphrase)));
        await Rec("me.journal-turning-off", "/api/me", user.Get("/api/me"));
        await Rec("day-note.put.plaintext-while-turning-off", DayNote, user.Put(Url(DayNote, 2), new { text = "plain again" }));
        await Rec("journal-key.delete.wrong-password", JournalKey, user.Send(HttpMethod.Delete, JournalKey, new DeleteKeyBody(Wrong, false)));
        await Rec("journal-key.delete.encrypted-left", JournalKey, user.Send(HttpMethod.Delete, JournalKey, new DeleteKeyBody(ApiClient.Passphrase, false)));
        await Rec("journal-key.delete.discard", JournalKey, user.Send(HttpMethod.Delete, JournalKey, new DeleteKeyBody(ApiClient.Passphrase, true)));
        await Rec("journal-key.get.gone", JournalKey, user.Get(JournalKey));
        await Rec("journal-key.turn-off.again", JournalKey + "/turn-off", user.Post($"{JournalKey}/turn-off", new TurnOffBody(ApiClient.Passphrase)));
        await Rec("mood.patch.after-discard", Mood, user.Patch($"{Moods}/{plainMood}", new { label = "Plain again" }));
    }

    private async Task Helps_(ApiClient user)
    {
        await Rec("helps.get.none", Helps, user.Get(Helps));
        await Rec("helps.put.journal-off", Helps, user.Put(Helps, new { encrypted = "ciphertext", version = 0 }));
        await (await user.Put(JournalKey, Key())).Expect(HttpStatusCode.OK);
        await Rec("helps.put.ok", Helps, user.Put(Helps, new { encrypted = "ciphertext", version = 0 }));
        await Rec("helps.put.stale", Helps, user.Put(Helps, new { encrypted = "newer", version = 0 }));
        await Rec("helps.put.invalid", Helps, user.Put(Helps, new { encrypted = "", version = -1 }));
        await Rec("helps.put.missing-fields", Helps, user.Put(Helps, new { }));
        await Rec("helps.get.ok", Helps, user.Get(Helps));
        await Rec("helps.delete.ok", Helps, user.Delete(Helps));
        await Rec("helps.delete.again", Helps, user.Delete(Helps));
        await Rec("helps.unauthenticated", Helps, Host.NewClient().Get(Helps));

        var withoutConsent = await Host.SignedInAsync(consent: false);
        await Rec("helps.put.consent-required", Helps, withoutConsent.Put(Helps, new { encrypted = "x", version = 0 }));
    }

    private async Task ImportEncrypted()
    {
        var user = await Host.SignedInAsync();
        await Rec("import.encrypted.ok", "/api/me/import", user.Post("/api/me/import", new
        {
            format_version = 2,
            journal_key = Key(),
            habits = new[] { new { id = 1, name = "Water", frequency = "daily", created_at = "2026-10-20T08:00:00Z" } },
            moods = new[] { new { id = 1, encrypted = Blob() } },
            day_notes = new[] { new { date = "2026-10-21", encrypted = Blob() } },
            habit_notes = new[] { new { habit_id = 1, date = "2026-10-21", encrypted = Blob() } },
        }));
        await Rec("export.encrypted.ok", "/api/me/export", user.Get("/api/me/export"));
    }
}
