using Ember.Bot.Jobs;
using Ember.Core.Data;
using Ember.Core.Models;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>The daily cleanup on the production database deletes unsent diagnostics uploads after 30 days, with no upload or sender running.</summary>
[Collection("Postgres")]
public class DiagnosticsCleanupPostgresTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 3, 10, 3, 30, 0, TimeSpan.Zero);

    private static LookoutDiagnostic Upload(string reference, DateTimeOffset createdAt) => new()
    {
        Key = Guid.CreateVersion7(createdAt).ToString("N"),
        Ref = reference,
        Body = "{}"u8.ToArray(),
        CreatedAt = createdAt.UtcDateTime,
    };

    [DockerFact]
    public async Task The_daily_cleanup_deletes_an_upload_unsent_for_30_days_and_keeps_a_fresh_one()
    {
        await using var host = await IntegrationTestHost.CreateAsync(time: new FakeTimeProvider(Now),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.LookoutDiagnostics.AddRange(Upload("EMB-OLD00000", Now.AddDays(-31)), Upload("EMB-NEW00000", Now.AddDays(-29)));
            await db.SaveChangesAsync();
        });

        await host.RunJobAsync(UnconfirmedAccountCleanupJob.Key);

        var left = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().LookoutDiagnostics.Select(d => d.Ref).ToListAsync());
        left.Should().Equal("EMB-NEW00000");
    }
}
