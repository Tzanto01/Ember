using System.Net;
using Ember.Core.Models;
using Ember.Core.Services;

namespace Ember.Api.Tests.Contract;

/// <summary>Fireside: settings and consent, friend codes, joining, friends, warmth and asking for company.</summary>
[ContractArea("fireside")]
public class FiresideContractTests(PostgresFixture postgres) : ContractTestBase(postgres)
{
    private const string Fireside = "/api/fireside/";
    private const string Code = "/api/fireside/code";
    private const string Join = "/api/fireside/join";
    private const string Ask = "/api/fireside/ask";
    private const string Friend = "/api/fireside/friends/{id:int}";
    private const string Warmth = "/api/fireside/friends/{id:int}/warmth";
    private const string Acknowledge = "/api/fireside/friends/{id:int}/acknowledge";
    private const string Dismiss = "/api/fireside/warmth/{id:int}/dismiss";

    private sealed record FiresideSettings(bool? Enabled, string? Name, bool? Notify, string? ConsentVersion);

    private static string Slug() => Guid.NewGuid().ToString("N")[..10];

    private async Task<ApiClient> PersonAsync(string name)
    {
        var user = await Host.SignedInAsync();
        await (await user.Put("/api/fireside", new FiresideSettings(true, name, null, FiresideService.CurrentConsentVersion))).Expect(HttpStatusCode.OK);
        return user;
    }

    private static async Task<int> FriendIdAsync(ApiClient owner)
        => (await (await owner.Get("/api/fireside")).ExpectJson()).GetProperty("friends")[0].GetProperty("id").GetInt32();

    [DockerFact]
    public async Task Fireside_exchanges_match_the_golden()
    {
        await Setup();
        var (owner, joiner) = await Friends();
        await Warmth_(owner, joiner);
        await Asking(owner, joiner);
        await Full();
        await Removing(owner);
        Verify();
    }

    private async Task Setup()
    {
        var user = await Host.SignedInAsync();
        await Rec("get.off", Fireside, user.Get("/api/fireside"));
        await Rec("get.unauthenticated", Fireside, Host.NewClient().Get("/api/fireside"));
        await Rec("put.missing-enabled", Fireside, user.Put("/api/fireside", new FiresideSettings(null, "Ember", null, null)));
        await Rec("put.invalid-name", Fireside, user.Put("/api/fireside", new FiresideSettings(true, new string('x', 33), null, FiresideService.CurrentConsentVersion)));
        await Rec("put.name-required", Fireside, user.Put("/api/fireside", new FiresideSettings(true, null, null, FiresideService.CurrentConsentVersion)));
        await Rec("put.consent-required", Fireside, user.Put("/api/fireside", new FiresideSettings(true, "Ember", null, null)));
        await Rec("put.old-consent", Fireside, user.Put("/api/fireside", new FiresideSettings(true, "Ember", null, "2000-01-01")));
        await Rec("code.off", Code, user.Post(Code));
        await Rec("ask.off", Ask, user.Put(Ask));
        await Rec("join.off", Join, user.Post(Join, new { code = "ABCD EFGH" }));
        await Rec("put.ok", Fireside, user.Put("/api/fireside", new FiresideSettings(true, $"Ember{Slug()}", false, FiresideService.CurrentConsentVersion)));
        await Rec("get.on", Fireside, user.Get("/api/fireside"));
        await Rec("put.rename-and-notify", Fireside, user.Put("/api/fireside", new FiresideSettings(true, $"Ember{Slug()}", true, null)));
        await Rec("put.off", Fireside, user.Put("/api/fireside", new FiresideSettings(false, null, null, null)));

        var withoutConsent = await Host.SignedInAsync(consent: false);
        await Rec("get.no-health-consent", Fireside, withoutConsent.Get("/api/fireside"));
    }

    private async Task<(ApiClient Owner, ApiClient Joiner)> Friends()
    {
        var owner = await PersonAsync($"Owner{Slug()}");
        var joiner = await PersonAsync($"Joiner{Slug()}");
        var created = await Rec("code.ok", Code, owner.Post(Code));
        var code = (await created.ExpectJson()).GetProperty("code").GetString()!;
        await Rec("get.with-code", Fireside, owner.Get("/api/fireside"));
        await Rec("join.invalid", Join, joiner.Post(Join, new { code = "NOPE NOPE" }));
        await Rec("join.missing-code", Join, joiner.Post(Join, new { }));
        await Rec("join.own-code", Join, owner.Post(Join, new { code }));
        await Rec("join.ok", Join, joiner.Post(Join, new { code }));
        await Rec("join.used-code", Join, joiner.Post(Join, new { code }));
        await Rec("get.with-friend", Fireside, owner.Get("/api/fireside"));
        var second = (await (await owner.Post(Code)).ExpectJson()).GetProperty("code").GetString()!;
        await Rec("join.already-friends", Join, joiner.Post(Join, new { code = second }));
        await Rec("code.delete.ok", Code, owner.Delete(Code));
        await Rec("code.delete.again", Code, owner.Delete(Code));

        var tryer = await PersonAsync($"Tryer{Slug()}");
        for (var i = 0; i < 10; i++) await tryer.Post(Join, new { code = "NOPE NOPE" });
        await Rec("join.rate-limited", Join, tryer.Post(Join, new { code = "NOPE NOPE" }));
        return (owner, joiner);
    }

    private async Task Warmth_(ApiClient owner, ApiClient joiner)
    {
        var link = await FriendIdAsync(owner);
        await Rec("warmth.post.ok", Warmth, owner.Post($"/api/fireside/friends/{link}/warmth", new { message = "thinking" }));
        await Rec("warmth.post.limit", Warmth, owner.Post($"/api/fireside/friends/{link}/warmth", new { message = "thinking" }));
        await Rec("warmth.post.invalid-message", Warmth, owner.Post($"/api/fireside/friends/{link}/warmth", new { message = "hello there" }));
        await Rec("warmth.post.not-found", Warmth, owner.Post("/api/fireside/friends/9999/warmth", new { message = "thinking" }));
        var received = await Rec("get.with-warmth", Fireside, joiner.Get("/api/fireside"));
        var warmth = (await received.ExpectJson()).GetProperty("warmth")[0].GetProperty("id").GetInt32();
        await Rec("warmth.dismiss.ok", Dismiss, joiner.Post($"/api/fireside/warmth/{warmth}/dismiss"));
        await Rec("warmth.dismiss.again", Dismiss, joiner.Post($"/api/fireside/warmth/{warmth}/dismiss"));
        await Rec("warmth.dismiss.foreign", Dismiss, owner.Post($"/api/fireside/warmth/{warmth}/dismiss"));
    }

    private async Task Asking(ApiClient owner, ApiClient joiner)
    {
        var link = await FriendIdAsync(owner);
        await Rec("friend.patch.mute", Friend, owner.Patch($"/api/fireside/friends/{link}", new { muted = true }));
        await Rec("friend.patch.share-flame", Friend, owner.Patch($"/api/fireside/friends/{link}", new { shareFlame = true, muted = false }));
        await Rec("friend.patch.nothing", Friend, owner.Patch($"/api/fireside/friends/{link}", new { }));
        await Rec("friend.patch.not-found", Friend, owner.Patch("/api/fireside/friends/9999", new { muted = true }));
        var joinerLink = await FriendIdAsync(joiner);
        await Rec("friend.acknowledge.joined", Acknowledge, joiner.Post($"/api/fireside/friends/{joinerLink}/acknowledge", new { joined = true }));
        await Rec("friend.acknowledge.rename", Acknowledge, joiner.Post($"/api/fireside/friends/{joinerLink}/acknowledge", new { rename = true }));
        await Rec("friend.acknowledge.not-found", Acknowledge, joiner.Post("/api/fireside/friends/9999/acknowledge", new { }));

        await Rec("ask.put.ok", Ask, owner.Put(Ask));
        await Rec("get.asking", Fireside, joiner.Get("/api/fireside"));
        await Rec("ask.delete.ok", Ask, owner.Delete(Ask));
        await Rec("ask.delete.again", Ask, owner.Delete(Ask));
    }

    private async Task Full()
    {
        var owner = await PersonAsync($"Full{Slug()}");
        var ownerId = await owner.IdAsync();
        for (var i = 0; i < FiresideService.MaxFriends; i++)
        {
            var friend = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
            await Host.Db(async db =>
            {
                db.FiresideLinks.Add(new FiresideLink { OwnerId = ownerId, FriendId = friend, KnownName = $"Friend{i}", CreatedAt = Host.Time.GetUtcNow().UtcDateTime });
                db.FiresideLinks.Add(new FiresideLink { OwnerId = friend, FriendId = ownerId, KnownName = $"Full{i}", CreatedAt = Host.Time.GetUtcNow().UtcDateTime });
                return await db.SaveChangesAsync();
            });
        }

        var joiner = await PersonAsync($"Late{Slug()}");
        var code = (await (await joiner.Post(Code)).ExpectJson()).GetProperty("code").GetString()!;
        await Rec("join.full", Join, owner.Post(Join, new { code }));
    }

    private async Task Removing(ApiClient owner)
    {
        var link = await FriendIdAsync(owner);
        await Rec("friend.delete.ok", Friend, owner.Delete($"/api/fireside/friends/{link}"));
        await Rec("friend.delete.again", Friend, owner.Delete($"/api/fireside/friends/{link}"));

        var first = await PersonAsync($"First{Slug()}");
        var second = await PersonAsync($"Second{Slug()}");
        var code = (await (await first.Post(Code)).ExpectJson()).GetProperty("code").GetString()!;
        await (await second.Post(Join, new { code })).Expect(HttpStatusCode.OK);
        await Rec("friend.delete.revoke-code", Friend, second.Delete($"/api/fireside/friends/{await FriendIdAsync(second)}?revokeCode=true"));
    }
}
