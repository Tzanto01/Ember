using System.Diagnostics;
using System.Text.Json;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Api.Tests;

[Collection("Postgres")]
public sealed class DeletionLedgerTests(PostgresFixture postgres) : ApiTestBase(postgres), IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ember-ledger-").FullName;

    private string Ledger => Path.Combine(_dir, "deletions.jsonl");

    protected override Dictionary<string, string?>? Settings => new() { [DeletionLedger.FileSetting] = Ledger };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task<Guid> UserWithHabit()
    {
        var id = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        await Host.Run(s => s.GiveHealthDataConsentAsync(id));
        await Host.Run(s => s.AddHabitAsync(id, "Stretch", null));
        return id;
    }

    private Task<bool> Exists(Guid id) => Host.Db(db => db.Users.AnyAsync(u => u.Id == id));

    private Task<int> Habits(Guid id) => Host.Db(db => db.Habits.CountAsync(h => h.UserId == id));

    private List<JsonElement> Lines() => File.ReadAllLines(Ledger).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

    private async Task<(int Exit, string Output)> Replay(DateTime since)
    {
        var connection = Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection");
        await using var db = new EmberDbContext(new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connection).Options);
        var output = new StringWriter();
        var exit = await DeletionReplayCli.ReplayAsync(db, DeletionLedger.Read(Ledger), since, output, Host.Time);
        return (exit, output.ToString().Trim());
    }

    private static string Line(DateTime at, string kind, Guid userId) =>
        JsonSerializer.Serialize(new DeletionLedger.Entry(at, kind, userId), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [DockerFact]
    public async Task An_account_deletion_and_a_health_data_deletion_each_append_one_line_with_only_time_kind_and_id()
    {
        var deleted = await UserWithHabit();
        var withdrawn = await UserWithHabit();
        await Host.Run(s => s.WithdrawHealthDataConsentAsync(withdrawn));

        await Host.Run(s => s.DeleteUserDataAsync(deleted));
        Host.Time.Advance(TimeSpan.FromMinutes(5));
        (await Host.Service(s => s.DeleteHealthDataAsync(withdrawn, Host.Time.GetUtcNow().UtcDateTime))).Should().BeTrue();
        await Host.Run(s => s.DeleteUserDataAsync(Guid.NewGuid()));

        var lines = Lines();
        lines.Select(l => l.EnumerateObject().Select(p => p.Name)).Should().AllSatisfy(names => names.Should().Equal("at", "kind", "userId"));
        lines.Select(l => (l.GetProperty("at").GetString(), l.GetProperty("kind").GetString(), l.GetProperty("userId").GetGuid())).Should().Equal(
            ("2026-11-01T12:00:00Z", "account", deleted), ("2026-11-01T12:05:00Z", "health", withdrawn));
    }

    [DockerFact]
    public async Task A_ledger_it_cannot_write_logs_a_warning_without_the_id_and_the_deletion_still_happens()
    {
        var id = await UserWithHabit();
        var logs = new CapturingLogger<DeletionLedger>();
        var ledger = new DeletionLedger(Path.Combine(_dir, "missing", "deletions.jsonl"), Host.Time, logs);

        await Host.Db(async db =>
        {
            await new HabitService(db, Host.Time, ledger).DeleteUserDataAsync(id);
            return 0;
        });

        (await Exists(id)).Should().BeFalse();
        logs.All.Should().Contain("Warning: Could not write a deletion (account) to the deletion ledger: DirectoryNotFoundException")
            .And.NotContain(id.ToString());
    }

    [Fact]
    public void Without_a_file_setting_the_ledger_is_off_and_writes_nothing()
    {
        var unset = new DeletionLedger(null, TimeProvider.System, new CapturingLogger<DeletionLedger>());
        var empty = new DeletionLedger(" ", TimeProvider.System, new CapturingLogger<DeletionLedger>());

        unset.Record(DeletionLedger.Account, Guid.NewGuid());
        empty.Record(DeletionLedger.Health, Guid.NewGuid());

        unset.Path.Should().BeNull();
        empty.Path.Should().BeNull();
        unset.Prune().Should().Be(0);
        Directory.EnumerateFileSystemEntries(_dir).Should().BeEmpty();
    }

    [Fact]
    public void Pruning_drops_entries_older_than_40_days_and_keeps_the_rest()
    {
        var now = new DateTime(2026, 12, 20, 3, 30, 0, DateTimeKind.Utc);
        var (old, edge, recent) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        File.WriteAllLines(Ledger, [Line(now.AddDays(-41), "account", old), Line(now.AddDays(-40), "health", edge), Line(now.AddHours(-1), "account", recent)]);
        var ledger = new DeletionLedger(Ledger, new FakeTimeProvider(now), new CapturingLogger<DeletionLedger>());

        var pruned = ledger.Prune();

        pruned.Should().Be(1);
        DeletionLedger.Read(Ledger).Select(e => e.UserId).Should().Equal(edge, recent);
        ledger.Prune().Should().Be(0);
    }

    [DockerFact]
    public async Task Replay_repeats_only_the_deletions_since_the_backup_that_still_apply_and_queues_the_Lookout_deletion()
    {
        var before = await UserWithHabit();
        var account = await UserWithHabit();
        var health = await UserWithHabit();
        var emptied = await UserWithHabit();
        await Host.Run(s => s.SaveFeedbackAsync(account, "web", "1.0", "text", default, "title", []));
        var userRef = await Host.Db(db => db.LookoutUserRefs.Where(r => r.UserId == account).Select(r => r.Ref).SingleAsync());
        await Host.Db(db => db.Habits.Where(h => h.UserId == emptied).ExecuteDeleteAsync());
        var since = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(Ledger,
        [
            Line(since.AddMinutes(-1), "account", before),
            Line(since, "account", account),
            Line(since.AddHours(1), "health", health),
            Line(since.AddHours(2), "health", emptied),
            Line(since.AddHours(3), "account", Guid.NewGuid()),
            Line(since.AddHours(4), "health", Guid.NewGuid()),
        ]);

        var (exit, output) = await Replay(since);

        exit.Should().Be(0);
        output.Should().Be("Deleted again: 1 accounts, health data of 1 users. Already deleted: 3.");
        (await Exists(before)).Should().BeTrue();
        (await Habits(before)).Should().Be(1);
        (await Exists(account)).Should().BeFalse();
        (await Exists(health)).Should().BeTrue();
        (await Habits(health)).Should().Be(0);
        (await Host.Db(db => db.LookoutOutbox.Where(o => o.Kind == LookoutOutbox.UserDelete).Select(o => o.UserRef).ToListAsync())).Should().Equal(userRef);
        File.ReadAllLines(Ledger).Should().HaveCount(6, "a replay records nothing");
    }

    [DockerFact]
    public async Task Replay_run_twice_changes_nothing_the_second_time()
    {
        var account = await UserWithHabit();
        var health = await UserWithHabit();
        var since = new DateTime(2026, 10, 30, 3, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(Ledger, [Line(since.AddHours(1), "account", account), Line(since.AddHours(2), "health", health)]);

        var first = await Replay(since);
        var second = await Replay(since);

        first.Output.Should().Be("Deleted again: 1 accounts, health data of 1 users. Already deleted: 0.");
        second.Should().Be((0, "Deleted again: 0 accounts, health data of 0 users. Already deleted: 2."));
        (await Exists(health)).Should().BeTrue();
    }

    [Theory]
    [InlineData("""{"at":"2026-11-01T12:00:00Z","kind":"other","userId":"0193a6f2-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"at":"2026-11-01T12:00:00","kind":"account","userId":"0193a6f2-0000-7000-8000-000000000001"}""")]
    [InlineData("""{"at":"2026-11-01T12:00:00Z","kind":"account"}""")]
    [InlineData("not json")]
    public void A_ledger_with_a_line_that_is_not_an_entry_is_refused(string bad)
    {
        File.WriteAllLines(Ledger, [Line(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), "account", Guid.NewGuid()), bad]);

        var read = () => DeletionLedger.Read(Ledger);

        read.Should().Throw<InvalidDataException>().WithMessage("Line 2 of the deletion ledger is not an entry.");
    }

    [Theory]
    [InlineData("2026-11-19", true, "Usage: --replay-deletions")]
    [InlineData("2026-11-19T03:00:00", true, "Usage: --replay-deletions")]
    [InlineData("yesterday", true, "Usage: --replay-deletions")]
    [InlineData("2999-01-01T00:00:00Z", true, "The backup time lies in the future.")]
    [InlineData("2026-11-01T03:00:00Z", false, "The deletion ledger is not configured.")]
    [InlineData("2026-11-01T03:00:00+01:00", true, "FileNotFoundException")]
    public async Task The_replay_command_fails_closed_before_it_touches_the_database(string since, bool withLedger, string error)
    {
        var dll = Path.Combine(Path.GetDirectoryName(typeof(ApiApp).Assembly.Location)!, "Ember.Api.dll");
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(dll);
        info.ArgumentList.Add("--replay-deletions");
        info.ArgumentList.Add(since);
        info.Environment["ConnectionStrings__DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2";
        if (withLedger) info.Environment["Ember__DeletionLedgerFile"] = Ledger;
        else info.Environment.Remove("Ember__DeletionLedgerFile");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var err = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.Should().Be(1);
        err.Should().Contain(error);
        (await stdout).Should().BeEmpty();
    }
}
