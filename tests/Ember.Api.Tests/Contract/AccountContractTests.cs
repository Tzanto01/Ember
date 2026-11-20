using System.Net;
using Ember.Api.TestHost;
using Ember.Core.Models;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests.Contract;

/// <summary>The account itself: settings, consent, export, import, two-factor sign-in, deletion and merging a bot-only account.</summary>
[Collection("Postgres")]
public class AccountContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "account")
{
    private const string Wrong = "wrong-long-passphrase";

    private sealed record CodeLogin(string Email, string Password, string? TwoFactorCode = null, string? TwoFactorRecoveryCode = null);

    private sealed record EnableBody(string? Code, string? Password);

    private sealed record DisableBody(string? Password);

    private sealed record DeleteBody(string? Password, string? Confirm);

    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    [DockerFact]
    public async Task Account_exchanges_match_the_golden()
    {
        await SettingsScenario();
        await Consent();
        await Export();
        await Import();
        await TwoFactor();
        await Merge();
        await Deletion();
        Verify();
    }

    private async Task SettingsScenario()
    {
        var user = await Host.SignedInAsync();
        var habit = (await user.CreateHabitAsync()).GetProperty("id").GetInt32();
        await Rec("me.get.ok", "/api/me", user.Get("/api/me"));
        await Rec("me.patch.ok", "/api/me", user.Patch("/api/me", new
        {
            timezone = "Europe/Berlin",
            graceDaysPerWeek = 2,
            theme = "tidepool",
            secondReminderMinutes = 30,
            showStreaks = false,
            language = "de",
            calmMode = true,
            offerHabitNote = false,
        }));
        await Rec("me.patch.invalid", "/api/me", user.Patch("/api/me", new { timezone = "Mars/Base", graceDaysPerWeek = 9, theme = "neon", secondReminderMinutes = 7, language = "fr" }));
        await Rec("me.patch.wrong-type", "/api/me", user.Patch("/api/me", new { graceDaysPerWeek = "many" }));
        await Rec("me.patch.not-an-object", "/api/me", user.Patch("/api/me", new[] { 1 }));
        await Rec("today-order.ok", "/api/me/today-order", user.Put("/api/me/today-order", new { order = new[] { $"h:{habit}" } }));
        await Rec("today-order.clear.ok", "/api/me/today-order", user.Put("/api/me/today-order", new { order = (string[]?)null }));
        await Rec("today-order.too-many", "/api/me/today-order", user.Put("/api/me/today-order", new { order = Enumerable.Range(0, 501).Select(i => $"habit:{i}") }));
        await Rec("tips.ok", "/api/me/tips/{id}", user.Put("/api/me/tips/routines"));
        await Rec("tips.unknown", "/api/me/tips/{id}", user.Put("/api/me/tips/nope"));
        await Rec("me.get.tips-and-order", "/api/me", user.Get("/api/me"));
        await Rec("dm-reminders.resume.no-content", "/api/me/dm-reminders/resume", user.Post("/api/me/dm-reminders/resume"));

        var bearer = await Host.BearerAsync();
        await Rec("widget-key.bearer.ok", "/api/me/widget-key", bearer.Post("/api/me/widget-key"));
        await Rec("widget-key.cookie.not-found", "/api/me/widget-key", user.Post("/api/me/widget-key"));
    }

    private async Task Consent()
    {
        var user = await Host.SignedInAsync(consent: false);
        await Rec("me.get.no-consent", "/api/me", user.Get("/api/me"));
        await Rec("consent.age-not-confirmed", "/api/me/consent", user.Post("/api/me/consent", new { policyVersion = HabitService.CurrentConsentPolicyVersion, ageConfirmed = false }));
        await Rec("consent.old-policy", "/api/me/consent", user.Post("/api/me/consent", new { policyVersion = "2000-01-01", ageConfirmed = true }));
        await Rec("consent.habits.consent-required", "/api/habits", user.Post("/api/habits", new { name = "Water", frequency = "daily" }));
        await Rec("consent.decline.no-content", "/api/me/consent/decline", user.Post("/api/me/consent/decline"));
        await Rec("me.get.declined", "/api/me", user.Get("/api/me"));
        await Rec("consent.give.no-content", "/api/me/consent", user.Post("/api/me/consent", new { policyVersion = HabitService.CurrentConsentPolicyVersion, ageConfirmed = true }));

        await user.CreateHabitAsync("Water");
        await Rec("consent.withdraw.no-content", "/api/me/consent/withdraw", user.Post("/api/me/consent/withdraw"));
        await Rec("me.get.consent-withdrawn", "/api/me", user.Get("/api/me"));
    }

    private async Task Export()
    {
        var user = await Host.SignedInAsync();
        await Rec("export.json.empty", "/api/me/export", user.Get("/api/me/export"));
        await Rec("export.bad-format", "/api/me/export", user.Get("/api/me/export?format=xml"));
    }

    private static object ImportDoc() => new
    {
        format_version = 2,
        settings = new
        {
            grace_days_per_week = 2,
            theme = "tidepool",
            second_reminder_minutes = 30,
            show_streaks = false,
            language = "de",
            calm_mode = true,
            tips_seen = new[] { "routines" },
            offer_habit_note = false,
        },
        routines = new[] { new { id = 1, name = "Morning", reminder_time = "08:00" } },
        habits = new object[]
        {
            new { id = 1, name = "Water", frequency = "daily", times_per_day = 2, created_at = "2026-10-20T08:00:00Z", reminder_times = new[] { "09:00", "18:00" }, routine_id = 1, routine_position = 0 },
            new { id = 2, name = "Gym", frequency = "weekdays", days = new[] { "mon", "wed" }, created_at = "2026-10-20T08:00:00Z", paused_from = "2026-10-25", paused_until = "2026-10-28" },
            new { id = 3, name = "Read", frequency = "weekly", weekly_target = 3, created_at = "2026-10-20T08:00:00Z" },
            new { id = 4, name = "No sugar", frequency = "daily", kind = "let_go", created_at = "2026-10-20T08:00:00Z" },
        },
        logs = new[]
        {
            new { habit_id = 1, date = "2026-10-21", completed = true, count = 2 },
            new { habit_id = 3, date = "2026-10-22", completed = true, count = 1 },
        },
        slips = new[] { new { habit_id = 4, date = "2026-10-22", count = 2, reasons = new[] { "tired", "other" } } },
        day_marks = new[] { new { date = "2026-10-23", kind = "low_day" } },
        moods = new object[] { new { id = 1, label = "Proud", emoji = "\U0001F31F" }, new { id = 2, label = "Old", archived = true } },
        day_notes = new object[] { new { date = "2026-10-24", text = "Good day", mood = "good" }, new { date = "2026-10-25", mood = "custom:1" } },
        habit_notes = new[] { new { habit_id = 1, date = "2026-10-21", text = "Felt easy" } },
    };

    private async Task Import()
    {
        var user = await Host.SignedInAsync();
        const string route = "/api/me/import";
        await Rec("import.ok", route, user.Post(route, ImportDoc()));
        await Rec("import.not-empty", route, user.Post(route, ImportDoc()));
        await Rec("export.json.ok", "/api/me/export", user.Get("/api/me/export"));
        await Rec("export.csv.ok", "/api/me/export", user.Get("/api/me/export?format=csv"));

        var fresh = await Host.SignedInAsync();
        await Rec("import.wrong-version", route, fresh.Post(route, new { format_version = 1 }));
        await Rec("import.wrong-type", route, fresh.Post(route, new[] { 1 }));
        await Rec("import.wrong-field-type", route, fresh.Post(route, new { format_version = "two" }));
        await Rec("import.invalid", route, fresh.Post(route, new
        {
            format_version = 2,
            settings = new { theme = "neon" },
            habits = new object[] { new { id = 1, name = "", frequency = "hourly" } },
            logs = new[] { new { habit_id = 9, date = "2026-10-21", count = 1 } },
            day_marks = new[] { new { date = "2026-10-23", kind = "party" } },
        }));
        await Rec("import.habit-limit", route, fresh.Post(route, new
        {
            format_version = 2,
            habits = Enumerable.Range(1, 26).Select(i => new { id = i, name = $"Habit {i}", frequency = "daily" }),
        }));

        var withoutConsent = await Host.SignedInAsync(consent: false);
        await Rec("import.consent-required", route, withoutConsent.Post(route, new { format_version = 2 }));
    }

    private async Task TwoFactor()
    {
        var user = await Host.SignedInAsync(consent: false);
        await Rec("2fa.get.off", "/api/me/2fa", user.Get("/api/me/2fa"));
        await Rec("2fa.enable.before-setup", "/api/me/2fa/enable", user.Post("/api/me/2fa/enable", new EnableBody("123456", ApiClient.Passphrase)));
        var setup = await Rec("2fa.setup.ok", "/api/me/2fa/setup", user.Post("/api/me/2fa/setup"));
        var key = (await setup.ExpectJson()).GetProperty("sharedKey").GetString()!;
        await Rec("2fa.enable.wrong-password", "/api/me/2fa/enable", user.Post("/api/me/2fa/enable", new EnableBody(Totp.Code(key), Wrong)));
        await Rec("2fa.enable.wrong-code", "/api/me/2fa/enable", user.Post("/api/me/2fa/enable", new EnableBody(Totp.Code(key, 10), ApiClient.Passphrase)));
        await Rec("2fa.enable.ok", "/api/me/2fa/enable", user.Post("/api/me/2fa/enable", new EnableBody(Totp.Code(key), ApiClient.Passphrase)));
        await Rec("2fa.get.on", "/api/me/2fa", user.Get("/api/me/2fa"));
        await Rec("2fa.setup.already-on", "/api/me/2fa/setup", user.Post("/api/me/2fa/setup"));
        await Rec("2fa.enable.already-on", "/api/me/2fa/enable", user.Post("/api/me/2fa/enable", new EnableBody(Totp.Code(key), ApiClient.Passphrase)));
        await Rec("2fa.recovery-codes.wrong-code", "/api/me/2fa/recovery-codes", user.Post("/api/me/2fa/recovery-codes", new { code = "000000" }));
        var renewed = await Rec("2fa.recovery-codes.ok", "/api/me/2fa/recovery-codes", user.Post("/api/me/2fa/recovery-codes", new { code = Totp.Code(key) }));
        var recovery = (await renewed.ExpectJson()).GetProperty("recoveryCodes")[0].GetString()!;

        var login = "/api/auth/login?useCookies=true";
        await Rec("login.2fa.required", "/api/auth/login", Host.NewClient().Post(login, new CodeLogin(user.Email, ApiClient.Passphrase)));
        await Rec("login.2fa.wrong-code", "/api/auth/login", Host.NewClient().Post(login, new CodeLogin(user.Email, ApiClient.Passphrase, "000000")));
        await Rec("login.2fa.code.ok", "/api/auth/login", Host.NewClient().Post(login, new CodeLogin(user.Email, ApiClient.Passphrase, Totp.Code(key))));
        await Rec("login.2fa.bearer.ok", "/api/auth/login", Host.NewClient().Post("/api/auth/login", new CodeLogin(user.Email, ApiClient.Passphrase, Totp.Code(key))));
        await Rec("login.2fa.recovery.ok", "/api/auth/login", Host.NewClient().Post(login, new CodeLogin(user.Email, ApiClient.Passphrase, null, recovery)));

        await Rec("2fa.disable.wrong-password", "/api/me/2fa/disable", user.Post("/api/me/2fa/disable", new DisableBody(Wrong)));
        await Rec("2fa.disable.ok", "/api/me/2fa/disable", user.Post("/api/me/2fa/disable", new DisableBody(ApiClient.Passphrase)));
        await Rec("2fa.disable.already-off", "/api/me/2fa/disable", user.Post("/api/me/2fa/disable", new DisableBody(ApiClient.Passphrase)));
        await Rec("2fa.recovery-codes.off", "/api/me/2fa/recovery-codes", user.Post("/api/me/2fa/recovery-codes", new { code = "000000" }));
    }

    private async Task Merge()
    {
        const string route = "/api/account/merge";
        var user = await Host.SignedInAsync();
        await Rec("merge.get.nothing-pending", route, user.Get(route));
        await Rec("merge.post.nothing-pending", route, user.Post(route));
        await Rec("merge.delete.no-content", route, user.Delete(route));

        var discordId = ApiHost.NewDiscordId();
        var botId = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(discordId));
        await Host.Run(s => s.GiveHealthDataConsentAsync(botId));
        var habit = await Host.Service(s => s.AddHabitAsync(botId, "Stretch", null));
        await Host.Run(s => s.CheckInAsync(botId, habit.Id, Host.Today, true));
        await Host.DiscordAsync(user, "/api/auth/discord/link", discordId);
        await Rec("merge.get.ok", route, user.Get(route));
        await Rec("merge.post.ok", route, user.Post(route));
        await Rec("merge.post.after", route, user.Post(route));

        var cancelling = await Host.SignedInAsync();
        var cancelledBot = ApiHost.NewDiscordId();
        await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(cancelledBot));
        await Host.DiscordAsync(cancelling, "/api/auth/discord/link", cancelledBot);
        await Rec("merge.delete.pending.no-content", route, cancelling.Delete(route));
        await Rec("merge.get.cancelled", route, cancelling.Get(route));

        var withoutConsent = await Host.SignedInAsync(consent: false);
        var otherBot = ApiHost.NewDiscordId();
        await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(otherBot));
        await Host.DiscordAsync(withoutConsent, "/api/auth/discord/link", otherBot);
        await Rec("merge.post.consent-required", route, withoutConsent.Post(route));

        var journalUser = await Host.SignedInAsync();
        await (await journalUser.Put("/api/me/journal-key", new
        {
            passphrase = new { salt = B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
            recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
            password = ApiClient.Passphrase,
        })).Expect(HttpStatusCode.OK);
        var journalBotDiscord = ApiHost.NewDiscordId();
        var journalBot = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(journalBotDiscord));
        await Host.Db(async db =>
        {
            db.JournalKeys.Add(new JournalKey
            {
                UserId = journalBot,
                Version = 1,
                PassphraseSalt = "c2FsdA==",
                PassphraseIterations = 600000,
                PassphraseIv = "aXY=",
                PassphraseWrapped = "d3JhcA==",
                RecoverySalt = "cnNhbHQ=",
                RecoveryIv = "cml2",
                RecoveryWrapped = "cndyYXA=",
            });
            return await db.SaveChangesAsync();
        });
        await Host.DiscordAsync(journalUser, "/api/auth/discord/link", journalBotDiscord);
        await Rec("merge.post.journal-conflict", route, journalUser.Post(route));
    }

    private async Task Deletion()
    {
        var user = await Host.SignedInAsync();
        await Rec("delete.wrong-password", "/api/me/delete", user.Post("/api/me/delete", new DeleteBody(Wrong, null)));
        await Rec("delete.missing-password", "/api/me/delete", user.Post("/api/me/delete", new DeleteBody(null, null)));
        await Rec("delete.ok", "/api/me/delete", user.Post("/api/me/delete", new DeleteBody(ApiClient.Passphrase, null)));
        await Rec("me.get.deleted", "/api/me", user.Get("/api/me"));
    }
}
