using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class SystemTests(V2DatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database);

    public void Dispose() => _factory.Dispose();

    private async Task<SystemReport> SystemAsync(string statusJson)
    {
        await File.WriteAllTextAsync(_factory.StatusFile, statusJson);
        return await _factory.Services.GetRequiredService<SystemInfo>().GetAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_old_status_file_with_one_disk_shows_it_as_home()
    {
        var report = await SystemAsync("""{"writtenAt":"2026-11-18T12:00:00Z","disk":{"freeBytes":100,"totalBytes":400}}""");

        report.Disks.Should().Equal(new HostDisk("/home", 100, 400));
        report.NightlyBackup.Should().BeNull();
    }

    [Fact]
    public async Task The_nightly_backup_and_the_pre_deploy_dump_are_read_and_aged_separately()
    {
        var clock = database.Time.GetUtcNow().UtcDateTime;
        var now = clock.AddTicks(-(clock.Ticks % TimeSpan.TicksPerSecond));
        string At(double hoursAgo) => now.AddHours(-hoursAgo).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);

        var report = await SystemAsync($$$"""
            {"writtenAt":"{{{At(0)}}}",
             "backup":{"name":"emberdb-20261115-101500.dump","at":"{{{At(75)}}}","sizeBytes":500},
             "nightlyBackup":{"name":"pg_dumpall_20261118_033000.sql.gz","at":"{{{At(6.5)}}}","sizeBytes":2345}}
            """);

        report.NightlyBackup.Should().Be(new BackupInfo("pg_dumpall_20261118_033000.sql.gz", now.AddHours(-6.5), 6.5, 2345));
        report.Backup.Should().Be(new BackupInfo("emberdb-20261115-101500.dump", now.AddHours(-75), 75, 500));
    }

    [Fact]
    public async Task A_status_file_with_disks_shows_every_disk()
    {
        var report = await SystemAsync("""
            {"writtenAt":"2026-11-18T12:00:00Z","disk":{"freeBytes":1,"totalBytes":2},
             "disks":[{"mount":"/","freeBytes":10,"totalBytes":110},{"mount":"/srv/data","freeBytes":3400,"totalBytes":3600}]}
            """);

        report.Disks.Should().Equal(new HostDisk("/", 10, 110), new HostDisk("/srv/data", 3400, 3600));
    }

    [Fact]
    public async Task The_outbox_counts_pending_and_parked_rows_and_ages_the_oldest_pending_one()
    {
        var now = database.Time.GetUtcNow().UtcDateTime;
        await using var ember = await database.OpenAsync("emberdb");
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "LookoutOutbox" ("Kind", "UserRef", "CreatedAt", "SentAt", "ParkedAt", "ParkedStatus") VALUES
                ('user-delete', 'synthetic-ref-1', $1, NULL, NULL, NULL),
                ('user-delete', 'synthetic-ref-2', $2, NULL, NULL, NULL),
                ('user-delete', 'synthetic-ref-3', $3, $2, NULL, NULL),
                ('user-delete', 'synthetic-ref-4', $3, NULL, $2, 409)
            """, ember) { Parameters = { new() { Value = now.AddHours(-3) }, new() { Value = now.AddMinutes(-30) }, new() { Value = now.AddHours(-6) } } })
            await insert.ExecuteNonQueryAsync();
        try
        {
            var report = await SystemAsync("""{"writtenAt":"2026-11-18T12:00:00Z"}""");

            report.Outbox.Should().Be(new OutboxInfo(2, 1, TimeSpan.FromHours(3)));
            report.Errors.Should().NotContainKey("outbox");
        }
        finally
        {
            await using var clear = new NpgsqlCommand("""DELETE FROM "LookoutOutbox" """, ember);
            await clear.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task An_empty_outbox_has_no_oldest_pending_row()
    {
        var report = await SystemAsync("""{"writtenAt":"2026-11-18T12:00:00Z"}""");

        report.Outbox.Should().Be(new OutboxInfo(0, 0, null));
    }

    [Fact]
    public async Task The_database_counts_every_client_connection_of_the_server_against_max_connections_without_extra_roles()
    {
        await using var other = await database.OpenAsync("postgres");
        await using var ember = await database.OpenAsync("emberdb");

        var report = await SystemAsync("""{"writtenAt":"2026-11-18T12:00:00Z"}""");

        report.Database!.Connections.Should().BeGreaterThanOrEqualTo(3, "the superuser's two and the dashboard's own, whatever their role");
        report.Database.EmberConnections.Should().BeGreaterThanOrEqualTo(2).And.BeLessThan(report.Database.Connections);
        report.Database.MaxConnections.Should().BeGreaterThan(0);
        (await database.ScalarAsync<long>("emberdb", "SELECT count(*) FROM pg_auth_members WHERE member = 'ember_dashboard'::regrole")).Should().Be(0);
    }
}
