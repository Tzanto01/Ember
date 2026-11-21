using System.Net;

namespace Ember.Api.Tests;

/// <summary>The "The password is not correct." answer of the endpoints that re-ask for the login password.</summary>
public class PasswordCheckApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string WrongPassword = """{"type":"validation","title":"One or more validation errors occurred.","status":400,"errors":{"password":["The password is not correct."]}}""";

    private static string B64(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)7, bytes).ToArray());

    private static object KeyBody(string? password) => new
    {
        passphrase = new { salt = B64(16), iterations = 600_000, iv = B64(12), wrapped = B64(48) },
        recovery = new { salt = B64(16), iv = B64(12), wrapped = B64(48) },
        password,
    };

    [DockerFact]
    public async Task Enabling_two_factor_with_a_wrong_or_missing_password_is_a_400_on_password_before_the_code_is_checked()
    {
        var user = await Host.SignedInAsync();

        await (await user.Post("/api/me/2fa/enable", new { code = "123456", password = "wrong-passphrase" })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Post("/api/me/2fa/enable", new { code = "123456" })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
    }

    [DockerFact]
    public async Task Setting_turning_off_and_deleting_the_journal_key_with_a_wrong_or_missing_password_is_a_400_on_password()
    {
        var user = await Host.SignedInAsync();

        await (await user.Put("/api/me/journal-key", KeyBody("wrong-passphrase"))).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Put("/api/me/journal-key", KeyBody(null))).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Post("/api/me/journal-key/turn-off", new { password = "wrong-passphrase" })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Post("/api/me/journal-key/turn-off", new { })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Send(HttpMethod.Delete, "/api/me/journal-key", new { password = "wrong-passphrase" })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
        await (await user.Send(HttpMethod.Delete, "/api/me/journal-key", new { discardEncrypted = true })).ExpectExactProblem(HttpStatusCode.BadRequest, WrongPassword);
    }
}
