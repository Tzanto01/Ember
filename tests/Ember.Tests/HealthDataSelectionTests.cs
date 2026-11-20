using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Privacy;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

/// <summary>
/// Holds <see cref="ConsentService"/>'s health-data check to the registry: a withdrawn user holding a single row of any health entity
/// they own directly counts as holding health data, so the deletion reminder and the deletion job pick them up.
/// </summary>
[Collection("Integration")]
public class HealthDataSelectionTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<Type, Action<User>> Seeds = new()
    {
        [typeof(Habit)] = u => u.Habits.Add(new Habit { Name = "Take meds", CreatedAt = Now.UtcDateTime.AddDays(-60) }),
        [typeof(DayMark)] = u => u.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 2) }),
        [typeof(DayNote)] = u => u.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 2), Text = "Rough day" }),
        [typeof(Mood)] = u => u.Moods.Add(new Mood { Label = "Tired" }),
        [typeof(UserHelps)] = u => u.Helps = new UserHelps { Encrypted = "c3ludGhldGlj", Version = 1, UpdatedAt = Now.UtcDateTime },
    };

    [Fact]
    public void Seeds_cover_exactly_the_health_entities_a_user_owns_directly()
        => Seeds.Keys.Should().BeEquivalentTo(DataLifecycle.Entities.Where(e => e.HealthData && e.Owner == "UserId").Select(e => e.Entity));

    [Fact]
    public async Task A_withdrawn_user_with_one_row_of_any_health_entity_is_due_for_the_reminder_and_then_the_deletion()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var withdrawnAt = Now.UtcDateTime.AddDays(-40);
        var cases = Seeds.Select((seed, i) => (Type: seed.Key, Seed: seed.Value, Unreminded: TestUsers.Id(100 + i), Reminded: TestUsers.Id(200 + i)))
            .ToList();
        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            foreach (var c in cases)
            {
                foreach (var (id, remindedAt) in new[] { (c.Unreminded, (DateTime?)null), (c.Reminded, Now.UtcDateTime.AddDays(-10)) })
                {
                    var user = new User
                    {
                        Id = id, Logins = TestUsers.Discord(TestUsers.DiscordIdOf(id)), ConsentWithdrawnAt = withdrawnAt,
                        DeletionReminderSentAt = remindedAt,
                    };
                    c.Seed(user);
                    db.Users.Add(user);
                }
            }
            await db.SaveChangesAsync();
        });

        await host.WithScopeAsync(async sp =>
        {
            var consent = sp.GetRequiredService<ConsentService>();
            var reminderDue = await consent.GetDeletionReminderDueAsync(Now.UtcDateTime);
            var deletionDue = await consent.GetHealthDataDeletionDueAsync(Now.UtcDateTime, Now.UtcDateTime);
            foreach (var c in cases)
            {
                (await consent.HasHealthDataAsync(c.Unreminded)).Should().BeTrue($"{c.Type.Name} is health data");
                reminderDue.Should().Contain(c.Unreminded, $"a user holding only {c.Type.Name} gets the deletion reminder");
                deletionDue.Should().Contain(c.Reminded, $"a user holding only {c.Type.Name} is deleted after the reminder");
            }
        });
    }
}
