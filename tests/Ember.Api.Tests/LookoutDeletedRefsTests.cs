using System.Diagnostics;
using Ember.Api.Hosting;
using Ember.Api.Lookout;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Api.Tests;

public sealed class LookoutDeletedRefsFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ember-deleted-refs-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));

    private string RefsFile => Path.Combine(_dir, "refs.jsonl");

    private LookoutDeletedRefs Refs() => new(RefsFile, _time);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void A_new_file_says_since_when_it_records_and_only_its_owner_can_read_it()
    {
        Refs().Start();
        _time.Advance(TimeSpan.FromDays(3));
        Refs().Start();

        LookoutDeletedRefs.Read(RefsFile).Should().BeEquivalentTo((new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc), new List<string>()));
        if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(RefsFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Refs_are_kept_30_days_and_the_start_line_stays()
    {
        var refs = Refs();
        refs.Start();
        refs.Append("ref-first-1");
        _time.Advance(TimeSpan.FromDays(29));
        refs.Append("ref-second-2");
        var early = refs.Prune();
        _time.Advance(TimeSpan.FromDays(1));
        var due = refs.Prune();
        refs.Append("ref-third-3");

        (early, due).Should().Be((0, 1));
        var (since, left) = LookoutDeletedRefs.Read(RefsFile);
        since.Should().Be(new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc));
        left.Should().Equal("ref-second-2", "ref-third-3");
        Directory.GetFiles(_dir).Should().Equal(RefsFile);
        if (!OperatingSystem.IsWindows()) File.GetUnixFileMode(RefsFile).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Theory]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"re")]
    [InlineData("�\u0000")]
    public void A_write_that_was_cut_off_is_dropped_by_the_next_one(string cutOff)
    {
        Refs().Start();
        Refs().Append("ref-first-1");
        File.AppendAllText(RefsFile, cutOff);

        Refs().Append("ref-first-1");

        LookoutDeletedRefs.Read(RefsFile).Refs.Should().Equal("ref-first-1", "ref-first-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"ref\":\"ref-first-1\"}\n")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"start\"}\n{\"at\":\"2026-11-01T12:00:00Z\",\"ref\":\"ref-first-1\"}")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"start\"}\n{\"at\":\"2026-11-01T12:00:00Z\",\"ref\":\"ref\\nline\"}\n")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"start\"}\n{\"ref\":\"ref-first-1\"}\n")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"start\"}\n\n{\"at\":\"2026-11-01T12:00:00Z\",\"ref\":\"ref-first-1\"}\n")]
    [InlineData("{\"at\":\"2026-11-01T12:00:00Z\",\"kind\":\"start\"}\nnull\n")]
    public void Reading_refuses_a_file_without_a_start_or_with_any_malformed_line(string content)
    {
        File.WriteAllText(RefsFile, content);

        var read = () => LookoutDeletedRefs.Read(RefsFile);

        read.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Starting_fails_loudly_when_the_configured_folder_does_not_exist()
    {
        var start = () => new LookoutDeletedRefs(Path.Combine(_dir, "missing", "refs.jsonl"), _time).Start();

        start.Should().Throw<DirectoryNotFoundException>();
    }
}

public sealed class LookoutDeletedRefsTests(PostgresFixture postgres) : ApiTestBase(postgres), IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ember-deleted-refs-").FullName;

    private string RefsFile => Path.Combine(_dir, "refs.jsonl");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string ConnectionString => Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;

    private Task<SenderRig> Rig() => SenderRig.StartAsync(Host, extraSettings: new() { [LookoutDeletedRefs.FileSetting] = RefsFile });

    private async Task<string> DeletedUser()
    {
        var user = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        await Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(user, "web", "1.0", "synthetic"));
        var userRef = await Host.Db(db => db.LookoutUserRefs.Where(r => r.UserId == user).Select(r => r.Ref).SingleAsync());
        await Host.Run<AccountService>(s => s.DeleteUserDataAsync(user));
        return userRef;
    }

    private Task<int> Waiting() => Host.Db(db => db.LookoutOutbox.CountAsync());

    private async Task<(int Exit, string Out, string Err)> Verb(string? file, string? connection = null)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Path.GetDirectoryName(typeof(ApiApp).Assembly.Location)!, "Ember.Api.dll"));
        info.ArgumentList.Add("--lookout-deleted-refs");
        info.Environment["ConnectionStrings__DefaultConnection"] = connection ?? ConnectionString;
        info.Environment.Remove("Lookout__DeletedRefsFile");
        if (file is not null) info.Environment["Lookout__DeletedRefsFile"] = file;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, error);
    }

    [DockerFact]
    public async Task A_ref_is_recorded_only_once_Lookout_answered_its_deletion_with_200()
    {
        await using var rig = await Rig();
        var userRef = await DeletedUser();
        rig.Fake.Script(503, 201, 404, FakeLookout.Abort, 200);

        for (var i = 0; i < 4; i++) await rig.Step();
        var early = File.Exists(RefsFile);
        var waiting = await Waiting();
        await rig.Step();

        (early, waiting).Should().Be((false, 1));
        LookoutDeletedRefs.Read(RefsFile).Refs.Should().Equal(userRef);
        (await Waiting()).Should().Be(0);
        rig.Logs.All.Should().NotContain(userRef);
    }

    [DockerFact]
    public async Task A_ref_that_cannot_be_written_stays_in_the_outbox_and_is_sent_again_and_the_log_never_names_it()
    {
        await using var rig = await Rig();
        var userRef = await DeletedUser();
        Directory.CreateDirectory(RefsFile);

        var wait = await rig.Step();
        var waiting = await Waiting();
        Directory.Delete(RefsFile);
        await rig.Step();

        wait.Should().Be(TimeSpan.FromSeconds(5));
        waiting.Should().Be(1);
        rig.Fake.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Method == "DELETE");
        LookoutDeletedRefs.Read(RefsFile).Refs.Should().Equal(userRef);
        (await Waiting()).Should().Be(0);
        rig.Logs.All.Should().Contain("Warning").And.NotContain(userRef);
    }

    [DockerFact]
    public async Task The_sender_prunes_old_refs_also_while_it_has_nothing_to_send()
    {
        await using var rig = await Rig();
        await DeletedUser();
        await rig.Step();
        Host.Time.Advance(TimeSpan.FromDays(31));

        await rig.Step();

        LookoutDeletedRefs.Read(RefsFile).Refs.Should().BeEmpty();
    }

    [DockerFact]
    public async Task The_verb_prints_every_recorded_and_waiting_ref_once_and_the_total_and_start_on_stderr()
    {
        await using var rig = await Rig();
        var recorded = await DeletedUser();
        await rig.Step();
        var waiting = await DeletedUser();
        Host.Time.Advance(TimeSpan.FromDays(31));
        new LookoutDeletedRefs(RefsFile, Host.Time).Append(waiting);

        var (exit, output, error) = await Verb(RefsFile);

        exit.Should().Be(0);
        output.Should().Be($"{recorded}\n{waiting}\n", "no time filter, and a ref in both places once");
        error.Should().StartWith("Total: 2\n").And.Contain("Recorded since 2026-11-01T12:00:00Z");
        error.Should().NotContain(recorded).And.NotContain(waiting);
    }

    [DockerFact]
    public async Task The_verb_prints_no_ref_and_fails_when_the_file_is_unset_missing_or_malformed_or_the_database_is_down()
    {
        await DeletedUser();
        var malformed = Path.Combine(_dir, "malformed.jsonl");
        new LookoutDeletedRefs(malformed, Host.Time).Append("ref-first-1");
        await File.AppendAllTextAsync(malformed, "cut off");
        var good = Path.Combine(_dir, "good.jsonl");
        new LookoutDeletedRefs(good, Host.Time).Append("ref-first-1");

        var results = new[]
        {
            await Verb(null),
            await Verb(""),
            await Verb(Path.Combine(_dir, "missing.jsonl")),
            await Verb(malformed),
            await Verb(good, "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2"),
        };

        results.Should().OnlyContain(r => r.Exit == 1 && r.Out == "" && r.Err.Length > 0);
    }

    [DockerFact]
    public async Task The_verb_fails_on_a_waiting_deletion_without_a_valid_ref()
    {
        new LookoutDeletedRefs(RefsFile, Host.Time).Start();
        await Host.Db(async db =>
        {
            db.LookoutOutbox.Add(new LookoutOutbox { Kind = LookoutOutbox.UserDelete, UserRef = "bad ref", CreatedAt = Host.Time.GetUtcNow().UtcDateTime });
            return await db.SaveChangesAsync();
        });
        await using var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(ConnectionString).Options);

        var print = () => LookoutDeletedRefsCli.PrintAsync(db, RefsFile, TextWriter.Null, TextWriter.Null);

        await print.Should().ThrowAsync<InvalidDataException>();
    }
}
