using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>Each merge Core refuses as a conflict changes nothing: the source keeps its habit and Discord login, the target its security stamp.</summary>
[Collection("Postgres")]
public class MergeRefusalTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid SourceId = TestUsers.Id(5);
    private static readonly Guid TargetId = TestUsers.Id(6);
    private const string TargetStamp = "TARGET-STAMP";

    private static User Consented(User user)
    {
        user.HealthDataConsentAt = Now.UtcDateTime.AddDays(-30);
        user.ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion;
        return user;
    }

    private async Task AssertRefusedAsync(Action<User, User> change, Guid targetId, Guid sourceId, bool previewRefused)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        var source = Consented(new User { Id = SourceId, Logins = TestUsers.Discord(5), Timezone = "UTC" });
        source.Habits.Add(new Habit { Name = "Walk", CreatedAt = Now.UtcDateTime.AddDays(-20) });
        var target = Consented(new User
        {
            Id = TargetId, UserName = "web@example.test", NormalizedUserName = "WEB@EXAMPLE.TEST", Email = "web@example.test",
            SecurityStamp = TargetStamp,
        });
        change(source, target);
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            db.Users.AddRange(source, target);
            await db.SaveChangesAsync();
        });

        var (preview, result) = await host.WithScopeAsync(async sp =>
        {
            var accounts = sp.GetRequiredService<AccountService>();
            return (await accounts.GetMergePreviewAsync(targetId, sourceId, 5), await accounts.MergeBotOnlyAccountAsync(targetId, sourceId, 5));
        });

        result.Should().Be(AccountMergeResult.Conflict);
        if (previewRefused) preview.Should().BeNull();
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            (await db.Habits.Where(h => h.Name == "Walk").Select(h => h.UserId).SingleAsync()).Should().Be(SourceId);
            (await db.UserLogins.Where(l => l.ProviderKey == DiscordLogin.Key(5)).Select(l => l.UserId).SingleAsync()).Should().Be(SourceId);
            (await db.Users.Where(u => u.Id == TargetId).Select(u => u.SecurityStamp).SingleAsync()).Should().Be(TargetStamp);
        });
    }

    [DockerFact]
    public Task A_target_that_already_has_a_Discord_login_is_a_conflict()
        => AssertRefusedAsync((_, target) => target.Logins = TestUsers.Discord(6), TargetId, SourceId, previewRefused: false);

    [DockerFact]
    public Task A_source_with_an_email_is_a_conflict()
        => AssertRefusedAsync((source, _) => source.Email = "bot@example.test", TargetId, SourceId, previewRefused: true);

    [DockerFact]
    public Task A_source_with_a_password_is_a_conflict()
        => AssertRefusedAsync((source, _) => source.PasswordHash = "synthetic-hash", TargetId, SourceId, previewRefused: true);

    [DockerFact]
    public Task A_missing_target_is_a_conflict()
        => AssertRefusedAsync((_, _) => { }, TestUsers.Id(9), SourceId, previewRefused: false);

    [DockerFact]
    public Task Merging_an_account_into_itself_is_a_conflict()
        => AssertRefusedAsync((_, _) => { }, SourceId, SourceId, previewRefused: false);
}
