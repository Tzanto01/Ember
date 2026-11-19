using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

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
