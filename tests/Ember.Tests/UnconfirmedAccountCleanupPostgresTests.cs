using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>The unconfirmed-account cleanup's query on the production database: it deletes only the stale empty signup.</summary>
[Collection("Postgres")]
public class UnconfirmedAccountCleanupPostgresTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 3, 10, 3, 30, 0, TimeSpan.Zero);

    private static User Signup(string name, int daysOld) => new()
    {
        Id = Guid.CreateVersion7(Now.AddDays(-daysOld)),
        UserName = $"{name}@example.test",
        Email = $"{name}@example.test",
        PasswordHash = "synthetic-hash",
    };

    [DockerFact]
    public async Task Only_the_stale_unconfirmed_signup_without_data_is_deleted()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        var stale = Signup("stale", 8);
        var young = Signup("young", 6);
        var confirmed = Signup("confirmed", 8);
        confirmed.EmailConfirmed = true;
        var withLogin = Signup("login", 8);
        withLogin.Logins.Add(new() { LoginProvider = DiscordLogin.Provider, ProviderKey = DiscordLogin.Key(77), ProviderDisplayName = DiscordLogin.Provider, UserId = withLogin.Id });
        var withMood = Signup("mood", 8);
        withMood.Moods.Add(new Mood { Label = "Calm" });

        var (deleted, left) = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.AddRange(stale, young, confirmed, withLogin, withMood);
            await db.SaveChangesAsync();
            var count = await sp.GetRequiredService<AccountService>().DeleteStaleUnconfirmedAccountsAsync();
            return (count, await db.Users.Select(u => u.Id).ToListAsync());
        });

        deleted.Should().Be(1);
        left.Should().BeEquivalentTo([young.Id, confirmed.Id, withLogin.Id, withMood.Id]);
    }
}
