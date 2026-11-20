using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Privacy;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>
/// Pins, on Postgres, that an account holding a single row of one entity counts as non-empty for an import exactly where the registry
/// says <see cref="UserData.BlocksImport"/>, through both <see cref="AccountService.IsAccountEmptyAsync"/> and <see cref="AccountService.ImportAsync"/>.
/// </summary>
[Collection("Postgres")]
public class ImportEmptinessPerTableTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly ImportData EmptyImport = new([], [], [], [], [], null, null, null, 0);

    /// <summary>The user's own row plus the single row of the entity, built for the user and a second user that may be needed as the friend.</summary>
    private static readonly Dictionary<Type, Func<Guid, Guid, object[]>> Seeds = new()
    {
        [typeof(User)] = (_, _) => [],
        [typeof(IdentityUserLogin<Guid>)] = (u, _) => [new IdentityUserLogin<Guid> { UserId = u, LoginProvider = "zz-provider", ProviderKey = u.ToString() }],
        [typeof(IdentityUserClaim<Guid>)] = (u, _) => [new IdentityUserClaim<Guid> { UserId = u, ClaimType = "t", ClaimValue = "v" }],
        [typeof(IdentityUserToken<Guid>)] = (u, _) => [new IdentityUserToken<Guid> { UserId = u, LoginProvider = "zz-provider", Name = "n", Value = "v" }],
        [typeof(Habit)] = (u, _) => [new Habit { UserId = u, Name = "Take meds", CreatedAt = Now.UtcDateTime }],
        [typeof(DayMark)] = (u, _) => [new DayMark { UserId = u, Date = new DateOnly(2027, 1, 12) }],
        [typeof(DayNote)] = (u, _) => [new DayNote { UserId = u, Date = new DateOnly(2027, 1, 12), Text = "Rough day" }],
        [typeof(Mood)] = (u, _) => [new Mood { UserId = u, Label = "Tired" }],
        [typeof(UserHelps)] = (u, _) => [new UserHelps { UserId = u, Encrypted = "c3ludGhldGlj", Version = 1, UpdatedAt = Now.UtcDateTime }],
        [typeof(Routine)] = (u, _) => [new Routine { UserId = u, Name = "Morning" }],
        [typeof(JournalKey)] = (u, _) =>
            [new JournalKey { UserId = u, Version = 1, PassphraseSalt = "cHM=", PassphraseIterations = 1, PassphraseIv = "aXY=", PassphraseWrapped = "d3I=" }],
        [typeof(Ember.Core.Models.PushSubscription)] = (u, _) =>
            [new Ember.Core.Models.PushSubscription { UserId = u, Endpoint = $"https://push.example.test/{u}", P256dh = "k", Auth = "a" }],
        [typeof(ReminderPhone)] = (u, _) => [new ReminderPhone { UserId = u, InstallId = $"install-{u}", Label = "Phone" }],
        [typeof(ReminderMessage)] = (u, _) => [new ReminderMessage { UserId = u, Date = new DateOnly(2027, 1, 14), ChannelId = 5, MessageId = 6 }],
        [typeof(PushReminder)] = (u, _) => [new PushReminder { UserId = u, Date = new DateOnly(2027, 1, 14) }],
        [typeof(AuthSession)] = (u, _) => [new AuthSession { Id = Guid.NewGuid(), UserId = u, CreatedUtc = Now, ExpiresUtc = Now.AddDays(30) }],
        [typeof(Feedback)] = (u, _) => [new Feedback { UserId = u, Platform = "web", Title = "Idea", Text = "More colors", CreatedAt = Now.UtcDateTime }],
        [typeof(FiresideCode)] = (u, _) => [new FiresideCode { UserId = u, CodeHash = $"hash-{u}", ExpiresAtUtc = Now.UtcDateTime.AddDays(1) }],
        [typeof(FiresideLink)] = (u, f) => [new FiresideLink { OwnerId = u, FriendId = f, KnownName = "Sam", CreatedAt = Now.UtcDateTime }],
        [typeof(Warmth)] = (u, f) => [new Warmth { FromUserId = f, ToUserId = u, Message = "Thinking of you", SentAtUtc = Now.UtcDateTime }],
        [typeof(LookoutUserRef)] = (u, _) => [new LookoutUserRef { UserId = u, Ref = $"ref-{u}", CreatedAt = Now.UtcDateTime }],
    };

    /// <summary>Entities not seedable alone: children of another row, rows that need a habit, or rows not keyed to a user.</summary>
    private static readonly Type[] NotSeedableAlone =
    [
        typeof(HabitLog), typeof(HabitSlip), typeof(HabitNote), typeof(FeedbackImage), typeof(ReminderActionToken), typeof(LookoutOutbox),
    ];

    [Fact]
    public void SeedMap_CoversExactlyTheRegistryEntitiesThatCanBeSeededAlone()
    {
        var seedable = DataLifecycle.Entities.Select(e => e.Entity).Except(NotSeedableAlone);

        Seeds.Keys.Should().BeEquivalentTo(seedable);
        NotSeedableAlone.Should().BeSubsetOf(DataLifecycle.Entities.Select(e => e.Entity));
    }

    [DockerFact]
    public async Task AccountWithOneRowOfAnEntity_IsNonEmptyAndRefusesImport_ExactlyWhereTheRegistryBlocksImport()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        var cases = Seeds.Keys.Select((type, index) => (Type: type, UserId: TestUsers.Id(10 + index), FriendId: TestUsers.Id(500 + index))).ToList();

        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            foreach (var (type, userId, friendId) in cases)
            {
                db.Users.Add(new User
                {
                    Id = userId,
                    UserName = $"user-{userId}",
                    HealthDataConsentAt = Now.UtcDateTime.AddDays(-60),
                    ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
                });
                db.Users.Add(new User { Id = friendId, UserName = $"friend-{friendId}" });
                await db.SaveChangesAsync();
                db.AddRange(Seeds[type](userId, friendId));
                await db.SaveChangesAsync();
            }
        });

        foreach (var (type, userId, _) in cases)
        {
            var blocks = DataLifecycle.Entities.Single(e => e.Entity == type).BlocksImport;

            var empty = await host.WithScopeAsync(sp => sp.GetRequiredService<AccountService>().IsAccountEmptyAsync(userId));
            var result = await host.WithScopeAsync(sp => sp.GetRequiredService<AccountService>().ImportAsync(userId, EmptyImport));

            empty.Should().Be(!blocks, $"{type.Name} BlocksImport is {blocks}");
            result.Should().Be(blocks ? ImportResult.NotEmpty : ImportResult.Imported, $"{type.Name} BlocksImport is {blocks}");
        }
    }
}
