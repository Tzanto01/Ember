using System.Data.Common;
using System.Globalization;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>A failure late in a multi-statement service call must leave the database exactly as it was; run on Postgres, where the services lock rows.</summary>
[Collection("Postgres")]
public class PostgresTransactionTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Throws on the first write that starts with <c>verb</c> and touches <c>table</c>, counting the writes that went through before it.</summary>
    private sealed class FailOnWrite : DbCommandInterceptor
    {
        private string? _verb;
        private string? _table;

        public int WritesBefore { get; private set; }
        public bool Fired { get; private set; }

        public void Arm(string verb, string table)
        {
            (_verb, _table, WritesBefore, Fired) = (verb, table, 0, false);
        }

        private void Check(DbCommand command)
        {
            if (_verb is null) return;
            var text = command.CommandText.TrimStart();
            if (!text.StartsWith("INSERT", StringComparison.Ordinal) && !text.StartsWith("UPDATE", StringComparison.Ordinal)
                && !text.StartsWith("DELETE", StringComparison.Ordinal))
                return;
            if (text.StartsWith(_verb, StringComparison.Ordinal) && text.Contains($"\"{_table}\"", StringComparison.Ordinal))
            {
                (_verb, Fired) = (null, true);
                throw new InvalidOperationException("injected failure");
            }
            WritesBefore++;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Check(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private async Task<(IntegrationTestHost Host, FailOnWrite Failure)> HostAsync()
    {
        var failure = new FailOnWrite();
        var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now), configureDb: o => o.AddInterceptors(failure),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        return (host, failure);
    }

    /// <summary>Every row of every mapped table as text, so any change to any column of any table shows.</summary>
    private static Task<Dictionary<string, List<string>>> SnapshotAsync(IntegrationTestHost host)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            var snapshot = new Dictionary<string, List<string>>();
            foreach (var table in db.Model.GetEntityTypes().Select(t => t.GetTableName()!).Distinct().Order())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{table}\"";
                await using var reader = await command.ExecuteReaderAsync();
                var rows = new List<string>();
                while (await reader.ReadAsync())
                    rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) switch
                    {
                        DBNull => "null",
                        byte[] bytes => Convert.ToHexString(bytes),
                        var value => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
                    })));
                snapshot[table] = rows.Order().ToList();
            }
            return snapshot;
        });

    private static async Task AssertRolledBackAsync(IntegrationTestHost host, FailOnWrite failure, Dictionary<string, List<string>> before, Func<Task> operation)
    {
        await operation.Should().ThrowAsync<Exception>();

        failure.Fired.Should().BeTrue("the failure must be injected at the late write");
        failure.WritesBefore.Should().BeGreaterThan(0, "earlier writes already ran, so the rollback has something to undo");
        (await SnapshotAsync(host)).Should().BeEquivalentTo(before);
    }

    private static Task SeedAsync(IntegrationTestHost host, Action<EmberDbContext> seed)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            seed(db);
            await db.SaveChangesAsync();
        });

    private static User Consented(User user)
    {
        user.HealthDataConsentAt = Now.UtcDateTime.AddDays(-30);
        user.ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion;
        return user;
    }

    [DockerFact]
    public async Task Merge_failing_when_the_source_account_is_deleted_leaves_both_accounts_untouched()
    {
        var (host, failure) = await HostAsync();
        await using var _ = host;
        var source = Consented(new User { Id = TestUsers.Id(5), Logins = TestUsers.Discord(5), Timezone = "UTC" });
        var sourceHabit = new Habit { Name = "Walk", CreatedAt = Now.UtcDateTime.AddDays(-20) };
        sourceHabit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 10), Completed = true, Count = 1 });
        source.Habits.Add(sourceHabit);
        source.Routines.Add(new Routine { Name = "Morning" });
        source.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 11) });
        source.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 12), Text = "Fine" });
        source.Moods.Add(new Mood { Label = "Calm" });
        var target = Consented(new User { Id = TestUsers.Id(6), UserName = "web@example.test", NormalizedUserName = "WEB@EXAMPLE.TEST", Email = "web@example.test" });
        target.Habits.Add(new Habit { Name = "Read", CreatedAt = Now.UtcDateTime.AddDays(-20) });
        await SeedAsync(host, db => db.Users.AddRange(source, target));
        var before = await SnapshotAsync(host);
        failure.Arm("DELETE", "AspNetUsers");

        await AssertRolledBackAsync(host, failure, before, () => host.WithScopeAsync(sp =>
            sp.GetRequiredService<HabitService>().MergeBotOnlyAccountAsync(TestUsers.Id(6), TestUsers.Id(5), 5)));
    }

    [DockerFact]
    public async Task Import_failing_on_the_final_settings_update_leaves_the_account_empty()
    {
        var (host, failure) = await HostAsync();
        await using var _ = host;
        await SeedAsync(host, db => db.Users.Add(Consented(new User { Id = TestUsers.Id(7), Logins = TestUsers.Discord(7), Timezone = "UTC" })));
        var before = await SnapshotAsync(host);
        var habit = new Habit { Name = "Stretch", CreatedAt = Now.UtcDateTime.AddDays(-5), ReminderTime = new TimeOnly(8, 0) };
        habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 10), Completed = true, Count = 1 });
        var mood = new Mood { Id = 3, Label = "Calm" };
        var data = new ImportData([new Routine { Name = "Morning" }], [habit], [new DayMark { Date = new DateOnly(2027, 1, 11) }], [mood],
            [(new DayNote { Date = new DateOnly(2027, 1, 12), Text = "Fine" }, mood)],
            new JournalKey { PassphraseSalt = "cw==", PassphraseIv = "aXY=", PassphraseWrapped = "d3I=", RecoverySalt = "cw==", RecoveryIv = "aXY=", RecoveryWrapped = "d3I=" },
            2, "tidepool", 30);
        failure.Arm("UPDATE", "AspNetUsers");

        await AssertRolledBackAsync(host, failure, before, () => host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().ImportAsync(TestUsers.Id(7), data)));
    }

    [DockerFact]
    public async Task Turning_the_private_journal_off_for_good_failing_at_the_last_delete_keeps_key_notes_moods_and_helps()
    {
        var (host, failure) = await HostAsync();
        await using var _ = host;
        var user = Consented(new User { Id = TestUsers.Id(8), Logins = TestUsers.Discord(8), Timezone = "UTC" });
        var habit = new Habit { Name = "Walk", CreatedAt = Now.UtcDateTime.AddDays(-5) };
        habit.Notes.Add(new HabitNote { Date = new DateOnly(2027, 1, 10), CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });
        user.Habits.Add(habit);
        user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 12), CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });
        user.Moods.Add(new Mood { CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });
        user.JournalKey = new JournalKey { Version = 2, TurningOff = true, PassphraseSalt = "cw==", PassphraseIv = "aXY=", PassphraseWrapped = "d3I=", RecoverySalt = "cw==", RecoveryIv = "aXY=", RecoveryWrapped = "d3I=" };
        user.Helps = new UserHelps { Encrypted = "aGVscHM=", Version = 1, UpdatedAt = Now.UtcDateTime };
        await SeedAsync(host, db => db.Users.Add(user));
        var before = await SnapshotAsync(host);
        failure.Arm("DELETE", "UserHelps");

        await AssertRolledBackAsync(host, failure, before, () => host.WithScopeAsync(sp =>
            sp.GetRequiredService<JournalService>().DeleteJournalKeyAsync(TestUsers.Id(8), discardEncrypted: true)));
    }

    [DockerFact]
    public async Task Fireside_join_failing_when_the_friendship_is_stored_keeps_the_code_and_creates_no_link()
    {
        var (host, failure) = await HostAsync();
        await using var _ = host;
        await SeedAsync(host, db => db.Users.AddRange(
            new User { Id = TestUsers.Id(10), Logins = TestUsers.Discord(10), Timezone = "UTC", FiresideEnabled = true, FiresideName = "Rae" },
            new User { Id = TestUsers.Id(20), Logins = TestUsers.Discord(20), Timezone = "UTC", FiresideEnabled = true, FiresideName = "Sam" }));
        var code = await host.WithScopeAsync(sp => sp.GetRequiredService<FiresideService>().CreateCodeAsync(TestUsers.Id(10)));
        var before = await SnapshotAsync(host);
        failure.Arm("INSERT", "FiresideLinks");

        await AssertRolledBackAsync(host, failure, before, () => host.WithScopeAsync(sp =>
            sp.GetRequiredService<FiresideService>().JoinAsync(TestUsers.Id(20), code!.Code)));
    }
}
