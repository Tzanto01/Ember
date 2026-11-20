using System.Net;
using System.Security.Cryptography;
using System.Text;
using Ember.Api.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Ember.Api.Tests.Contract;

/// <summary>Discord sign-in (web and native), the second step for two-factor accounts, linking, unlinking and deleting a Discord-only account.</summary>
[ContractArea("discord")]
public class DiscordContractTests(PostgresFixture postgres) : ContractTestBase(postgres)
{
    private const string Login = "/api/auth/discord/login";
    private const string Link = "/api/auth/discord/link";
    private const string LinkStart = "/api/auth/discord/link/start";
    private const string NativeToken = "/api/auth/discord/native/token";
    private const string Wrong = "wrong-long-passphrase";

    private sealed record NativeTokenBody(string? Code, string? Verifier, string? TwoFactorCode = null);

    private sealed record PasswordBody(string? Password);

    private sealed record DeleteBody(string? Password, string? Confirm);

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (verifier, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }

    private static string PathOf(string url) => url.Contains('?') ? url[..url.IndexOf('?')] : url;

    /// <summary>
    /// Drives a Discord flow like <see cref="ApiHost.DiscordAsync"/> and records each redirect: the challenge at
    /// <paramref name="startUrl"/>, the callback and the API's own follow-ups. Returns the final redirect target.
    /// </summary>
    private async Task<string> Flow(ApiClient client, string name, string startUrl, ulong? discordId = null, bool deny = false)
    {
        if (discordId is { } id) Host.Services.GetRequiredService<FakeDiscord>().SetNext(id);

        var challenge = await Rec($"{name}.challenge", PathOf(startUrl), client.Get(startUrl));
        var authorize = challenge.Headers.Location!.OriginalString;
        if (!authorize.Contains("state=")) return authorize;

        var state = Uri.EscapeDataString(QueryHelpers.ParseQuery(authorize[(authorize.IndexOf('?') + 1)..])["state"].ToString());
        var callback = await Rec($"{name}.callback", "/api/auth/discord/callback",
            client.Get($"/api/auth/discord/callback?{(deny ? "error=access_denied" : "code=fake")}&state={state}"));
        var next = callback.Headers.Location!.OriginalString;
        for (var step = 1; next.StartsWith("/api/auth/discord/", StringComparison.Ordinal); step++)
            next = (await Rec($"{name}.follow-{step}", PathOf(next), client.Get(next))).Headers.Location!.OriginalString;
        return next;
    }

    [DockerFact]
    public async Task Discord_exchanges_match_the_golden()
    {
        await WebSignIn();
        await NativeSignIn();
        await TwoFactorSignIn();
        await LinkAndUnlink();
        await DiscordOnlyDeletion();
        Verify();
    }

    private async Task WebSignIn()
    {
        var browser = Host.NewClient();
        var discordId = ApiHost.NewDiscordId();
        await Flow(browser, "login.new", $"{Login}?returnUrl=/habits&language=de", discordId);
        await Rec("login.new.me", "/api/me", browser.Get("/api/me"));
        await Flow(Host.NewClient(), "login.existing", Login, discordId);
        await Flow(Host.NewClient(), "login.unsafe-return", $"{Login}?returnUrl=//evil.test", ApiHost.NewDiscordId());
        await Flow(Host.NewClient(), "login.denied", Login, deny: true);
        await Flow(Host.NewClient(), "login.failed", Login);
        await Rec("callback.bad-state", "/api/auth/discord/callback", Host.NewClient().Get("/api/auth/discord/callback?code=fake&state=nope"));
        await Rec("login-complete.direct", "/api/auth/discord/login/complete", Host.NewClient().Get("/api/auth/discord/login/complete"));
        await Rec("native-complete.direct", "/api/auth/discord/native/complete", Host.NewClient().Get("/api/auth/discord/native/complete"));
        await Rec("link-complete.direct", "/api/auth/discord/link/complete", Host.NewClient().Get("/api/auth/discord/link/complete"));
    }

    private async Task<(string Verifier, string Code)> NativeCode(string name, ulong? discordId = null)
    {
        var (verifier, challenge) = Pkce();
        var target = await Flow(Host.NewClient(), name, $"{Login}?client=native&challenge={challenge}", discordId ?? ApiHost.NewDiscordId());
        return (verifier, QueryHelpers.ParseQuery(new Uri(target).Query)["code"].ToString());
    }

    private async Task NativeSignIn()
    {
        await Rec("native.challenge.too-short", Login, Host.NewClient().Get($"{Login}?client=native&challenge=short"));
        var (verifier, code) = await NativeCode("native");
        await Rec("native-token.ok", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(code, verifier)));
        await Rec("native-token.used", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(code, verifier)));
        await Rec("native-token.missing-code", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(null, verifier)));

        var (otherVerifier, otherCode) = await NativeCode("native.second");
        await Rec("native-token.wrong-verifier", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(otherCode, otherVerifier + "x")));
    }

    private async Task TwoFactorSignIn()
    {
        var owner = Host.NewClient();
        var discordId = ApiHost.NewDiscordId();
        await Host.DiscordAsync(owner, Login, discordId);
        await owner.RefreshCsrfAsync();
        var key = (await (await owner.Post("/api/me/2fa/setup")).ExpectJson()).GetProperty("sharedKey").GetString()!;
        await (await owner.Post("/api/me/2fa/enable", new { code = Totp.Code(key) })).Expect(HttpStatusCode.OK);

        var web = Host.NewClient();
        await Rec("2fa.no-pending", "/api/auth/discord/2fa", web.Post("/api/auth/discord/2fa", new { code = Totp.Code(key) }));
        await Flow(web, "2fa.login", $"{Login}?returnUrl=/habits", discordId);
        await Rec("2fa.wrong-code", "/api/auth/discord/2fa", web.Post("/api/auth/discord/2fa", new { code = Totp.Code(key, 10) }));
        await Rec("2fa.ok", "/api/auth/discord/2fa", web.Post("/api/auth/discord/2fa", new { code = Totp.Code(key) }));

        var (verifier, code) = await NativeCode("2fa.native", discordId);
        await Rec("2fa.native-token.required", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(code, verifier)));
        await Rec("2fa.native-token.ok", NativeToken, Host.NewClient().Post(NativeToken, new NativeTokenBody(code, verifier, Totp.Code(key))));
    }

    private async Task LinkAndUnlink()
    {
        var user = await Host.SignedInAsync();
        var anonymous = Host.NewClient();
        await Rec("link.unauthenticated", Link, anonymous.Get(Link));
        await Rec("link-start.unauthenticated", LinkStart, anonymous.Post(LinkStart, new PasswordBody(ApiClient.Passphrase)));
        await Rec("link.password-needed", Link, user.Get(Link));
        await Rec("link-start.wrong-password", LinkStart, user.Post(LinkStart, new PasswordBody(Wrong)));
        await Rec("link-start.ok", LinkStart, user.Post(LinkStart, new PasswordBody(ApiClient.Passphrase)));

        var discordId = ApiHost.NewDiscordId();
        await Flow(user, "link", Link, discordId);
        await Rec("link.me", "/api/me", user.Get("/api/me"));

        var other = await Host.SignedInAsync();
        await (await other.Post(LinkStart, new PasswordBody(ApiClient.Passphrase))).Expect(HttpStatusCode.NoContent);
        await Flow(other, "link.taken", Link, discordId);

        await Rec("unlink.ok", "/api/me/discord", user.Delete("/api/me/discord"));
        await Rec("unlink.not-linked", "/api/me/discord", user.Delete("/api/me/discord"));
        await Rec("unlink.unauthenticated", "/api/me/discord", anonymous.Delete("/api/me/discord"));

        var discordOnly = Host.NewClient();
        await Host.DiscordAsync(discordOnly, Login, ApiHost.NewDiscordId());
        await discordOnly.RefreshCsrfAsync();
        await Rec("unlink.no-password", "/api/me/discord", discordOnly.Delete("/api/me/discord"));
        await Rec("link-start.no-password", LinkStart, discordOnly.Post(LinkStart, new PasswordBody("irrelevant-passphrase")));
        await Rec("link.discord-only-account", Link, discordOnly.Get(Link));
    }

    private async Task DiscordOnlyDeletion()
    {
        var discordOnly = Host.NewClient();
        await Host.DiscordAsync(discordOnly, Login, ApiHost.NewDiscordId());
        await discordOnly.RefreshCsrfAsync();
        await Rec("me.discord-only", "/api/me", discordOnly.Get("/api/me"));
        await Rec("delete.discord-only.unconfirmed", "/api/me/delete", discordOnly.Post("/api/me/delete", new DeleteBody(null, null)));
        await Rec("delete.discord-only.ok", "/api/me/delete", discordOnly.Post("/api/me/delete", new DeleteBody(null, "DELETE")));
    }
}
