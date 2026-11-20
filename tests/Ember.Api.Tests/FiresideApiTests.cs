using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Tests;

/// <summary>Fireside v1.22: friends by code and the five warmth messages, through the real HTTP pipeline on Postgres.</summary>
public partial class FiresideApiTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Fireside = "/api/fireside";

    private sealed record Person(ApiClient Client, string Name, Guid Id);

    private sealed record Seeded(Guid Id, string Name);

    [GeneratedRegex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidPattern();

    private static string NewName(string prefix) => $"{prefix}{Guid.NewGuid():N}"[..14];

    private async Task<Person> PersonAsync(string prefix, string? timezone = null, bool enabled = true)
    {
        var user = await Host.SignedInAsync();
        var name = NewName(prefix);
        if (timezone is not null) await (await user.Patch("/api/me", new { timezone })).Expect(HttpStatusCode.OK);
        if (enabled) await (await user.Put(Fireside, new { enabled = true, name, consentVersion = FiresideService.CurrentConsentVersion })).ExpectJson();
        return new Person(user, name, await user.IdAsync());
    }

    private static async Task<JsonElement> StateAsync(Person person) => await (await person.Client.Get(Fireside)).ExpectJson();

    private static IEnumerable<JsonElement> FriendsOf(JsonElement state) => state.GetProperty("friends").EnumerateArray();

    private static async Task<JsonElement> FriendAsync(Person owner, Person friend)
        => FriendsOf(await StateAsync(owner)).Single(f => f.GetProperty("name").GetString() == friend.Name);

    private static async Task<int> LinkIdAsync(Person owner, Person friend) => (await FriendAsync(owner, friend)).GetProperty("id").GetInt32();

    private static async Task<bool> HasFriendAsync(Person owner, Person friend)
        => FriendsOf(await StateAsync(owner)).Any(f => f.GetProperty("name").GetString() == friend.Name);

    private static async Task<string> NewCodeAsync(Person owner)
        => (await (await owner.Client.Post($"{Fireside}/code")).ExpectJson()).GetProperty("code").GetString()!;

    private static Task<HttpResponseMessage> JoinAsync(Person joiner, string code) => joiner.Client.Post($"{Fireside}/join", new { code });

    private static async Task<(int OwnerLink, int JoinerLink)> BefriendAsync(Person owner, Person joiner)
    {
        var friend = await (await JoinAsync(joiner, await NewCodeAsync(owner))).ExpectJson();
        return (await LinkIdAsync(owner, joiner), friend.GetProperty("id").GetInt32());
    }

    private static Task<HttpResponseMessage> SendWarmthAsync(Person from, int linkId, string message = "thinking")
        => from.Client.Post($"{Fireside}/friends/{linkId}/warmth", new { message });

    private async Task<Seeded> SeedAsync(string prefix)
    {
        var id = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        var name = NewName(prefix);
        await Host.Db(db => db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FiresideEnabled, true).SetProperty(u => u.FiresideName, name)));
        await Host.Run(s => s.GiveHealthDataConsentAsync(id));
        return new Seeded(id, name);
    }

    private Task LinkAsync(Guid a, string aName, Guid b, string bName, DateTime createdAt) => Host.Db(async db =>
    {
        db.FiresideLinks.Add(new FiresideLink { OwnerId = a, FriendId = b, KnownName = bName, CreatedAt = createdAt });
        db.FiresideLinks.Add(new FiresideLink { OwnerId = b, FriendId = a, KnownName = aName, CreatedAt = createdAt });
        return await db.SaveChangesAsync();
    });

    private async Task<List<Seeded>> FillFriendsAsync(Person person, int count)
    {
        var made = new List<Seeded>();
        for (var i = 0; i < count; i++)
        {
            var friend = await SeedAsync("f");
            await LinkAsync(person.Id, person.Name, friend.Id, friend.Name, Host.Time.GetUtcNow().UtcDateTime.AddMinutes(-count + i));
            made.Add(friend);
        }
        return made;
    }

    private static string Shape(JsonElement problem)
        => string.Join("|", new[] { "type", "status", "title", "detail" }.Select(p => problem.TryGetProperty(p, out var v) ? v.GetRawText() : ""));

    private static async Task<string> ValidationMessageAsync(HttpResponseMessage response, string field)
    {
        var problem = await response.ExpectProblem(HttpStatusCode.BadRequest, "validation");
        return problem.GetProperty("errors").GetProperty(field).GetRawText();
    }

    [DockerFact]
    public async Task Another_accounts_link_and_warmth_ids_are_404_on_every_route_exactly_like_unknown_ids_and_change_nothing()
    {
        var a = await PersonAsync("A");
        var b = await PersonAsync("B");
        var x = await PersonAsync("X");
        var (aToB, bToA) = await BefriendAsync(a, b);
        (await SendWarmthAsync(b, bToA, "proud")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        var warmthId = (await StateAsync(a)).GetProperty("warmth")[0].GetProperty("id").GetInt32();
        const int unknown = 999_999;

        (HttpMethod Method, string Url, object? Body)[] Attempts(int link, int warmth) =>
        [
            (HttpMethod.Patch, $"{Fireside}/friends/{link}", new { muted = true, shareFlame = true }),
            (HttpMethod.Post, $"{Fireside}/friends/{link}/acknowledge", new { rename = true, joined = true }),
            (HttpMethod.Post, $"{Fireside}/friends/{link}/warmth", new { message = "thinking" }),
            (HttpMethod.Delete, $"{Fireside}/friends/{link}?revokeCode=true", null),
            (HttpMethod.Post, $"{Fireside}/warmth/{warmth}/dismiss", null),
        ];

        foreach (var attacker in new[] { x, b })
        {
            var foreign = Attempts(aToB, warmthId);
            var missing = Attempts(unknown, unknown);
            for (var i = 0; i < foreign.Length; i++)
            {
                var forged = await attacker.Client.Send(foreign[i].Method, foreign[i].Url, foreign[i].Body);
                var absent = await attacker.Client.Send(missing[i].Method, missing[i].Url, missing[i].Body);
                var forgedProblem = await forged.ExpectProblem(HttpStatusCode.NotFound, "not_found");
                var absentProblem = await absent.ExpectProblem(HttpStatusCode.NotFound, "not_found");
                Shape(forgedProblem).Should().Be(Shape(absentProblem));
            }
        }

        var friend = await FriendAsync(a, b);
        (friend.GetProperty("muted").GetBoolean(), friend.GetProperty("shareFlame").GetBoolean()).Should().Be((false, false));
        (await StateAsync(a)).GetProperty("warmth").GetArrayLength().Should().Be(1);
        (await StateAsync(b)).GetProperty("warmth").GetArrayLength().Should().Be(0);
        (await HasFriendAsync(b, a)).Should().BeTrue();
        FriendsOf(await StateAsync(x)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task The_fireside_page_exposes_only_the_callers_own_link_ids_and_never_a_user_id()
    {
        var a = await PersonAsync("A");
        var b = await PersonAsync("B");
        var c = await PersonAsync("C");
        await BefriendAsync(a, b);
        var (aToC, cToA) = await BefriendAsync(a, c);
        await SendWarmthAsync(c, cToA);
        var body = await (await a.Client.Get(Fireside)).Expect(HttpStatusCode.OK);

        var state = JsonElement.Parse(body);

        GuidPattern().IsMatch(body).Should().BeFalse();
        var ownLinkIds = await Host.Db(db => db.FiresideLinks.Where(l => l.OwnerId == a.Id).Select(l => l.Id).ToListAsync());
        FriendsOf(state).Select(f => f.GetProperty("id").GetInt32()).Should().BeEquivalentTo(ownLinkIds);
        state.GetProperty("warmth").EnumerateArray().Select(w => w.GetProperty("friendId").GetInt32()).Should().Equal(aToC);
        var otherSide = await Host.Db(db => db.FiresideLinks.Where(l => l.OwnerId != a.Id).Select(l => l.Id).ToListAsync());
        FriendsOf(state).Select(f => f.GetProperty("id").GetInt32()).Should().NotIntersectWith(otherSide);
    }

    [DockerFact]
    public async Task Every_fireside_route_is_401_without_a_session()
    {
        var anonymous = Host.NewClient();
        (HttpMethod Method, string Url, object? Body)[] routes =
        [
            (HttpMethod.Get, Fireside, null),
            (HttpMethod.Put, Fireside, new { enabled = true, name = "Ann" }),
            (HttpMethod.Post, $"{Fireside}/code", null),
            (HttpMethod.Delete, $"{Fireside}/code", null),
            (HttpMethod.Post, $"{Fireside}/join", new { code = "ABCD EFGH" }),
            (HttpMethod.Post, $"{Fireside}/friends/1/warmth", new { message = "thinking" }),
            (HttpMethod.Patch, $"{Fireside}/friends/1", new { muted = true }),
            (HttpMethod.Post, $"{Fireside}/friends/1/acknowledge", new { rename = true }),
            (HttpMethod.Delete, $"{Fireside}/friends/1", null),
            (HttpMethod.Post, $"{Fireside}/warmth/1/dismiss", null),
            (HttpMethod.Put, $"{Fireside}/ask", null),
            (HttpMethod.Delete, $"{Fireside}/ask", null),
        ];

        foreach (var (method, url, body) in routes)
            await (await anonymous.Send(method, url, body)).ExpectProblem(HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [DockerFact]
    public async Task Joining_makes_both_directions_marks_only_the_owner_and_uses_the_code_up()
    {
        var owner = await PersonAsync("Owner");
        var joiner = await PersonAsync("Joiner");
        var third = await PersonAsync("Third");
        var code = await NewCodeAsync(owner);
        code.Replace(" ", "").Should().MatchRegex("^[2-9A-HJKMNP-Z]{8}$");

        var friend = await (await JoinAsync(joiner, code.Replace(" ", "").ToLowerInvariant())).ExpectJson();

        friend.GetProperty("name").GetString().Should().Be(owner.Name);
        friend.GetProperty("joinedViaMyCode").GetBoolean().Should().BeFalse();
        (await FriendAsync(owner, joiner)).GetProperty("joinedViaMyCode").GetBoolean().Should().BeTrue();
        (await FriendAsync(joiner, owner)).GetProperty("joinedViaMyCode").GetBoolean().Should().BeFalse();
        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        var reuse = await ValidationMessageAsync(await JoinAsync(third, code), "code");
        var unknown = await ValidationMessageAsync(await JoinAsync(third, "ZZZZ ZZZZ"), "code");
        reuse.Should().Be(unknown);
        FriendsOf(await StateAsync(third)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Invalid_expired_used_own_and_malformed_codes_all_give_the_same_400_on_code()
    {
        var owner = await PersonAsync("Owner");
        var first = await PersonAsync("First");
        var caller = await PersonAsync("Caller");
        var used = await NewCodeAsync(owner);
        await (await JoinAsync(first, used)).Expect(HttpStatusCode.OK);
        var own = await NewCodeAsync(caller);
        var other = await PersonAsync("Other");
        var expiring = await NewCodeAsync(other);
        var bad = new[] { "ZZZZ ZZZZ", used, own, "OIL0 1234", new string('A', 65), "", "ABC" };

        var messages = new List<string>();
        foreach (var code in bad) messages.Add(await ValidationMessageAsync(await JoinAsync(caller, code), "code"));
        Host.Time.Advance(TimeSpan.FromMinutes(31));
        messages.Add(await ValidationMessageAsync(await JoinAsync(caller, expiring), "code"));

        messages.Distinct().Should().ContainSingle();
        FriendsOf(await StateAsync(caller)).Should().BeEmpty();
        (await StateAsync(other)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task Joining_someone_you_are_already_friends_with_returns_that_friend_and_still_uses_the_code_up()
    {
        var owner = await PersonAsync("Owner");
        var joiner = await PersonAsync("Joiner");
        var (_, joinerLink) = await BefriendAsync(owner, joiner);
        var code = await NewCodeAsync(owner);

        var again = await (await JoinAsync(joiner, code)).ExpectJson();

        again.GetProperty("id").GetInt32().Should().Be(joinerLink);
        FriendsOf(await StateAsync(joiner)).Should().ContainSingle();
        FriendsOf(await StateAsync(owner)).Should().ContainSingle();
        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task With_fireside_off_joining_making_a_code_and_asking_are_409_and_the_code_survives()
    {
        var owner = await PersonAsync("Owner");
        var off = await PersonAsync("Off", enabled: false);
        var code = await NewCodeAsync(owner);

        await (await JoinAsync(off, code)).ExpectProblem(HttpStatusCode.Conflict, "conflict");
        await (await off.Client.Post($"{Fireside}/code")).ExpectProblem(HttpStatusCode.Conflict, "conflict");
        await (await off.Client.Put($"{Fireside}/ask")).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Object);
        FriendsOf(await StateAsync(owner)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_full_side_is_409_with_one_neutral_message_whichever_side_it_is_and_the_code_is_not_used_up()
    {
        var owner = await PersonAsync("Owner");
        var fullJoiner = await PersonAsync("FullJoiner");
        await FillFriendsAsync(fullJoiner, FiresideService.MaxFriends);
        var code = await NewCodeAsync(owner);
        var callerFull = await (await JoinAsync(fullJoiner, code)).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        var fullOwner = await PersonAsync("FullOwner");
        var joiner = await PersonAsync("Joiner");
        await FillFriendsAsync(fullOwner, FiresideService.MaxFriends);
        var ownerCode = await NewCodeAsync(fullOwner);
        var ownerFull = await (await JoinAsync(joiner, ownerCode)).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        Shape(callerFull).Should().Be(Shape(ownerFull));
        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Object);
        (await StateAsync(fullOwner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Object);
        FriendsOf(await StateAsync(fullJoiner)).Count().Should().Be(FiresideService.MaxFriends);
        FriendsOf(await StateAsync(joiner)).Should().BeEmpty();
    }

    [DockerFact]
    public async Task The_eleventh_failed_try_in_an_hour_is_429_with_retry_after_and_a_success_does_not_count()
    {
        var owner = await PersonAsync("Owner");
        var later = await PersonAsync("Later");
        var caller = await PersonAsync("Caller");
        for (var i = 0; i < 9; i++) await ValidationMessageAsync(await JoinAsync(caller, "ZZZZ ZZZZ"), "code");
        await (await JoinAsync(caller, await NewCodeAsync(owner))).Expect(HttpStatusCode.OK);

        await ValidationMessageAsync(await JoinAsync(caller, "ZZZZ ZZZZ"), "code");
        var limited = await JoinAsync(caller, "ZZZZ ZZZZ");
        var retryAfter = limited.Headers.RetryAfter!.Delta!.Value;
        await limited.ExpectProblem(HttpStatusCode.TooManyRequests, "rate_limited");
        var validWhileLimited = await JoinAsync(caller, await NewCodeAsync(later));

        retryAfter.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromHours(1));
        await validWhileLimited.ExpectProblem(HttpStatusCode.TooManyRequests, "rate_limited");
        Host.Time.Advance(TimeSpan.FromMinutes(61));
        await (await JoinAsync(caller, await NewCodeAsync(later))).Expect(HttpStatusCode.OK);
    }

    [DockerFact]
    public async Task Two_concurrent_joins_with_the_same_code_let_exactly_one_succeed()
    {
        var first = await PersonAsync("First");
        var second = await PersonAsync("Second");
        for (var round = 0; round < 3; round++)
        {
            var owner = await PersonAsync("Owner");
            var code = await NewCodeAsync(owner);

            var statuses = (await Task.WhenAll(JoinAsync(first, code), JoinAsync(second, code))).Select(r => r.StatusCode).ToList();

            statuses.Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.BadRequest]);
            FriendsOf(await StateAsync(owner)).Should().ContainSingle();
        }
    }

    [DockerFact]
    public async Task Warmth_is_once_per_sender_local_day_and_a_bad_message_is_400_without_using_it_up()
    {
        var sender = await PersonAsync("Sender", "Asia/Tokyo");
        var friend = await PersonAsync("Friend");
        var (friendToSender, senderToFriend) = await BefriendAsync(friend, sender);

        await (await SendWarmthAsync(sender, senderToFriend, "nonsense")).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        (await SendWarmthAsync(sender, senderToFriend, "blanket")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await (await SendWarmthAsync(sender, senderToFriend)).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        (await FriendAsync(sender, friend)).GetProperty("canSendWarmth").GetBoolean().Should().BeFalse();
        var received = (await StateAsync(friend)).GetProperty("warmth");
        received.GetArrayLength().Should().Be(1);
        (received[0].GetProperty("message").GetString(), received[0].GetProperty("friendId").GetInt32(), received[0].GetProperty("name").GetString())
            .Should().Be(("blanket", friendToSender, sender.Name));

        Host.Time.Advance(TimeSpan.FromHours(4));

        (await FriendAsync(sender, friend)).GetProperty("canSendWarmth").GetBoolean().Should().BeTrue();
        (await SendWarmthAsync(sender, senderToFriend)).StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [DockerFact]
    public async Task While_the_friend_is_asking_one_more_warmth_is_allowed_that_day_and_no_more()
    {
        var sender = await PersonAsync("Sender");
        var friend = await PersonAsync("Friend");
        var (_, senderToFriend) = await BefriendAsync(friend, sender);
        (await SendWarmthAsync(sender, senderToFriend)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await (await SendWarmthAsync(sender, senderToFriend)).ExpectProblem(HttpStatusCode.Conflict, "conflict");

        await (await friend.Client.Put($"{Fireside}/ask")).ExpectJson();

        (await FriendAsync(sender, friend)).GetProperty("canSendWarmth").GetBoolean().Should().BeTrue();
        (await SendWarmthAsync(sender, senderToFriend, "easy")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        await (await SendWarmthAsync(sender, senderToFriend)).ExpectProblem(HttpStatusCode.Conflict, "conflict");
        (await FriendAsync(sender, friend)).GetProperty("canSendWarmth").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task Warmth_to_someone_who_muted_you_is_204_and_stays_invisible_even_after_they_unmute()
    {
        var sender = await PersonAsync("Sender");
        var muter = await PersonAsync("Muter");
        var (muterToSender, senderToMuter) = await BefriendAsync(muter, sender);
        await (await muter.Client.Patch($"{Fireside}/friends/{muterToSender}", new { muted = true })).ExpectJson();

        (await SendWarmthAsync(sender, senderToMuter)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await StateAsync(muter)).GetProperty("warmth").GetArrayLength().Should().Be(0);
        await (await muter.Client.Patch($"{Fireside}/friends/{muterToSender}", new { muted = false })).ExpectJson();
        (await StateAsync(muter)).GetProperty("warmth").GetArrayLength().Should().Be(0);
    }

    [DockerFact]
    public async Task Warmth_lists_newest_first_without_dismissed_or_muted_friends()
    {
        var receiver = await PersonAsync("Receiver");
        var senders = new List<(Person Person, int ToReceiver)>();
        foreach (var message in new[] { "thinking", "proud", "notAlone" })
        {
            var sender = await PersonAsync("S");
            var (_, toReceiver) = await BefriendAsync(receiver, sender);
            senders.Add((sender, toReceiver));
            await SendWarmthAsync(sender, toReceiver, message);
            Host.Time.Advance(TimeSpan.FromMinutes(1));
        }

        var all = (await StateAsync(receiver)).GetProperty("warmth").EnumerateArray().ToList();
        all.Select(w => w.GetProperty("name").GetString()).Should().Equal(senders.AsEnumerable().Reverse().Select(s => s.Person.Name));
        all.Select(w => w.GetProperty("message").GetString()).Should().Equal("notAlone", "proud", "thinking");

        await (await receiver.Client.Post($"{Fireside}/warmth/{all[0].GetProperty("id").GetInt32()}/dismiss")).Expect(HttpStatusCode.NoContent);
        await (await receiver.Client.Patch($"{Fireside}/friends/{await LinkIdAsync(receiver, senders[1].Person)}", new { muted = true })).ExpectJson();

        (await StateAsync(receiver)).GetProperty("warmth").EnumerateArray().Select(w => w.GetProperty("message").GetString())
            .Should().Equal("thinking");
    }

    [DockerFact]
    public async Task Checked_in_today_is_null_unless_the_friend_shares_the_flame_and_true_after_their_check_in()
    {
        var viewer = await PersonAsync("Viewer");
        var friend = await PersonAsync("Friend");
        var (viewerToFriend, friendToViewer) = await BefriendAsync(viewer, friend);
        var habitId = (await friend.Client.CreateHabitAsync()).GetProperty("id").GetInt32();

        (await FriendAsync(viewer, friend)).GetProperty("checkedInToday").ValueKind.Should().Be(JsonValueKind.Null);

        await (await viewer.Client.Patch($"{Fireside}/friends/{viewerToFriend}", new { shareFlame = true })).ExpectJson();
        (await FriendAsync(viewer, friend)).GetProperty("checkedInToday").ValueKind.Should().Be(JsonValueKind.Null);

        await (await friend.Client.Patch($"{Fireside}/friends/{friendToViewer}", new { shareFlame = true })).ExpectJson();
        (await FriendAsync(viewer, friend)).GetProperty("checkedInToday").GetBoolean().Should().BeFalse();

        await (await friend.Client.Put($"/api/habits/{habitId}/days/{Host.Today.Iso()}", new { completed = true })).ExpectJson();
        (await FriendAsync(viewer, friend)).GetProperty("checkedInToday").GetBoolean().Should().BeTrue();
        (await FriendAsync(friend, viewer)).GetProperty("checkedInToday").GetBoolean().Should().BeFalse();

        await (await friend.Client.Post("/api/me/consent/withdraw")).Expect(HttpStatusCode.NoContent);
        (await FriendAsync(viewer, friend)).GetProperty("checkedInToday").ValueKind.Should().Be(JsonValueKind.Null,
            "a friend who withdrew their health-data consent shares no check-ins");
    }

    [DockerFact]
    public async Task A_friends_new_name_shows_the_old_one_until_acknowledged_and_joined_clears_on_acknowledge()
    {
        var owner = await PersonAsync("Owner");
        var friend = await PersonAsync("Friend");
        var (ownerToFriend, _) = await BefriendAsync(owner, friend);
        var renamed = NewName("Renamed");

        await (await friend.Client.Put(Fireside, new { enabled = true, name = renamed })).ExpectJson();

        var seen = await FriendAsync(owner, new Person(friend.Client, renamed, friend.Id));
        (seen.GetProperty("renamedFrom").GetString(), seen.GetProperty("joinedViaMyCode").GetBoolean()).Should().Be((friend.Name, true));
        var acknowledged = await (await owner.Client.Post($"{Fireside}/friends/{ownerToFriend}/acknowledge", new { rename = true })).ExpectJson();
        acknowledged.GetProperty("renamedFrom").ValueKind.Should().Be(JsonValueKind.Null);
        acknowledged.GetProperty("name").GetString().Should().Be(renamed);
        acknowledged.GetProperty("joinedViaMyCode").GetBoolean().Should().BeTrue();
        var joined = await (await owner.Client.Post($"{Fireside}/friends/{ownerToFriend}/acknowledge", new { joined = true })).ExpectJson();
        joined.GetProperty("joinedViaMyCode").GetBoolean().Should().BeFalse();
    }

    [DockerFact]
    public async Task Removing_a_friend_removes_both_directions_and_only_revokeCode_true_deletes_the_callers_code()
    {
        var owner = await PersonAsync("Owner");
        var kept = await PersonAsync("Kept");
        var revoked = await PersonAsync("Revoked");
        var (ownerToKept, _) = await BefriendAsync(owner, kept);
        var (ownerToRevoked, _) = await BefriendAsync(owner, revoked);
        await NewCodeAsync(owner);

        await (await owner.Client.Delete($"{Fireside}/friends/{ownerToKept}")).Expect(HttpStatusCode.NoContent);

        (await HasFriendAsync(owner, kept)).Should().BeFalse();
        (await HasFriendAsync(kept, owner)).Should().BeFalse();
        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Object);
        await (await owner.Client.Delete($"{Fireside}/friends/{ownerToRevoked}?revokeCode=true")).Expect(HttpStatusCode.NoContent);
        (await HasFriendAsync(revoked, owner)).Should().BeFalse();
        (await StateAsync(owner)).GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        await (await owner.Client.Delete($"{Fireside}/friends/{ownerToRevoked}")).ExpectProblem(HttpStatusCode.NotFound, "not_found");
    }

    [DockerFact]
    public async Task Turning_fireside_on_needs_a_clean_name_and_stores_it_trimmed()
    {
        var user = await PersonAsync("Ann", enabled: false);

        await (await user.Client.Put(Fireside, new { enabled = true })).ExpectProblem(HttpStatusCode.BadRequest, "validation");
        foreach (var name in new[] { "", "   ", "\u200B\u200B", "\u202E\u202D", "Ann\u0007", "Ann\u200Fx", "Lena\U000E0041", new string('x', 33) })
        {
            var problem = await (await user.Client.Put(Fireside, new { enabled = true, name })).ExpectProblem(HttpStatusCode.BadRequest, "validation");
            problem.GetProperty("errors").Has("name").Should().BeTrue($"name {JsonSerializer.Serialize(name)}");
        }
        (await StateAsync(user)).GetProperty("enabled").GetBoolean().Should().BeFalse();

        var saved = await (await user.Client.Put(Fireside, new { enabled = true, name = "  Lena Ö  ", consentVersion = FiresideService.CurrentConsentVersion })).ExpectJson();

        (saved.GetProperty("enabled").GetBoolean(), saved.GetProperty("name").GetString()).Should().Be((true, "Lena Ö"));
        (await StateAsync(user)).GetProperty("name").GetString().Should().Be("Lena Ö");
    }

    [DockerFact]
    public async Task Turning_fireside_on_needs_the_current_fireside_consent_which_is_stored_until_it_is_turned_off()
    {
        var user = await PersonAsync("Ann", enabled: false);
        Task<(DateTime? At, string? Version)> StoredConsent() => Host.Db(db => db.Users.Where(u => u.Id == user.Id)
            .Select(u => new ValueTuple<DateTime?, string?>(u.FiresideConsentAt, u.FiresideConsentVersion)).SingleAsync());

        var state = await StateAsync(user);
        (state.GetProperty("consentVersion").GetString(), state.GetProperty("consentGiven").GetBoolean())
            .Should().Be((FiresideService.CurrentConsentVersion, false));
        await (await user.Client.Put(Fireside, new { enabled = true, name = user.Name }))
            .ExpectProblem(HttpStatusCode.Forbidden, "fireside_consent_required");
        await (await user.Client.Put(Fireside, new { enabled = true, name = user.Name, consentVersion = "2020-01" }))
            .ExpectProblem(HttpStatusCode.Forbidden, "fireside_consent_required");
        (await StateAsync(user)).GetProperty("enabled").GetBoolean().Should().BeFalse();

        var on = await (await user.Client.Put(Fireside, new { enabled = true, name = user.Name, consentVersion = FiresideService.CurrentConsentVersion })).ExpectJson();

        on.GetProperty("consentGiven").GetBoolean().Should().BeTrue();
        (await StoredConsent()).Should().Be(((DateTime?)Host.Time.GetUtcNow().UtcDateTime, FiresideService.CurrentConsentVersion));
        await (await user.Client.Put(Fireside, new { enabled = true, notify = true })).ExpectJson();

        var off = await (await user.Client.Put(Fireside, new { enabled = false })).ExpectJson();

        off.GetProperty("consentGiven").GetBoolean().Should().BeFalse();
        (await StoredConsent()).Should().Be(((DateTime?)null, (string?)null));
        await (await user.Client.Put(Fireside, new { enabled = true }))
            .ExpectProblem(HttpStatusCode.Forbidden, "fireside_consent_required");
    }

    [DockerFact]
    public async Task Turning_fireside_off_deletes_links_in_both_directions_the_code_and_warmth()
    {
        var leaver = await PersonAsync("Leaver");
        var friend = await PersonAsync("Friend");
        var other = await PersonAsync("Other");
        var (leaverToFriend, friendToLeaver) = await BefriendAsync(leaver, friend);
        await BefriendAsync(friend, other);
        await SendWarmthAsync(leaver, leaverToFriend);
        await SendWarmthAsync(friend, friendToLeaver);
        await NewCodeAsync(leaver);

        var off = await (await leaver.Client.Put(Fireside, new { enabled = false })).ExpectJson();

        off.GetProperty("enabled").GetBoolean().Should().BeFalse();
        off.GetProperty("friends").GetArrayLength().Should().Be(0);
        off.GetProperty("code").ValueKind.Should().Be(JsonValueKind.Null);
        (await HasFriendAsync(friend, leaver)).Should().BeFalse();
        (await HasFriendAsync(friend, other)).Should().BeTrue();
        (await StateAsync(friend)).GetProperty("warmth").GetArrayLength().Should().Be(0);
        (await Host.Db(db => db.FiresideLinks.CountAsync(l => l.OwnerId == leaver.Id || l.FriendId == leaver.Id))).Should().Be(0);
        (await Host.Db(db => db.Warmth.CountAsync(w => w.FromUserId == leaver.Id || w.ToUserId == leaver.Id))).Should().Be(0);
        (await Host.Db(db => db.FiresideCodes.CountAsync(c => c.UserId == leaver.Id))).Should().Be(0);
    }

    [DockerFact]
    public async Task Asking_for_warmth_lasts_to_the_end_of_the_callers_local_day_is_seen_by_friends_and_can_be_stopped()
    {
        var asker = await PersonAsync("Asker", "Asia/Tokyo");
        var friend = await PersonAsync("Friend");
        await BefriendAsync(asker, friend);

        var until = (await (await asker.Client.Put($"{Fireside}/ask")).ExpectJson()).GetProperty("askingUntil").GetDateTimeOffset();

        until.Should().Be(new DateTimeOffset(2026, 11, 1, 15, 0, 0, TimeSpan.Zero));
        (await FriendAsync(friend, asker)).GetProperty("asking").GetBoolean().Should().BeTrue();
        (await StateAsync(asker)).GetProperty("askingUntil").GetDateTimeOffset().Should().Be(until);
        await (await asker.Client.Delete($"{Fireside}/ask")).Expect(HttpStatusCode.NoContent);
        (await FriendAsync(friend, asker)).GetProperty("asking").GetBoolean().Should().BeFalse();
        (await StateAsync(asker)).GetProperty("askingUntil").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [DockerFact]
    public async Task Deleting_the_account_removes_its_codes_links_in_both_directions_and_warmth()
    {
        var leaver = await PersonAsync("Leaver");
        var friend = await PersonAsync("Friend");
        var (leaverToFriend, friendToLeaver) = await BefriendAsync(leaver, friend);
        await SendWarmthAsync(leaver, leaverToFriend);
        await SendWarmthAsync(friend, friendToLeaver);
        await NewCodeAsync(leaver);

        await (await leaver.Client.Post("/api/me/delete", new DeleteRequest(ApiClient.Passphrase, "DELETE"))).Expect(HttpStatusCode.NoContent);

        (await HasFriendAsync(friend, leaver)).Should().BeFalse();
        (await StateAsync(friend)).GetProperty("warmth").GetArrayLength().Should().Be(0);
        (await Host.Db(db => db.FiresideLinks.CountAsync(l => l.OwnerId == leaver.Id || l.FriendId == leaver.Id))).Should().Be(0);
        (await Host.Db(db => db.Warmth.CountAsync(w => w.FromUserId == leaver.Id || w.ToUserId == leaver.Id))).Should().Be(0);
        (await Host.Db(db => db.FiresideCodes.CountAsync(c => c.UserId == leaver.Id))).Should().Be(0);
    }

    [DockerFact]
    public async Task The_json_export_has_the_three_fireside_settings_and_no_friends_or_warmth()
    {
        var user = await PersonAsync("Ann");
        var friend = await PersonAsync("Friend");
        var (_, friendToUser) = await BefriendAsync(user, friend);
        await SendWarmthAsync(friend, friendToUser);
        await (await user.Client.Put(Fireside, new { enabled = true, notify = true })).ExpectJson();

        var json = await (await user.Client.Get("/api/me/export?format=json")).Expect(HttpStatusCode.OK);

        var settings = JsonElement.Parse(json).GetProperty("settings");
        (settings.GetProperty("fireside_enabled").GetBoolean(), settings.GetProperty("fireside_name").GetString(),
            settings.GetProperty("fireside_notify").GetBoolean()).Should().Be((true, user.Name, true));
        json.Should().NotContain(friend.Name).And.NotContainEquivalentOf("warmth").And.NotContainEquivalentOf("friends");
    }

    private async Task<(Guid Id, ulong DiscordId)> SeededSourceAsync(string prefix)
    {
        var discordId = ApiHost.NewDiscordId();
        var id = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(discordId));
        await Host.Run(s => s.GiveHealthDataConsentAsync(id));
        await Host.Db(db => db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FiresideEnabled, true).SetProperty(u => u.FiresideName, prefix).SetProperty(u => u.FiresideNotify, true)
            .SetProperty(u => u.FiresideConsentAt, new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc))
            .SetProperty(u => u.FiresideConsentVersion, FiresideService.CurrentConsentVersion)));
        return (id, discordId);
    }

    [DockerFact]
    public async Task Merging_moves_the_oldest_source_friends_into_free_places_dedupes_and_reports_exactly_what_is_dropped()
    {
        var target = await PersonAsync("Target");
        var targetOwn = await FillFriendsAsync(target, 17);
        var shared = await SeedAsync("shared");
        var (sourceId, discordId) = await SeededSourceAsync("Source");
        var t0 = Host.Time.GetUtcNow().UtcDateTime.AddDays(-30);
        await LinkAsync(target.Id, target.Name, shared.Id, shared.Name, t0);
        await LinkAsync(sourceId, "Source", shared.Id, shared.Name, t0);
        await LinkAsync(sourceId, "Source", target.Id, target.Name, t0.AddMinutes(1));
        var movers = new List<Seeded>();
        for (var i = 0; i < 5; i++)
        {
            var friend = await SeedAsync("o");
            movers.Add(friend);
            await LinkAsync(sourceId, "Source", friend.Id, friend.Name, t0.AddMinutes(2 + i));
        }
        var targetLinksBefore = await Host.Db(db => db.FiresideLinks.Where(l => l.OwnerId == target.Id && l.FriendId != sourceId).Select(l => l.Id).ToListAsync());
        (await Host.DiscordAsync(target.Client, "/api/auth/discord/link", discordId)).Should().Be("/link/merge");

        var preview = await (await target.Client.Get("/api/account/merge")).ExpectJson();
        await (await target.Client.Post("/api/account/merge")).Expect(HttpStatusCode.NoContent);

        preview.GetProperty("firesideDropped").GetInt32().Should().Be(4);
        var friends = await Host.Db(db => db.FiresideLinks.Where(l => l.OwnerId == target.Id).ToListAsync());
        friends.Select(l => l.FriendId).Should().BeEquivalentTo(
            [.. targetOwn.Select(f => f.Id), shared.Id, movers[0].Id, movers[1].Id]);
        friends.Select(l => l.Id).Should().Contain(targetLinksBefore);
        (await Host.Db(db => db.FiresideLinks.Where(l => l.FriendId == target.Id).Select(l => l.OwnerId).ToListAsync()))
            .Should().BeEquivalentTo(friends.Select(l => l.FriendId));
        (await Host.Db(db => db.FiresideLinks.CountAsync(l => l.OwnerId == shared.Id))).Should().Be(1);
        (await Host.Db(db => db.FiresideLinks.CountAsync(l => l.OwnerId == movers[2].Id || l.FriendId == movers[2].Id))).Should().Be(0);
        (await Host.Db(db => db.FiresideLinks.CountAsync(l => l.OwnerId == sourceId || l.FriendId == sourceId))).Should().Be(0);
        FriendsOf(await StateAsync(target)).Count().Should().Be(FiresideService.MaxFriends);
    }

    [DockerFact]
    public async Task Merging_gives_a_target_with_fireside_off_the_sources_settings_and_a_target_with_it_on_keeps_its_own()
    {
        var off = await PersonAsync("Off", enabled: false);
        var offSource = await SeededSourceAsync("SourceOne");
        (await Host.DiscordAsync(off.Client, "/api/auth/discord/link", offSource.DiscordId)).Should().Be("/link/merge");
        await (await off.Client.Post("/api/account/merge")).Expect(HttpStatusCode.NoContent);

        var adopted = await StateAsync(off);

        (adopted.GetProperty("enabled").GetBoolean(), adopted.GetProperty("name").GetString(), adopted.GetProperty("notify").GetBoolean(),
                adopted.GetProperty("consentGiven").GetBoolean())
            .Should().Be((true, "SourceOne", true, true), "Fireside stays on only together with the consent that turned it on");

        var on = await PersonAsync("On");
        var onSource = await SeededSourceAsync("SourceTwo");
        (await Host.DiscordAsync(on.Client, "/api/auth/discord/link", onSource.DiscordId)).Should().Be("/link/merge");
        await (await on.Client.Post("/api/account/merge")).Expect(HttpStatusCode.NoContent);

        var kept = await StateAsync(on);

        (kept.GetProperty("name").GetString(), kept.GetProperty("notify").GetBoolean()).Should().Be((on.Name, false));
    }
}
