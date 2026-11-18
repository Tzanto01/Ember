using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Ember.Ops.Tests;

[Collection(V2Collection.Name)]
public sealed class SystemTests(V2DatabaseFixture database) : IDisposable
{
    private readonly OpsApiFactory _factory = new(database);

    public void Dispose() => _factory.Dispose();

    private async Task<SystemReport> SystemAsync(string statusJson)
    {
        await File.WriteAllTextAsync(_factory.StatusFile, statusJson);
        using var client = _factory.ClientFrom();
        return (await client.GetFromJsonAsync<SystemReport>("/api/system", JsonSerializerOptions.Web))!;
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
}
