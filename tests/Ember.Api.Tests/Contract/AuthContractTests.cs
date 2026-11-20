using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests.Contract;

/// <summary>Sign-up, sign-in, tokens, sessions and the cross-cutting answers (401, antiforgery 403, unknown API paths, health).</summary>
[Collection("Postgres")]
public class AuthContractTests(PostgresFixture postgres) : ContractTestBase(postgres, "auth")
{
    private const string Register = "/api/auth/register";
    private const string Login = "/api/auth/login";
    private const string ConfirmCode = "/api/auth/confirm-email-code";
    private const string ConfirmEmail = "/api/auth/confirmEmail";
    private const string Another = "another-long-passphrase";
    private const string Wrong = "wrong-long-passphrase";

    private sealed record ConfirmBody(string? UserId = null, string? Email = null, string? Code = null, string? SignupKey = null, string? Password = null);

    private sealed record RegisterBody(string Email, string Password, string? Language = null);

    private ApiClient Device(string key)
    {
        var client = Host.NewClient();
        client.Http.DefaultRequestHeaders.Add("X-Ember-Signup-Key", key);
        return client;
    }

    private ApiClient BearerClient(string token)
    {
        var client = Host.NewClient();
        client.Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string NewKey() => WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    private static string WrongCode(string code) => code == "000000" ? "000001" : "000000";

    private async Task<(string Email, string Code, string UserId)> RegisteredAsync(ApiClient client)
    {
        var email = ApiHost.NewEmail();
        await (await client.Post(Register, new Credentials(email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var mail = (await Host.Mail.WaitForAsync(email, 1)).Single();
        var userId = (await Host.Db(db => db.Users.SingleAsync(u => u.NormalizedEmail == email.ToUpperInvariant()))).Id;
        return (email, ApiHost.SignupCode(mail), userId.ToString());
    }

    [DockerFact]
    public async Task Auth_exchanges_match_the_golden()
    {
        await SignUp();
        await SignIn();
        await Sessions();
        await PasswordReset();
        await CrossCutting();
        Verify();
    }

    private async Task SignUp()
    {
        var anonymous = Host.NewClient();
        var email = ApiHost.NewEmail();
        await Rec("register.ok", Register, anonymous.Post(Register, new Credentials(email, ApiClient.Passphrase)));
        await Rec("register.language.ok", Register, anonymous.Post(Register, new RegisterBody(ApiHost.NewEmail(), ApiClient.Passphrase, "de")));
        await Rec("register.invalid", Register, anonymous.Post(Register, new Credentials("not-an-email", "short")));
        await Rec("register.password-too-short", Register, anonymous.Post(Register, new Credentials(ApiHost.NewEmail(), "short")));
        await Rec("register.malformed", Register, anonymous.Post(Register, new { email = ApiHost.NewEmail() }));
        await Rec("register.taken.ok", Register, anonymous.Post(Register, new Credentials(email, ApiClient.Passphrase)));
        await Rec("resend.ok", "/api/auth/resendConfirmationEmail", anonymous.Post("/api/auth/resendConfirmationEmail", new { email }));
        await Rec("resend.unknown.ok", "/api/auth/resendConfirmationEmail", anonymous.Post("/api/auth/resendConfirmationEmail", new { email = ApiHost.NewEmail() }));

        var key = NewKey();
        var sameDevice = Device(key);
        var cookieUser = await RegisteredAsync(sameDevice);
        var cookieUrl = $"{ConfirmCode}?useCookies=true";
        await Rec("confirm-email-code.wrong", ConfirmCode, sameDevice.Post(cookieUrl, new ConfirmBody(UserId: cookieUser.UserId, Code: WrongCode(cookieUser.Code), SignupKey: key)));
        await Rec("confirm-email-code.cookie.ok", ConfirmCode, sameDevice.Post(cookieUrl, new ConfirmBody(UserId: cookieUser.UserId, Code: cookieUser.Code, SignupKey: key)));
        await Rec("confirm-email-code.again", ConfirmCode, sameDevice.Post(cookieUrl, new ConfirmBody(UserId: cookieUser.UserId, Code: cookieUser.Code, SignupKey: key)));

        var bearerUser = await RegisteredAsync(Device(key));
        await Rec("confirm-email-code.bearer.ok", ConfirmCode, Host.NewClient().Post(ConfirmCode, new ConfirmBody(Email: bearerUser.Email, Code: bearerUser.Code, SignupKey: key)));

        var otherDevice = await RegisteredAsync(Host.NewClient());
        await Rec("confirm-email-code.password-required", ConfirmCode, Host.NewClient().Post(cookieUrl, new ConfirmBody(UserId: otherDevice.UserId, Code: otherDevice.Code)));
        await Rec("confirm-email-code.password.ok", ConfirmCode, Host.NewClient().Post(cookieUrl, new ConfirmBody(UserId: otherDevice.UserId, Code: otherDevice.Code, Password: ApiClient.Passphrase)));

        var linkUser = Host.NewClient();
        linkUser.Email = ApiHost.NewEmail();
        await (await linkUser.Post(Register, new Credentials(linkUser.Email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        var (userId, code) = await Host.OldConfirmLinkAsync((await Host.Mail.WaitForAsync(linkUser.Email, 1)).Single());
        await Rec("confirm-email.wrong-code", ConfirmEmail, linkUser.Post(ConfirmEmail, new ConfirmBody(UserId: userId, Code: "AAAA", Password: ApiClient.Passphrase)));
        await Rec("confirm-email.wrong-password", ConfirmEmail, linkUser.Post(ConfirmEmail, new ConfirmBody(UserId: userId, Code: code, Password: Wrong)));
        await Rec("confirm-email.ok", ConfirmEmail, linkUser.Post(ConfirmEmail, new ConfirmBody(UserId: userId, Code: code, Password: ApiClient.Passphrase)));
        await Rec("confirm-email.get.not-found", ConfirmEmail, anonymous.Get($"{ConfirmEmail}?userId={userId}&code={code}"));
    }

    private async Task SignIn()
    {
        var confirmed = await Host.ConfirmedUserAsync();
        var unconfirmedEmail = ApiHost.NewEmail();
        await (await Host.NewClient().Post(Register, new Credentials(unconfirmedEmail, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);

        var cookieLogin = Host.NewClient();
        await Rec("login.cookie.ok", Login, cookieLogin.Post($"{Login}?useCookies=true", new Credentials(confirmed.Email, ApiClient.Passphrase)));
        await Rec("login.session-cookie.ok", Login, Host.NewClient().Post($"{Login}?useSessionCookies=true", new Credentials(confirmed.Email, ApiClient.Passphrase)));
        await Rec("login.bearer.ok", Login, Host.NewClient().Post(Login, new Credentials(confirmed.Email, ApiClient.Passphrase)));
        await Rec("login.wrong-password", Login, Host.NewClient().Post(Login, new Credentials(confirmed.Email, Wrong)));
        await Rec("login.unknown-email", Login, Host.NewClient().Post(Login, new Credentials(ApiHost.NewEmail(), ApiClient.Passphrase)));
        await Rec("login.unconfirmed", Login, Host.NewClient().Post(Login, new Credentials(unconfirmedEmail, ApiClient.Passphrase)));
        await Rec("login.malformed", Login, Host.NewClient().Post(Login, new { email = confirmed.Email }));

        await Rec("antiforgery.ok", "/api/auth/antiforgery", cookieLogin.Get("/api/auth/antiforgery"));
        await Rec("antiforgery.anonymous.ok", "/api/auth/antiforgery", Host.NewClient().Get("/api/auth/antiforgery"));
        await Rec("manage-info.get.not-found", "/api/auth/manage/info", cookieLogin.Get("/api/auth/manage/info"));
        await Rec("manage-info.post.not-found", "/api/auth/manage/info", cookieLogin.Post("/api/auth/manage/info", new { newPassword = Another }));
        await Rec("manage-2fa.not-found", "/api/auth/manage/2fa", cookieLogin.Post("/api/auth/manage/2fa", new { enable = true, forgetMachine = false, resetRecoveryCodes = false, resetSharedKey = true }));
    }

    private async Task Sessions()
    {
        var user = await Host.ConfirmedUserAsync();
        var tokens = await (await user.Post(Login, new Credentials(user.Email, ApiClient.Passphrase))).ExpectJson();
        await Rec("refresh.invalid", "/api/auth/refresh", Host.NewClient().Post("/api/auth/refresh", new { refreshToken = "not-a-token" }));
        var refreshed = await Rec("refresh.ok", "/api/auth/refresh",
            Host.NewClient().Post("/api/auth/refresh", new { refreshToken = tokens.GetProperty("refreshToken").GetString() }));

        var bearer = BearerClient((await refreshed.ExpectJson()).GetProperty("accessToken").GetString()!);
        await Rec("logout.bearer.no-content", "/api/auth/logout", bearer.Post("/api/auth/logout"));
        await Rec("me.bearer-after-logout", "/api/me", bearer.Get("/api/me"));

        var cookie = await Host.SignedInAsync(consent: false);
        await Rec("logout.cookie.no-content", "/api/auth/logout", cookie.Post("/api/auth/logout"));
        await Rec("logout.anonymous.no-content", "/api/auth/logout", Host.NewClient().Post("/api/auth/logout"));

        var everywhere = await Host.SignedInAsync(consent: false);
        await Rec("logout-everywhere.no-content", "/api/auth/logout-everywhere", everywhere.Post("/api/auth/logout-everywhere"));
        await Rec("logout-everywhere.anonymous.no-content", "/api/auth/logout-everywhere", Host.NewClient().Post("/api/auth/logout-everywhere"));
    }

    private async Task PasswordReset()
    {
        var user = await Host.ConfirmedUserAsync();
        await Rec("forgot-password.ok", "/api/auth/forgotPassword", user.Post("/api/auth/forgotPassword", new { email = user.Email }));
        await Rec("forgot-password.unknown.ok", "/api/auth/forgotPassword", user.Post("/api/auth/forgotPassword", new { email = ApiHost.NewEmail() }));
        var resetMail = (await Host.Mail.WaitForAsync(user.Email, 2))[^1];
        var code = QueryHelpers.ParseQuery(new Uri(resetMail.Link()).Query)["code"].ToString();

        await Rec("reset-password.bad-code", "/api/auth/resetPassword", user.Post("/api/auth/resetPassword", new ResetRequest(user.Email, "AAAA", Another)));
        await Rec("reset-password.too-short", "/api/auth/resetPassword", user.Post("/api/auth/resetPassword", new ResetRequest(user.Email, code, "123456789")));
        await Rec("reset-password.ok", "/api/auth/resetPassword", user.Post("/api/auth/resetPassword", new ResetRequest(user.Email, code, Another)));
    }

    private async Task CrossCutting()
    {
        var anonymous = Host.NewClient();
        await Rec("unauthenticated.get", "/api/me", anonymous.Get("/api/me"));
        await Rec("unauthenticated.post", "/api/habits", anonymous.Post("/api/habits", new { name = "Water" }));
        await Rec("unauthenticated.bad-bearer", "/api/me", BearerClient("not-a-token").Get("/api/me"));

        var user = await Host.SignedInAsync();
        var withoutToken = Host.NewClient();
        await withoutToken.Post($"{Login}?useCookies=true", new Credentials(user.Email, ApiClient.Passphrase));
        await Rec("antiforgery.post", "/api/habits", withoutToken.Post("/api/habits", new { name = "Water", frequency = "daily" }));
        await Rec("antiforgery.put", "/api/me/today-order", withoutToken.Put("/api/me/today-order", new { order = Array.Empty<string>() }));
        await Rec("antiforgery.patch", "/api/me", withoutToken.Patch("/api/me", new { theme = "dark" }));
        await Rec("antiforgery.delete", "/api/habits/{id:int}", withoutToken.Delete("/api/habits/1"));
        withoutToken.Csrf = "not-the-token";
        await Rec("antiforgery.wrong-token", "/api/habits", withoutToken.Post("/api/habits", new { name = "Water", frequency = "daily" }));
        await Rec("antiforgery.get.ok", "/api/me", withoutToken.Get("/api/me"));
        await Rec("antiforgery.login.exempt", Login, withoutToken.Post($"{Login}?useCookies=true", new Credentials(user.Email, ApiClient.Passphrase)));

        var tokens = await (await Host.NewClient().Post(Login, new Credentials(user.Email, ApiClient.Passphrase))).ExpectJson();
        var bearer = BearerClient(tokens.GetProperty("accessToken").GetString()!);
        await Rec("antiforgery.bearer.exempt", "/api/habits", bearer.Post("/api/habits", new { name = "Water", frequency = "daily" }));

        await Rec("fallback.api.not-found", "/api/{**rest}", user.Get("/api/does-not-exist"));
        await Rec("fallback.api.post.not-found", "/api/{**rest}", anonymous.Post("/api/does-not-exist", new { }));
        await Rec("fallback.habits.not-an-id", "/api/{**rest}", user.Get("/api/habits/abc"));
        await Rec("health.ok", "/health", anonymous.Get("/health"));
    }
}
