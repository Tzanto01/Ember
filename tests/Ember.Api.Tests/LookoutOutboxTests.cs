using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ember.Api.Lookout;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ember.Api.Tests;

internal sealed record Recorded(string Method, string Path, string? Authorization, byte[] Body)
{
    public JsonElement Json() => JsonElement.Parse(Body);
}

internal sealed class FakeLookout : IAsyncDisposable
{
    public const int Abort = -1;

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<int> _script = new();
    private readonly ConcurrentQueue<Recorded> _requests = new();

    private FakeLookout(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    public int DefaultPut { get; set; } = 201;

    public TaskCompletionSource? Hold { get; set; }

    public SemaphoreSlim Arrived { get; } = new(0);

    public List<Recorded> Requests => [.. _requests];

    public void Script(params int[] statuses)
    {
        foreach (var status in statuses) _script.Enqueue(status);
    }

    public static async Task<FakeLookout> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        FakeLookout? self = null;
        app.Run(async context =>
        {
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            self!._requests.Enqueue(new Recorded(context.Request.Method, context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(), body.ToArray()));
            self.Arrived.Release();
            if (self.Hold is { } hold) await hold.Task;
            var status = self._script.TryDequeue(out var scripted) ? scripted : context.Request.Method == "PUT" ? self.DefaultPut : 200;
            if (status == Abort)
            {
                context.Abort();
                return;
            }
            context.Response.StatusCode = status;
        });
        await app.StartAsync();
        self = new FakeLookout(app, app.Urls.First());
        return self;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<string> _lines = new();

    public string All => string.Join("\n", _lines);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
}

internal sealed class SenderRig : IAsyncDisposable
{
    private readonly HttpClient _http = LookoutIngest.CreateClient();
    private LookoutSender _sender = null!;

    private SenderRig(FakeLookout fake, string keyFile, string key, Uri endpoint, CapturingLogger<LookoutSender> logs)
    {
        Fake = fake;
        KeyFile = keyFile;
        Key = key;
        Endpoint = endpoint;
        Logs = logs;
    }

    public FakeLookout Fake { get; }

    public string KeyFile { get; }

    public string Key { get; }

    public Uri Endpoint { get; }

    public CapturingLogger<LookoutSender> Logs { get; }

    public HttpClient Http => _http;

    public static async Task<SenderRig> StartAsync(ApiHost host, Func<string, string?>? ingestUrl = null, bool withKeyFile = true,
        Dictionary<string, string?>? extraSettings = null)
    {
        var fake = await FakeLookout.StartAsync();
        var key = "test-key-" + Guid.NewGuid().ToString("N");
        var keyFile = Path.Combine(Path.GetTempPath(), $"lookout-{Guid.NewGuid():N}.key");
        await File.WriteAllTextAsync(keyFile, key + "\n");
        var settings = new Dictionary<string, string?>(extraSettings ?? []);
        if ((ingestUrl?.Invoke(fake.Url) ?? (ingestUrl is null ? fake.Url : null)) is { } url) settings["Lookout:IngestUrl"] = url;
        if (withKeyFile) settings["Lookout:KeyFile"] = keyFile;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var logs = new CapturingLogger<LookoutSender>();
        LookoutIngest.TryGetEndpoint(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Lookout:IngestUrl"] = fake.Url,
            ["Lookout:KeyFile"] = keyFile,
        }).Build(), out var endpoint, out _);
        var rig = new SenderRig(fake, keyFile, key, endpoint, logs);
        rig._sender = new LookoutSender(host.Services.GetRequiredService<IServiceScopeFactory>(), rig._http, configuration, host.Time, logs,
            new LookoutDeletedRefs(configuration[LookoutDeletedRefs.FileSetting], host.Time));
        return rig;
    }

    public Task<TimeSpan> Step() => _sender.StepAsync(default);

    public async ValueTask DisposeAsync()
    {
        _sender.Dispose();
        _http.Dispose();
        await Fake.DisposeAsync();
        File.Delete(KeyFile);
    }
}

public class LookoutOutboxTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly TimeSpan Zero = TimeSpan.Zero;

    private Task<Guid> NewUser() => Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));

    private async Task Report(Guid? userId, string platform, string title, params byte[][] pictures)
    {
        Host.Time.Advance(TimeSpan.FromMinutes(1));
        await Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, platform, "1.2.3", "text of " + title, default, title,
            pictures.Select(p => new FeedbackImage { ContentType = p[0] == 0xFF ? "image/jpeg" : "image/png", Data = p }).ToList()));
    }

    private Task<List<LookoutOutbox>> Outbox() => Host.Db(db => db.LookoutOutbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync());

    private Task<string> RefOf(Guid userId) => Host.Db(db => db.LookoutUserRefs.Where(r => r.UserId == userId).Select(r => r.Ref).SingleAsync());

    private static string Path(LookoutOutbox row) => "/ingest/v1/tickets/" + row.Key;

    private string ConnectionString => Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;

    [DockerFact]
    public async Task Reports_are_delivered_in_order_with_the_key_and_a_body_that_matches_the_stored_feedback()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        var png = SyntheticImages.Png(8, 8);
        var jpeg = SyntheticImages.Jpeg(8, 8);
        var discordPng = SyntheticImages.Png(9, 9);
        await Report(user, "web", "Crash on save", png, jpeg);
        await Report(null, "android", "Dark mode please");
        await Report(user, "discord", "Typo in reminder", discordPng);
        var rows = await Outbox();
        var feedback = await Host.Db(db => db.Feedback.AsNoTracking().OrderBy(f => f.Id).ToListAsync());
        var userRef = await RefOf(user);

        var waits = new[] { await rig.Step(), await rig.Step(), await rig.Step() };
        var idle = await rig.Step();

        rows.Should().HaveCount(3).And.OnlyContain(r => r.Kind == LookoutOutbox.Ticket && r.SentAt == null && r.ParkedAt == null);
        rows.Select(r => r.FeedbackId).Should().Equal(feedback.Select(f => (int?)f.Id));
        waits.Should().OnlyContain(w => w == Zero);
        idle.Should().Be(LookoutSender.PollInterval);
        var requests = rig.Fake.Requests;
        requests.Select(r => (r.Method, r.Path)).Should().Equal(rows.Select(r => ("PUT", Path(r))));
        requests.Should().OnlyContain(r => r.Authorization == "Bearer " + rig.Key);

        var web = requests[0].Json();
        web.GetProperty("kind").GetString().Should().Be("feedback");
        web.GetProperty("title").GetString().Should().Be("Crash on save");
        web.GetProperty("text").GetString().Should().Be("text of Crash on save");
        web.GetProperty("platform").GetString().Should().Be("web");
        web.GetProperty("version").GetString().Should().Be("1.2.3");
        web.GetProperty("createdAt").GetString().Should().Be(LookoutIngest.Utc(feedback[0].CreatedAt)).And.EndWith("Z");
        web.GetProperty("images").EnumerateArray().Select(i => (i.GetProperty("contentType").GetString(), i.GetProperty("data").GetString()))
            .Should().Equal(("image/png", Convert.ToBase64String(png)), ("image/jpeg", Convert.ToBase64String(jpeg)));
        var webRef = web.GetProperty("userRef").GetString()!;
        webRef.Should().Be(userRef).And.MatchRegex("^[A-Za-z0-9_-]{8,64}$").And.NotContain(user.ToString("N")).And.NotContain(user.ToString());

        var android = requests[1].Json();
        android.GetProperty("platform").GetString().Should().Be("android");
        android.GetProperty("userRef").ValueKind.Should().Be(JsonValueKind.Null);
        android.GetProperty("images").GetArrayLength().Should().Be(0);

        var discord = requests[2].Json();
        discord.GetProperty("platform").GetString().Should().Be("discord");
        discord.GetProperty("userRef").GetString().Should().Be(webRef);
        discord.GetProperty("images").EnumerateArray().Single().GetProperty("data").GetString().Should().Be(Convert.ToBase64String(discordPng));

        var after = await Outbox();
        after.Should().OnlyContain(r => r.SentAt == Host.Time.GetUtcNow().UtcDateTime);
    }

    [DockerFact]
    public async Task A_busy_unreachable_or_throttling_Lookout_keeps_the_row_at_the_head_with_growing_waits_and_a_byte_identical_resend()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "First report", SyntheticImages.Png(8, 8));
        await Report(null, "android", "Second report");
        var rows = await Outbox();
        rig.Fake.Script(503, 429, FakeLookout.Abort, 201);

        var first = await rig.Step();
        var second = await rig.Step();
        var third = await rig.Step();
        var requestsWhileFailing = rig.Fake.Requests;
        var rowsWhileFailing = await Outbox();
        var delivered = await rig.Step();
        var next = await rig.Step();

        (first, second, third).Should().Be((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)));
        requestsWhileFailing.Should().HaveCount(3).And.OnlyContain(r => r.Path == Path(rows[0]));
        rowsWhileFailing.Should().OnlyContain(r => r.SentAt == null && r.ParkedAt == null);
        delivered.Should().Be(Zero);
        var all = rig.Fake.Requests;
        all.Take(4).Should().OnlyContain(r => r.Path == Path(rows[0]));
        all.Take(4).Select(r => Convert.ToBase64String(r.Body)).Distinct().Should().ContainSingle();
        next.Should().Be(Zero);
        all[4].Path.Should().Be(Path(rows[1]));
        (await Outbox()).Select(r => r.SentAt != null).Should().Equal(true, true);
    }

    [DockerFact]
    public async Task The_backoff_restarts_at_five_seconds_after_a_delivery()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        await Report(null, "web", "One");
        await Report(null, "web", "Two");
        rig.Fake.Script(503, 503, 201, 503);

        await rig.Step();
        var secondFailure = await rig.Step();
        await rig.Step();
        var failureAfterDelivery = await rig.Step();

        secondFailure.Should().Be(TimeSpan.FromSeconds(10));
        failureAfterDelivery.Should().Be(TimeSpan.FromSeconds(5));
    }

    [DockerFact]
    public async Task A_rejected_key_is_retried_and_never_parks_the_row()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        await Report(null, "web", "Keyed");
        rig.Fake.Script(401, 401, 201);

        var first = await rig.Step();
        var second = await rig.Step();
        var rowAfterRefusals = (await Outbox()).Single();
        var delivered = await rig.Step();

        (first, second).Should().Be((TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        (rowAfterRefusals.ParkedAt, rowAfterRefusals.SentAt).Should().Be(((DateTime?)null, (DateTime?)null));
        delivered.Should().Be(Zero);
        (await Outbox()).Single().SentAt.Should().NotBeNull();
        rig.Logs.All.Should().NotContain(rig.Key);
    }

    [DockerFact]
    public async Task A_permanent_refusal_parks_the_row_without_logging_content_and_the_next_row_is_sent()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "Secret-Title-One");
        await Report(null, "android", "Secret-Title-Two");
        await Report(null, "web", "Secret-Title-Three");
        var rows = await Outbox();
        var userRef = await RefOf(user);
        rig.Fake.Script(400, 409, 201);

        var waits = new[] { await rig.Step(), await rig.Step(), await rig.Step() };

        waits.Should().OnlyContain(w => w == Zero);
        var after = await Outbox();
        after.Select(r => (r.ParkedStatus, Parked: r.ParkedAt != null, Sent: r.SentAt != null))
            .Should().Equal((400, true, false), (409, true, false), ((int?)null, false, true));
        after[0].ParkedAt.Should().Be(Host.Time.GetUtcNow().UtcDateTime);
        rig.Fake.Requests.Select(r => r.Path).Should().Equal(rows.Select(Path));
        var logs = rig.Logs.All;
        logs.Should().Contain("400").And.Contain("409");
        foreach (var secret in new[] { "Secret-Title", "text of", rig.Key, userRef }.Concat(rows.Select(r => r.Key!)))
            logs.Should().NotContain(secret);
    }

    [DockerFact]
    public async Task A_failing_send_logs_no_content_or_key()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "Secret-Title-Fail");
        var row = (await Outbox()).Single();
        var userRef = await RefOf(user);
        rig.Fake.Script(503, FakeLookout.Abort);

        await rig.Step();
        await rig.Step();

        var logs = rig.Logs.All;
        logs.Should().NotBeEmpty();
        foreach (var secret in new[] { "Secret-Title", "text of", rig.Key, userRef, row.Key! })
            logs.Should().NotContain(secret);
    }

    [DockerFact]
    public async Task Deleting_an_account_drops_its_unsent_tickets_and_the_user_deletion_goes_out_before_other_pending_tickets()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var doomed = await NewUser();
        var other = await NewUser();
        await Report(doomed, "web", "Doomed one");
        await Report(other, "web", "Other one");
        await Report(doomed, "discord", "Doomed two");
        var doomedRef = await RefOf(doomed);
        var before = await Outbox();

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(doomed));
        var queued = await Outbox();

        queued.Should().HaveCount(2);
        queued[0].Id.Should().Be(before[1].Id);
        queued[1].Kind.Should().Be(LookoutOutbox.UserDelete);
        queued[1].UserRef.Should().Be(doomedRef);
        queued[1].Id.Should().BeGreaterThan(before.Max(r => r.Id));
        (await Host.Db(db => db.LookoutUserRefs.CountAsync(r => r.UserId == doomed))).Should().Be(0);

        var delete = await rig.Step();
        var ticket = await rig.Step();

        (delete, ticket).Should().Be((Zero, Zero));
        rig.Fake.Requests.Select(r => (r.Method, r.Path)).Should().Equal(("DELETE", "/ingest/v1/users/" + doomedRef), ("PUT", Path(before[1])));
        rig.Fake.Requests[0].Authorization.Should().Be("Bearer " + rig.Key);
        (await Outbox()).Select(r => (r.Kind, r.SentAt != null)).Should().Equal((LookoutOutbox.Ticket, true));
    }

    [DockerFact]
    public async Task A_user_whose_feedback_was_already_sent_still_gets_a_deletion_request()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "Already sent");
        var userRef = await RefOf(user);
        await rig.Step();
        (await Outbox()).Single().SentAt.Should().NotBeNull();

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        var queued = await Outbox();
        await rig.Step();

        queued.Should().ContainSingle().Which.Should().Match<LookoutOutbox>(r => r.Kind == LookoutOutbox.UserDelete && r.UserRef == userRef);
        rig.Fake.Requests.Select(r => (r.Method, r.Path)).Last().Should().Be(("DELETE", "/ingest/v1/users/" + userRef));
        (await Outbox()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_user_deletion_stays_queued_while_Lookout_is_down()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "Gone soon");
        await rig.Step();
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        rig.Fake.Script(503, 200);

        var down = await rig.Step();
        var stillQueued = await Outbox();
        var up = await rig.Step();

        down.Should().Be(TimeSpan.FromSeconds(5));
        stillQueued.Should().ContainSingle().Which.Kind.Should().Be(LookoutOutbox.UserDelete);
        up.Should().Be(Zero);
        (await Outbox()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_ticket_stuck_on_429_at_the_head_does_not_block_a_later_user_deletion()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var stuck = await NewUser();
        var leaving = await NewUser();
        await Report(stuck, "web", "Stuck one");
        await Report(leaving, "web", "Leaving one");
        var leavingRef = await RefOf(leaving);
        var stuckRow = (await Outbox())[0];
        rig.Fake.Script(429);
        await rig.Step();
        await rig.Step();
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(leaving));

        var wait = await rig.Step();

        rig.Fake.Requests.Select(r => (r.Method, r.Path)).Last().Should().Be(("DELETE", "/ingest/v1/users/" + leavingRef));
        wait.Should().Be(Zero);
        (await Outbox()).Should().ContainSingle().Which.Id.Should().Be(stuckRow.Id);
    }

    [DockerFact]
    public async Task A_user_deletion_is_retried_on_any_refusal_and_never_parked()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var user = await NewUser();
        await Report(user, "web", "Leaving");
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        rig.Fake.Script(400, 404, 409, 200);

        var waits = new[] { await rig.Step(), await rig.Step(), await rig.Step() };
        var rowWhileRefused = (await Outbox()).Single();
        var done = await rig.Step();

        waits.Should().Equal(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));
        rowWhileRefused.ParkedAt.Should().BeNull();
        done.Should().Be(Zero);
        (await Outbox()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task A_ticket_is_retried_on_404_and_parked_only_on_the_permanent_refusals()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        await Report(null, "web", "Row one");
        await Report(null, "web", "Row two");
        rig.Fake.Script(404, 413, 415);

        var notFound = await rig.Step();
        var afterNotFound = await Outbox();
        var tooLarge = await rig.Step();
        var notJson = await rig.Step();

        notFound.Should().Be(TimeSpan.FromSeconds(5));
        afterNotFound.Should().OnlyContain(r => r.ParkedAt == null && r.SentAt == null);
        (tooLarge, notJson).Should().Be((Zero, Zero));
        (await Outbox()).Select(r => r.ParkedStatus).Should().Equal(413, 415);
    }

    [DockerFact]
    public async Task Deleting_a_user_who_never_sent_feedback_queues_nothing()
    {
        var user = await NewUser();

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));

        (await Outbox()).Should().BeEmpty();
    }

    [DockerFact]
    public async Task Merging_moves_the_bots_ref_to_the_account_and_deleting_the_account_queues_both_refs()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        var bot = await NewUser();
        var botDiscordId = await Host.Db(db => db.UserLogins.Where(l => l.UserId == bot).Select(l => l.ProviderKey).SingleAsync());
        await Host.Run<ConsentService>(s => s.GiveHealthDataConsentAsync(bot));
        await Report(bot, "discord", "From the bot");
        var botRef = await RefOf(bot);
        var user = await Host.SignedInAsync();
        var userId = await user.IdAsync();
        await Report(userId, "web", "From the account");
        var userRef = await RefOf(userId);
        await Host.DiscordAsync(user, "/api/auth/discord/link", ulong.Parse(botDiscordId, CultureInfo.InvariantCulture));

        await (await user.Post("/api/account/merge")).Expect(System.Net.HttpStatusCode.NoContent);

        (await Host.Db(db => db.LookoutUserRefs.Where(r => r.UserId == userId).Select(r => r.Ref).ToListAsync())).Should().BeEquivalentTo([botRef, userRef]);
        (await Host.Db(db => db.Users.AnyAsync(u => u.Id == bot))).Should().BeFalse();
        await Report(userId, "web", "After the merge");
        (await Outbox()).Last().UserRef.Should().Be(botRef, "the oldest ref of the merged account is the bot's");

        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(userId));
        var queued = await Outbox();

        queued.Should().OnlyContain(r => r.Kind == LookoutOutbox.UserDelete);
        queued.Select(r => r.UserRef).Should().BeEquivalentTo([botRef, userRef]);
        await rig.Step();
        await rig.Step();
        rig.Fake.Requests.Select(r => r.Method + " " + r.Path).Should().BeEquivalentTo(
            ["DELETE /ingest/v1/users/" + botRef, "DELETE /ingest/v1/users/" + userRef]);
    }

    private async Task Unconfigured(Func<string, string?>? ingestUrl, bool withKeyFile)
    {
        await using var rig = await SenderRig.StartAsync(Host, ingestUrl, withKeyFile);
        var user = await NewUser();
        await Report(user, "web", "Saved anyway");

        var wait = await rig.Step();

        wait.Should().Be(LookoutSender.PollInterval);
        rig.Fake.Requests.Should().BeEmpty();
        (await Outbox()).Should().ContainSingle().Which.SentAt.Should().BeNull();
        (await Host.Db(db => db.Feedback.CountAsync())).Should().Be(1);
    }

    [DockerFact]
    public Task Nothing_is_sent_without_an_ingest_URL() => Unconfigured(_ => null, withKeyFile: true);

    [DockerFact]
    public Task Nothing_is_sent_without_a_key_file() => Unconfigured(null, withKeyFile: false);

    [DockerFact]
    public Task Nothing_is_sent_to_a_host_name_even_when_it_would_reach_the_ingest()
        => Unconfigured(url => url.Replace("127.0.0.1", "localhost"), withKeyFile: true);

    [DockerFact]
    public async Task While_another_sender_holds_the_send_lock_nothing_is_sent_until_it_lets_go()
    {
        await using var rig = await SenderRig.StartAsync(Host);
        await Report(null, "web", "Waiting report");
        await using var holder = new Npgsql.NpgsqlConnection(ConnectionString);
        await holder.OpenAsync();
        await using (var take = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_lock({LookoutIngest.SendLockKey})", holder))
            await take.ExecuteNonQueryAsync();
        var step = rig.Step();

        var early = await rig.Fake.Arrived.WaitAsync(TimeSpan.FromSeconds(1));
        await using (var release = new Npgsql.NpgsqlCommand($"SELECT pg_advisory_unlock({LookoutIngest.SendLockKey})", holder))
            await release.ExecuteNonQueryAsync();

        early.Should().BeFalse();
        (await step).Should().Be(Zero);
        rig.Fake.Requests.Should().ContainSingle();
    }
}
