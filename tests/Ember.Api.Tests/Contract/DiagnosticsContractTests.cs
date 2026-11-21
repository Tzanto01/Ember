using System.Net;
using System.Text.Json.Nodes;

namespace Ember.Api.Tests.Contract;

/// <summary>Anonymous diagnostics uploads from the Android app, also from signed-in callers, whose session the endpoint ignores.</summary>
[ContractArea("diagnostics")]
public class DiagnosticsContractTests(PostgresFixture postgres) : ContractTestBase(postgres)
{
    private const string Diagnostics = "/api/diagnostics";

    private static StringContent Upload(string installation, Action<JsonObject>? change = null)
    {
        var request = DiagnosticsFixtures.WithInstallation(installation);
        change?.Invoke(request);
        return DiagnosticsFixtures.Json(request);
    }

    [DockerFact]
    public async Task Diagnostics_exchanges_match_the_golden()
    {
        Host.Time.SetUtcNow(DiagnosticsFixtures.GoldenCreatedAt);
        var anonymous = Host.NewClient();
        var user = await Host.SignedInAsync();

        await Rec("post.ok", Diagnostics, anonymous.Http.PostAsync(Diagnostics, Upload("installation-contract-1")));
        await Rec("post.signed-in.ok", Diagnostics, user.Http.PostAsync(Diagnostics, Upload("installation-contract-2")));
        await Rec("post.invalid", Diagnostics, anonymous.Http.PostAsync(Diagnostics, Upload("short", r => r["exits"]![0]!["reason"] = "BROKEN")));
        await Rec("post.clock", Diagnostics, anonymous.Http.PostAsync(Diagnostics,
            Upload("installation-contract-1", r => r["launches"]![0]!["at"] = "2026-11-21T18:45:00Z")));
        await Rec("post.not-json", Diagnostics, anonymous.Http.PostAsync(Diagnostics,
            new StringContent("installation=x", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded")));
        await Rec("post.cookie-without-token", Diagnostics, CookieWithoutToken(user));
        Verify();
    }

    private async Task<HttpResponseMessage> CookieWithoutToken(ApiClient user)
    {
        var client = Host.NewClient();
        await (await client.Post("/api/auth/login?useCookies=true", new Credentials(user.Email, ApiClient.Passphrase))).Expect(HttpStatusCode.OK);
        return await client.Http.PostAsync(Diagnostics, Upload("installation-contract-3"));
    }
}
