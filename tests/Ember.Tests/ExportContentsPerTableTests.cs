using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Privacy;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Ember.Core.Services.ExportBuilder;

namespace Ember.Tests;

/// <summary>
/// Pins, on Postgres, which part of the export holds each registry entity through the real web and Discord loaders, and that the
/// entities registered as never exported leave no trace in the JSON or CSV (feedback and session metadata among them, decision D7).
/// </summary>
[Collection("Postgres")]
public class ExportContentsPerTableTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid UserId = TestUsers.Id(1);
    private static readonly Guid FriendId = TestUsers.Id(2);

    private static readonly Dictionary<Type, Func<ExportDocument, object?>> ExportedPart = new()
    {
        [typeof(User)] = d => d.Settings,
        [typeof(IdentityUserLogin<Guid>)] = d => d.Account?.DiscordId,
        [typeof(Habit)] = d => d.Habits,
        [typeof(HabitLog)] = d => d.Logs,
        [typeof(HabitSlip)] = d => d.Slips,
        [typeof(HabitNote)] = d => d.HabitNotes,
        [typeof(DayMark)] = d => d.DayMarks,
        [typeof(DayNote)] = d => d.DayNotes,
        [typeof(Mood)] = d => d.Moods,
        [typeof(UserHelps)] = d => d.HelpsEncrypted,
        [typeof(Routine)] = d => d.Routines,
        [typeof(JournalKey)] = d => d.JournalKey,
        [typeof(Ember.Core.Models.PushSubscription)] = d => d.PushSubscriptions,
        [typeof(ReminderPhone)] = d => d.Phones,
    };

    private static readonly Dictionary<Type, string[]> NeverExportedMarkers = new()
    {
        [typeof(IdentityUserClaim<Guid>)] = ["zz-marker-claim-type", "zz-marker-claim-value"],
        [typeof(IdentityUserToken<Guid>)] = ["zz-marker-token-name", "zz-marker-token-value"],
        [typeof(ReminderMessage)] = [],
        [typeof(PushReminder)] = [],
        [typeof(ReminderActionToken)] = [],
        [typeof(AuthSession)] = ["zz-sess-plat", "zz-marker-session-version"],
        [typeof(Feedback)] = ["zz-marker-feedback-title", "zz-marker-feedback-text", "zz-fb-plat"],
        [typeof(FeedbackImage)] = ["zz-img-type"],
        [typeof(FiresideCode)] = ["zz-marker-code-hash", "zz-marker-code-protected"],
        [typeof(FiresideLink)] = ["zz-marker-known-name"],
        [typeof(Warmth)] = ["zz-warmth-msg"],
        [typeof(LookoutUserRef)] = ["zz-marker-lookout-ref"],
        [typeof(LookoutOutbox)] = ["zz-marker-outbox-key"],
    };

    private static readonly string[] SecretMarkers = ["zz-marker-password-hash", "zz-marker-email-code-hash"];

    private async Task<IntegrationTestHost> SeededHostAsync()
    {
        var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now),
            postgresConnectionString: await postgres.CreateDatabaseAsync());
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User
            {
                Id = UserId,
                UserName = "export-user",
                Email = "export-user@example.test",
                PasswordHash = "zz-marker-password-hash",
                EmailCodeHash = "zz-marker-email-code-hash",
                Logins = TestUsers.Discord(1),
                Timezone = "Europe/Berlin",
                FiresideEnabled = true,
                FiresideName = "Sam",
                HealthDataConsentAt = Now.UtcDateTime.AddDays(-60),
                ConsentPolicyVersion = ConsentService.CurrentConsentPolicyVersion,
            };
            var habit = new Habit { Name = "Take meds", CreatedAt = Now.UtcDateTime.AddDays(-60) };
            habit.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 10), Completed = true, Count = 1 });
            habit.Slips.Add(new HabitSlip { Date = new DateOnly(2027, 1, 11), Count = 1, Reasons = ["stress"] });
            habit.Notes.Add(new HabitNote { Date = new DateOnly(2027, 1, 10), Text = "Went fine" });
            user.Habits.Add(habit);
            user.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 12) });
            user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 12), Text = "Rough day" });
            user.Moods.Add(new Mood { Label = "Tired" });
            user.Helps = new UserHelps { Encrypted = "c3ludGhldGlj", Version = 1, UpdatedAt = Now.UtcDateTime };
            user.Routines.Add(new Routine { Name = "Morning" });
            user.JournalKey = new JournalKey { Version = 1, PassphraseSalt = "cHM=", PassphraseIterations = 1, PassphraseIv = "aXY=", PassphraseWrapped = "d3I=" };
            user.PushSubscriptions.Add(new Ember.Core.Models.PushSubscription { Endpoint = "https://push.example.test/u1", P256dh = "k", Auth = "a" });
            user.ReminderPhones.Add(new ReminderPhone { InstallId = "install-1", Label = "Phone" });
            db.Users.Add(user);
            db.Users.Add(new User { Id = FriendId, UserName = "export-friend", Logins = TestUsers.Discord(2) });
            await db.SaveChangesAsync();

            var feedback = new Feedback
            {
                UserId = UserId,
                Platform = "zz-fb-plat",
                Title = "zz-marker-feedback-title",
                Text = "zz-marker-feedback-text",
                CreatedAt = Now.UtcDateTime,
            };
            feedback.Images.Add(new FeedbackImage { ContentType = "zz-img-type", Size = 3, Data = [1, 2, 3] });
            db.Feedback.Add(feedback);
            db.ReminderMessages.Add(new ReminderMessage { UserId = UserId, HabitId = habit.Id, Date = new DateOnly(2027, 1, 14), ChannelId = 5, MessageId = 6 });
            db.PushReminders.Add(new PushReminder { UserId = UserId, HabitId = habit.Id, Date = new DateOnly(2027, 1, 14) });
            db.ReminderActionTokens.Add(new ReminderActionToken
            { TokenHash = [1, 2, 3], UserId = UserId, HabitId = habit.Id, Day = new DateOnly(2027, 1, 14), ExpiresUtc = Now.UtcDateTime.AddDays(1) });
            db.AuthSessions.Add(new AuthSession
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                CreatedUtc = Now,
                ExpiresUtc = Now.AddDays(30),
                Platform = "zz-sess-plat",
                AppVersion = "zz-marker-session-version",
            });
            db.FiresideCodes.Add(new FiresideCode
            { UserId = UserId, CodeHash = "zz-marker-code-hash", CodeProtected = "zz-marker-code-protected", ExpiresAtUtc = Now.UtcDateTime.AddDays(1) });
            db.FiresideLinks.Add(new FiresideLink { OwnerId = UserId, FriendId = FriendId, KnownName = "zz-marker-known-name", CreatedAt = Now.UtcDateTime });
            db.Warmth.Add(new Warmth { FromUserId = FriendId, ToUserId = UserId, Message = "zz-warmth-msg", SentAtUtc = Now.UtcDateTime });
            db.LookoutUserRefs.Add(new LookoutUserRef { Ref = "zz-marker-lookout-ref", UserId = UserId, CreatedAt = Now.UtcDateTime });
            db.LookoutOutbox.Add(new LookoutOutbox { Kind = LookoutOutbox.Ticket, Key = "zz-marker-outbox-key", Feedback = feedback, CreatedAt = Now.UtcDateTime });
            db.Set<IdentityUserClaim<Guid>>().Add(new IdentityUserClaim<Guid>
            { UserId = UserId, ClaimType = "zz-marker-claim-type", ClaimValue = "zz-marker-claim-value" });
            db.Set<IdentityUserToken<Guid>>().Add(new IdentityUserToken<Guid>
            { UserId = UserId, LoginProvider = "zz-provider", Name = "zz-marker-token-name", Value = "zz-marker-token-value" });
            await db.SaveChangesAsync();
        });
        return host;
    }

    private static bool IsPresent(object? part) => part switch
    {
        null => false,
        string s => s.Length > 0,
        System.Collections.IEnumerable e => e.GetEnumerator().MoveNext(),
        _ => true,
    };

    [Fact]
    public void ExportMap_NamesExactlyTheEntitiesTheRegistryExports_AndTheMarkersExactlyTheOnesItNeverExports()
    {
        var exported = DataLifecycle.Entities.Where(e => e.Export != ExportRule.Never).Select(e => e.Entity);
        var never = DataLifecycle.Entities.Where(e => e.Export == ExportRule.Never).Select(e => e.Entity);

        ExportedPart.Keys.Should().BeEquivalentTo(exported);
        NeverExportedMarkers.Keys.Should().BeEquivalentTo(never);
    }

    [DockerFact]
    public async Task EverySeededTable_IsFilled_SoTheExportAssertionsHaveSomethingToFind()
    {
        await using var host = await SeededHostAsync();

        var empty = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            var result = new List<string>();
            foreach (var type in db.Model.GetEntityTypes())
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{type.GetTableName()}\"";
                if (Convert.ToInt64(await command.ExecuteScalarAsync()) == 0) result.Add(type.ClrType.Name);
            }
            return result;
        });

        empty.Should().BeEmpty();
    }

    [DockerFact]
    public async Task WebExport_HoldsEveryExportedEntity_ThroughTheRealLoader()
    {
        await using var host = await SeededHostAsync();

        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetUserForExportAsync(UserId, withHabitNotes: true));
        var document = BuildDocument(user!);

        foreach (var (type, part) in ExportedPart)
            IsPresent(part(document)).Should().BeTrue($"the web export holds {type.Name}");
    }

    [DockerFact]
    public async Task DiscordExport_HoldsTheWebAndDiscordEntities_AndNothingOfTheWebOnlyOnes()
    {
        await using var host = await SeededHostAsync();

        var user = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>()
            .GetUserForExportAsync((ulong)TestUsers.DiscordIdOf(UserId)));
        var document = BuildDocument(user!, forDiscord: true);

        foreach (var entity in DataLifecycle.Entities.Where(e => e.Export != ExportRule.Never))
        {
            var present = IsPresent(ExportedPart[entity.Entity](document));
            present.Should().Be(entity.Export == ExportRule.WebAndDiscord, $"{entity.Entity.Name} is {entity.Export}");
        }
    }

    [DockerFact]
    public async Task NeverExportedEntities_FeedbackAndSessionMetadataIncluded_AreInNeitherTheJsonNorTheCsvOfEitherExport()
    {
        await using var host = await SeededHostAsync();

        var web = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>().GetUserForExportAsync(UserId, withHabitNotes: true));
        var discord = await host.WithScopeAsync(sp => sp.GetRequiredService<HabitService>()
            .GetUserForExportAsync((ulong)TestUsers.DiscordIdOf(UserId)));
        var outputs = new[] { BuildJson(web!), BuildCsv(web!), BuildJson(discord!, forDiscord: true), BuildCsv(discord!, forDiscord: true) };

        foreach (var marker in NeverExportedMarkers.Values.SelectMany(m => m).Concat(SecretMarkers))
            foreach (var output in outputs)
                output.Should().NotContain(marker);
    }
}
