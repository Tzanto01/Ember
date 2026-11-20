using System.Net;

namespace Ember.Api.Tests.Contract;

/// <summary>Routines, reminder destinations (browsers, phones, Discord) and low days.</summary>
[Collection("Postgres")]
public class RoutinesContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "routines")
{
    private const string Routines = "/api/routines";
    private const string Routine = "/api/routines/{id:int}";
    private const string Destinations = "/api/me/reminder-destinations";
    private const string Destination = "/api/me/reminder-destinations/{id}";
    private const string Phones = "/api/me/phones";
    private const string LowDays = "/api/low-days";
    private const string LowDay = "/api/low-days/{date}";

    private sealed record PushBody(string Endpoint, PushKeys Keys);

    private sealed record PushKeys(string P256dh, string Auth);

    private static async Task<int> IdOf(HttpResponseMessage response)
        => (await response.ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();

    [DockerFact]
    public async Task Routine_exchanges_match_the_golden()
    {
        var user = await Host.SignedInAsync();
        var habit = (await user.CreateHabitAsync("Water")).GetProperty("id").GetInt32();
        var letGo = (await (await user.Post("/api/habits", new { name = "Coffee", kind = "letGo", frequency = "daily" })).ExpectJson(HttpStatusCode.Created)).GetProperty("id").GetInt32();
        var destination = await DestinationScenario(user);
        await RoutineScenario(user, habit, letGo, destination);
        await LowDaysScenario(user);
        Verify();
    }

    private async Task<string> DestinationScenario(ApiClient user)
    {
        await Rec("destinations.get.none", Destinations, user.Get(Destinations));
        await Rec("destinations.unauthenticated", Destinations, Host.NewClient().Get(Destinations));
        await (await user.Post("/api/me/push-subscriptions", new PushBody("https://fcm.googleapis.com/fcm/send/contract", new PushKeys("BPcontractp256dhkey", "contractauth")))).Expect(HttpStatusCode.NoContent);
        await Rec("phones.ok", Phones, user.Post(Phones, new { installId = "contract-install-id-0001", label = "Pixel 8" }));
        await Rec("phones.no-label", Phones, user.Post(Phones, new { installId = "contract-install-id-0002" }));
        await Rec("phones.invalid", Phones, user.Post(Phones, new { installId = "short", label = new string('x', 61) }));
        await Rec("phones.label-characters", Phones, user.Post(Phones, new { installId = "contract-install-id-0003", label = "Pixel‮" }));
        var list = await Rec("destinations.get.ok", Destinations, user.Get(Destinations));
        var browser = (await list.ExpectJson()).EnumerateArray().First(d => d.GetProperty("kind").GetString() == "browser").GetProperty("id").GetString()!;
        var phone = (await list.ExpectJson()).EnumerateArray().First(d => d.GetProperty("kind").GetString() == "phone").GetProperty("id").GetString()!;
        await Rec("destination.patch.disable", Destination, user.Patch($"{Destinations}/{browser}", new { enabled = false }));
        await Rec("destination.patch.enable", Destination, user.Patch($"{Destinations}/{phone}", new { enabled = true }));
        await Rec("destination.patch.missing-enabled", Destination, user.Patch($"{Destinations}/{browser}", new { }));
        await Rec("destination.patch.not-found", Destination, user.Patch($"{Destinations}/phone-9999", new { enabled = true }));
        await Rec("destination.patch.discord-not-linked", Destination, user.Patch($"{Destinations}/discord", new { enabled = true }));

        var discord = Host.NewClient();
        await Host.DiscordAsync(discord, "/api/auth/discord/login", ApiHost.NewDiscordId());
        await discord.RefreshCsrfAsync();
        await Rec("destinations.get.discord", Destinations, discord.Get(Destinations));
        await Rec("destination.patch.discord", Destination, discord.Patch($"{Destinations}/discord", new { enabled = true }));
        return browser;
    }

    private async Task RoutineScenario(ApiClient user, int habit, int letGo, string destination)
    {
        await Rec("habit.with-exclusions", "/api/habits", user.Post("/api/habits", new { name = "Quiet", frequency = "weekly", weeklyTarget = 2, reminderTime = "08:00", reminderExclusions = new[] { destination } }));
        await Rec("list.empty", Routines, user.Get(Routines));
        await Rec("list.unauthenticated", Routines, Host.NewClient().Get(Routines));
        var morning = await IdOf(await Rec("create.ok", Routines, user.Post(Routines, new { name = "Morning", reminderTime = "07:30", habitIds = new[] { habit }, reminderExclusions = new[] { destination } })));
        var empty = await IdOf(await Rec("create.minimal", Routines, user.Post(Routines, new { name = "Evening" })));
        await Rec("create.invalid", Routines, user.Post(Routines, new { name = "", reminderTime = "7:30pm", reminderExclusions = new[] { "browser-9999" } }));
        await Rec("create.name-too-long", Routines, user.Post(Routines, new { name = new string('x', 51) }));
        await Rec("create.duplicate-habits", Routines, user.Post(Routines, new { name = "Dup", habitIds = new[] { habit, habit } }));
        await Rec("create.let-go-habit", Routines, user.Post(Routines, new { name = "Let go", habitIds = new[] { letGo } }));
        await Rec("create.unknown-habit", Routines, user.Post(Routines, new { name = "Ghost", habitIds = new[] { 9999 } }));
        await Rec("list.ok", Routines, user.Get(Routines));
        await Rec("habit.in-routine", "/api/habits/{id:int}", user.Get($"/api/habits/{habit}"));

        await Rec("patch.ok", Routine, user.Patch($"{Routines}/{morning}", new { name = "Early", reminderTime = "06:45" }));
        await Rec("patch.clear-reminder", Routine, user.Patch($"{Routines}/{morning}", new { clearReminder = true }));
        await Rec("patch.habits", Routine, user.Patch($"{Routines}/{empty}", new { habitIds = new[] { habit } }));
        await Rec("patch.conflict", Routine, user.Patch($"{Routines}/{morning}", new { reminderTime = "07:00", clearReminder = true }));
        await Rec("patch.invalid", Routine, user.Patch($"{Routines}/{morning}", new { name = "", habitIds = new[] { letGo } }));
        await Rec("patch.not-found", Routine, user.Patch($"{Routines}/9999", new { name = "Nope" }));

        await Rec("order.ok", Routines + "/order", user.Put($"{Routines}/order", new { ids = new[] { empty, morning } }));
        await Rec("order.mismatch", Routines + "/order", user.Put($"{Routines}/order", new { ids = new[] { empty } }));
        await Rec("order.missing-ids", Routines + "/order", user.Put($"{Routines}/order", new { }));
        await Rec("order.duplicates", Routines + "/order", user.Put($"{Routines}/order", new { ids = new[] { empty, empty } }));

        await Rec("delete.ok", Routine, user.Delete($"{Routines}/{empty}"));
        await Rec("delete.again", Routine, user.Delete($"{Routines}/{empty}"));
        for (var i = 0; i < 9; i++) await user.Post(Routines, new { name = $"Filler {i}" });
        await Rec("create.limit", Routines, user.Post(Routines, new { name = "One too many" }));
    }

    private async Task LowDaysScenario(ApiClient user)
    {
        Host.Time.Advance(TimeSpan.FromDays(5));
        await Rec("low-days.get.empty", LowDays, user.Get(LowDays));
        await Rec("low-day.put.ok", LowDay, user.Put($"{LowDays}/{Host.Today.AddDays(-2).Iso()}"));
        await Rec("low-day.put.again", LowDay, user.Put($"{LowDays}/{Host.Today.AddDays(-2).Iso()}"));
        await Rec("low-days.get.ok", LowDays, user.Get(LowDays));
        await Rec("low-days.get.range", LowDays, user.Get($"{LowDays}?from={Host.Today.AddDays(-1).Iso()}&to={Host.Today.Iso()}"));
        await Rec("low-days.get.bad-date", LowDays, user.Get($"{LowDays}?from=last-week"));
        await Rec("low-days.get.inverted", LowDays, user.Get($"{LowDays}?from={Host.Today.Iso()}&to={Host.Today.AddDays(-1).Iso()}"));
        await Rec("low-day.put.bad-date", LowDay, user.Put($"{LowDays}/yesterday"));
        await Rec("low-day.put.in-future", LowDay, user.Put($"{LowDays}/{Host.Today.AddDays(1).Iso()}"));
        await Rec("low-day.put.before-start", LowDay, user.Put($"{LowDays}/{Host.Today.AddDays(-60).Iso()}"));
        await Rec("low-day.delete.ok", LowDay, user.Delete($"{LowDays}/{Host.Today.AddDays(-2).Iso()}"));
        await Rec("low-day.delete.nothing-there", LowDay, user.Delete($"{LowDays}/{Host.Today.AddDays(-2).Iso()}"));
        await Rec("low-day.delete.bad-date", LowDay, user.Delete($"{LowDays}/yesterday"));
        await Rec("low-day.unauthenticated", LowDay, Host.NewClient().Put($"{LowDays}/{Host.Today.Iso()}"));

        var withoutConsent = await Host.SignedInAsync(consent: false);
        await Rec("low-day.put.consent-required", LowDay, withoutConsent.Put($"{LowDays}/{Host.Today.Iso()}"));
        await Rec("low-days.get.no-consent", LowDays, withoutConsent.Get(LowDays));
    }
}
