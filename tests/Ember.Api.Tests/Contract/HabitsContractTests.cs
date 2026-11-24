using System.Net;

namespace Ember.Api.Tests.Contract;

/// <summary>Habits of both kinds, their days, slips and made-it marks, and the habit templates.</summary>
[ContractArea("habits")]
public class HabitsContractTests(PostgresFixture postgres) : ContractTestBase(postgres)
{
    private const string Habits = "/api/habits";
    private const string One = "/api/habits/{id:int}";
    private const string Days = "/api/habits/{id:int}/days";
    private const string Day = "/api/habits/{id:int}/days/{date}";
    private const string Slips = "/api/habits/{id:int}/slips";
    private const string Slip = "/api/habits/{id:int}/slips/{date}";
    private const string MadeItDays = "/api/habits/{id:int}/made-it";
    private const string MadeIt = "/api/habits/{id:int}/made-it/{date}";

    private static async Task<int> IdOf(HttpResponseMessage response)
        => (await response.ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    private string DayUrl(int id, int daysAgo) => $"/api/habits/{id}/days/{Host.Today.AddDays(-daysAgo).Iso()}";

    private string SlipUrl(int id, int daysAgo) => $"/api/habits/{id}/slips/{Host.Today.AddDays(-daysAgo).Iso()}";

    private string MadeItUrl(int id, int daysAgo) => $"/api/habits/{id}/made-it/{Host.Today.AddDays(-daysAgo).Iso()}";

    [DockerFact]
    public async Task Habit_exchanges_match_the_golden()
    {
        var user = await Host.SignedInAsync();
        await Rec("templates.ok", "/api/habit-templates", Host.NewClient().Get("/api/habit-templates"));
        await Rec("list.empty", Habits, user.Get(Habits));
        await Rec("list.unauthenticated", Habits, Host.NewClient().Get(Habits));

        var ids = await Creating(user);
        Host.Time.Advance(TimeSpan.FromDays(6));
        await Reading(user, ids);
        await Updating(user, ids);
        await CheckIns(user, ids);
        await SlipsOfLetGo(user, ids);
        await MadeItOfLetGo(user, ids);
        await Pausing(user, ids);
        await Consent();
        await Limit();
        await Deleting(user, ids);
        Verify();
    }

    private sealed record Created(int Daily, int Weekly, int Weekdays, int Multi, int LetGo);

    private async Task<Created> Creating(ApiClient user)
    {
        var daily = await IdOf(await Rec("create.build.minimal", Habits, user.Post(Habits, new { name = "Water", frequency = "daily" })));
        var weekly = await IdOf(await Rec("create.build.weekly", Habits, user.Post(Habits, new { name = "Run", frequency = "weekly", weeklyTarget = 3, reminderTime = "07:30" })));
        var weekdays = await IdOf(await Rec("create.build.weekdays", Habits, user.Post(Habits, new { name = "Gym", frequency = "weekdays", days = new[] { "mon", "wed", "fri" } })));
        var multi = await IdOf(await Rec("create.build.times-per-day", Habits, user.Post(Habits, new
        {
            name = "Pills",
            frequency = "daily",
            kind = "build",
            timerMinutes = 30,
            timesPerDay = 3,
            reminderTimes = new[] { "08:00", "13:00", "20:00" },
        })));
        var letGo = await IdOf(await Rec("create.let-go", Habits, user.Post(Habits, new { name = "Coffee", frequency = "daily", kind = "letGo", reminderTime = "21:30" })));

        await Rec("create.invalid", Habits, user.Post(Habits, new
        {
            name = "",
            frequency = "hourly",
            kind = "other",
            weeklyTarget = 9,
            days = new[] { "mon" },
            timesPerDay = 0,
            reminderTime = "25:00",
            reminderExclusions = new[] { "browser-999" },
        }));
        await Rec("create.invalid.reminder-times", Habits, user.Post(Habits, new { name = "A", frequency = "daily", timesPerDay = 1, reminderTimes = new[] { "08:00", "09:00" } }));
        await Rec("create.invalid.weekdays", Habits, user.Post(Habits, new { name = "A", frequency = "weekdays" }));
        await Rec("create.invalid.let-go", Habits, user.Post(Habits, new
        {
            name = "A",
            kind = "letGo",
            frequency = "weekly",
            weeklyTarget = 2,
            timesPerDay = 2,
            days = new[] { "mon" },
            reminderTimes = new[] { "20:00", "21:00" },
        }));
        await Rec("create.wrong-type", Habits, user.Post(Habits, new { name = "A", timesPerDay = "many" }));
        return new Created(daily, weekly, weekdays, multi, letGo);
    }

    private async Task Reading(ApiClient user, Created ids)
    {
        await Rec("list.ok", Habits, user.Get(Habits));
        await Rec("get.build", One, user.Get($"{Habits}/{ids.Daily}"));
        await Rec("get.build.weekly", One, user.Get($"{Habits}/{ids.Weekly}"));
        await Rec("get.let-go", One, user.Get($"{Habits}/{ids.LetGo}"));
        await Rec("get.not-found", One, user.Get($"{Habits}/9999"));
        await Rec("get.unauthenticated", One, Host.NewClient().Get($"{Habits}/{ids.Daily}"));
    }

    private async Task Updating(ApiClient user, Created ids)
    {
        await Rec("patch.ok", One, user.Patch($"{Habits}/{ids.Daily}", new { name = "Drink water", reminderTime = "09:00" }));
        await Rec("patch.clear-reminder", One, user.Patch($"{Habits}/{ids.Daily}", new { clearReminder = true }));
        await Rec("patch.schedule", One, user.Patch($"{Habits}/{ids.Weekdays}", new { days = new[] { "tue", "thu" } }));
        await Rec("patch.times-per-day", One, user.Patch($"{Habits}/{ids.Multi}", new { timesPerDay = 2, reminderTimes = new[] { "08:00" } }));
        await Rec("patch.let-go", One, user.Patch($"{Habits}/{ids.LetGo}", new { name = "Less coffee", reminderTime = "20:00" }));
        await Rec("patch.invalid", One, user.Patch($"{Habits}/{ids.Daily}", new { name = "", frequency = "hourly", weeklyTarget = 2, reminderTime = "9am", reminderExclusions = new[] { "phone-0" } }));
        await Rec("patch.clear-conflict", One, user.Patch($"{Habits}/{ids.Daily}", new { reminderTime = "09:00", clearReminder = true }));
        await Rec("patch.kind-change", One, user.Patch($"{Habits}/{ids.Daily}", new { kind = "letGo" }));
        await Rec("patch.invalid.let-go", One, user.Patch($"{Habits}/{ids.LetGo}", new { frequency = "weekly", weeklyTarget = 2, timesPerDay = 3 }));
        await Rec("patch.not-found", One, user.Patch($"{Habits}/9999", new { name = "Nope" }));
    }

    private async Task CheckIns(ApiClient user, Created ids)
    {
        await Rec("days.get.ok", Days, user.Get($"{Habits}/{ids.Daily}/days"));
        await Rec("days.get.range", Days, user.Get($"{Habits}/{ids.Daily}/days?from={Host.Today.AddDays(-3).Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("days.get.bad-date", Days, user.Get($"{Habits}/{ids.Daily}/days?from=yesterday"));
        await Rec("days.get.future", Days, user.Get($"{Habits}/{ids.Daily}/days?to={Host.Today.AddDays(1).Iso()}"));
        await Rec("days.get.inverted", Days, user.Get($"{Habits}/{ids.Daily}/days?from={Host.Today.Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("days.get.too-long", Days, user.Get($"{Habits}/{ids.Daily}/days?from={Host.Today.AddDays(-400).Iso()}"));
        await Rec("days.get.let-go", Days, user.Get($"{Habits}/{ids.LetGo}/days"));
        await Rec("days.get.not-found", Days, user.Get($"{Habits}/9999/days"));

        await Rec("day.put.completed", Day, user.Put(DayUrl(ids.Daily, 1), new { completed = true }));
        await Rec("day.put.not-completed", Day, user.Put(DayUrl(ids.Daily, 2), new { completed = false }));
        await Rec("day.put.count", Day, user.Put(DayUrl(ids.Multi, 1), new { count = 1 }));
        await Rec("day.put.count-capped", Day, user.Put(DayUrl(ids.Multi, 2), new { count = 9 }));
        await Rec("day.put.weekly", Day, user.Put(DayUrl(ids.Weekly, 0), new { completed = true }));
        await Rec("day.put.neither", Day, user.Put(DayUrl(ids.Daily, 1), new { }));
        await Rec("day.put.both", Day, user.Put(DayUrl(ids.Daily, 1), new { completed = true, count = 1 }));
        await Rec("day.put.negative-count", Day, user.Put(DayUrl(ids.Multi, 1), new { count = -1 }));
        await Rec("day.put.bad-date", Day, user.Put($"{Habits}/{ids.Daily}/days/31-10-2026", new { completed = true }));
        await Rec("day.put.before-start", Day, user.Put(DayUrl(ids.Daily, 30), new { completed = true }));
        await Rec("day.put.in-future", Day, user.Put($"{Habits}/{ids.Daily}/days/{Host.Today.AddDays(1).Iso()}", new { completed = true }));
        await Rec("day.put.let-go", Day, user.Put(DayUrl(ids.LetGo, 1), new { completed = true }));
        await Rec("day.put.not-found", Day, user.Put(DayUrl(9999, 1), new { completed = true }));
        await Rec("day.put.count.not-found", Day, user.Put(DayUrl(9999, 1), new { count = 1 }));

        await Rec("days.get.after", Days, user.Get($"{Habits}/{ids.Multi}/days"));
        await Rec("get.build.after", One, user.Get($"{Habits}/{ids.Daily}"));
        await Rec("day.delete.ok", Day, user.Delete(DayUrl(ids.Daily, 2)));
        await Rec("day.delete.bad-date", Day, user.Delete($"{Habits}/{ids.Daily}/days/never"));
        await Rec("day.delete.before-start", Day, user.Delete(DayUrl(ids.Daily, 30)));
        await Rec("day.delete.in-future", Day, user.Delete($"{Habits}/{ids.Daily}/days/{Host.Today.AddDays(1).Iso()}"));
        await Rec("day.delete.not-found", Day, user.Delete(DayUrl(9999, 1)));

        await Rec("low-day.protects-today", "/api/low-days/{date}", user.Put($"/api/low-days/{Host.Today.Iso()}"));
        await Rec("get.build.low-day", One, user.Get($"{Habits}/{ids.Weekly}"));
    }

    private async Task SlipsOfLetGo(ApiClient user, Created ids)
    {
        await Rec("slips.get.empty", Slips, user.Get($"{Habits}/{ids.LetGo}/slips"));
        await Rec("slip.post.ok", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.AddDays(-1).Iso(), reason = "tired" }));
        await Rec("slip.post.again", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.AddDays(-1).Iso(), reason = "stressed" }));
        await Rec("slip.post.without-reason", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.Iso() }));
        await Rec("slips.get.ok", Slips, user.Get($"{Habits}/{ids.LetGo}/slips"));
        await Rec("slips.get.range", Slips, user.Get($"{Habits}/{ids.LetGo}/slips?from={Host.Today.AddDays(-1).Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("slips.get.bad-date", Slips, user.Get($"{Habits}/{ids.LetGo}/slips?to=soon"));
        await Rec("slips.get.not-found", Slips, user.Get($"{Habits}/9999/slips"));
        await Rec("get.let-go.after", One, user.Get($"{Habits}/{ids.LetGo}"));
        await Rec("slip.post.invalid", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = "today", reason = "boredom" }));
        await Rec("slip.post.in-future", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.AddDays(1).Iso() }));
        await Rec("slip.post.before-start", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.AddDays(-30).Iso() }));
        await Rec("slip.post.build-habit", Slips, user.Post($"{Habits}/{ids.Daily}/slips", new { date = Host.Today.Iso() }));
        await Rec("slip.post.not-found", Slips, user.Post($"{Habits}/9999/slips", new { date = Host.Today.Iso() }));
        await Rec("slip.delete.ok", Slip, user.Delete(SlipUrl(ids.LetGo, 1)));
        await Rec("slip.delete.nothing-there", Slip, user.Delete(SlipUrl(ids.LetGo, 2)));
        await Rec("slip.delete.bad-date", Slip, user.Delete($"{Habits}/{ids.LetGo}/slips/never"));
        await Rec("slip.delete.build-habit", Slip, user.Delete(SlipUrl(ids.Daily, 1)));
        await Rec("slip.delete.not-found", Slip, user.Delete(SlipUrl(9999, 1)));
    }

    private async Task MadeItOfLetGo(ApiClient user, Created ids)
    {
        await Rec("made-it.get.empty", MadeItDays, user.Get($"{Habits}/{ids.LetGo}/made-it"));
        await Rec("made-it.put.ok", MadeIt, user.Put(MadeItUrl(ids.LetGo, 2)));
        await Rec("made-it.put.again", MadeIt, user.Put(MadeItUrl(ids.LetGo, 2)));
        await Rec("made-it.get.ok", MadeItDays, user.Get($"{Habits}/{ids.LetGo}/made-it"));
        await Rec("made-it.get.range", MadeItDays, user.Get($"{Habits}/{ids.LetGo}/made-it?from={Host.Today.AddDays(-1).Iso()}"));
        await Rec("made-it.get.bad-date", MadeItDays, user.Get($"{Habits}/{ids.LetGo}/made-it?to=soon"));
        await Rec("made-it.get.build-habit", MadeItDays, user.Get($"{Habits}/{ids.Daily}/made-it"));
        await Rec("made-it.get.not-found", MadeItDays, user.Get($"{Habits}/9999/made-it"));
        await Rec("made-it.put.slipped", MadeIt, user.Put(MadeItUrl(ids.LetGo, 0)));
        await Rec("made-it.put.bad-date", MadeIt, user.Put($"{Habits}/{ids.LetGo}/made-it/today"));
        await Rec("made-it.put.in-future", MadeIt, user.Put(MadeItUrl(ids.LetGo, -1)));
        await Rec("made-it.put.before-start", MadeIt, user.Put(MadeItUrl(ids.LetGo, 30)));
        await Rec("made-it.put.build-habit", MadeIt, user.Put(MadeItUrl(ids.Daily, 1)));
        await Rec("made-it.put.not-found", MadeIt, user.Put(MadeItUrl(9999, 1)));
        await user.Put(MadeItUrl(ids.LetGo, 3));
        await Rec("made-it.cleared-by-slip", Slips, user.Post($"{Habits}/{ids.LetGo}/slips", new { date = Host.Today.AddDays(-3).Iso() }));
        await Rec("made-it.get.after-slip", MadeItDays, user.Get($"{Habits}/{ids.LetGo}/made-it"));
        await user.Delete(SlipUrl(ids.LetGo, 3));
        await Rec("made-it.delete.ok", MadeIt, user.Delete(MadeItUrl(ids.LetGo, 2)));
        await Rec("made-it.delete.nothing-there", MadeIt, user.Delete(MadeItUrl(ids.LetGo, 2)));
        await Rec("made-it.delete.bad-date", MadeIt, user.Delete($"{Habits}/{ids.LetGo}/made-it/never"));
        await Rec("made-it.delete.build-habit", MadeIt, user.Delete(MadeItUrl(ids.Daily, 1)));
        await Rec("made-it.delete.not-found", MadeIt, user.Delete(MadeItUrl(9999, 1)));
    }

    private async Task Pausing(ApiClient user, Created ids)
    {
        await Rec("pause.until", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Daily}/pause", new { until = Host.Today.AddDays(3).Iso() }));
        await Rec("pause.indefinite", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Weekly}/pause", new { }));
        await Rec("pause.let-go", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.LetGo}/pause", new { until = Host.Today.AddDays(1).Iso() }));
        await Rec("made-it.put.paused", MadeIt, user.Put(MadeItUrl(ids.LetGo, 0)));
        await Rec("pause.no-body", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Daily}/pause"));
        await Rec("pause.bad-date", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Daily}/pause", new { until = "later" }));
        await Rec("pause.in-the-past", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Daily}/pause", new { until = Host.Today.AddDays(-1).Iso() }));
        await Rec("pause.not-found", "/api/habits/{id:int}/pause", user.Post($"{Habits}/9999/pause", new { }));
        await Rec("days.get.paused", Days, user.Get($"{Habits}/{ids.Daily}/days?from={Host.Today.AddDays(-1).Iso()}"));
        await Rec("days.get.off-days", Days, user.Get($"{Habits}/{ids.Weekdays}/days"));
        await Rec("pause.resume", "/api/habits/{id:int}/pause", user.Post($"{Habits}/{ids.Weekly}/pause", new { until = Host.Today.Iso() }));
    }

    private async Task Consent()
    {
        var withoutConsent = await Host.SignedInAsync(consent: false);
        await Rec("create.consent-required", Habits, withoutConsent.Post(Habits, new { name = "Water", frequency = "daily" }));

        var withdrawn = await Host.SignedInAsync();
        var id = (await withdrawn.CreateHabitAsync("Water")).GetProperty("id").GetInt32();
        var letGo = await IdOf(await withdrawn.Post(Habits, new { name = "Coffee", kind = "letGo", frequency = "daily" }));
        Host.Time.Advance(TimeSpan.FromDays(1));
        await Rec("consent.get.ok", One, withdrawn.Get($"{Habits}/{id}"));
        await withdrawn.Post("/api/me/consent/withdraw");
        await Rec("patch.consent-required", One, withdrawn.Patch($"{Habits}/{id}", new { name = "Drink water" }));
        await Rec("day.put.consent-required", Day, withdrawn.Put(DayUrl(id, 0), new { completed = true }));
        await Rec("slip.post.consent-required", Slips, withdrawn.Post($"{Habits}/{letGo}/slips", new { date = Host.Today.Iso() }));
        await Rec("made-it.put.consent-required", MadeIt, withdrawn.Put(MadeItUrl(letGo, 0)));
        await Rec("made-it.delete.without-consent", MadeIt, withdrawn.Delete(MadeItUrl(letGo, 0)));
    }

    private async Task Limit()
    {
        var user = await Host.SignedInAsync();
        for (var i = 0; i < 25; i++) await user.CreateHabitAsync();
        await Rec("create.habit-limit", Habits, user.Post(Habits, new { name = "One too many", frequency = "daily" }));
    }

    private async Task Deleting(ApiClient user, Created ids)
    {
        await Rec("delete.ok", One, user.Delete($"{Habits}/{ids.Multi}"));
        await Rec("delete.again", One, user.Delete($"{Habits}/{ids.Multi}"));
        await Rec("delete.unauthenticated", One, Host.NewClient().Delete($"{Habits}/{ids.Daily}"));
        await Rec("list.after-delete", Habits, user.Get(Habits));
    }
}
