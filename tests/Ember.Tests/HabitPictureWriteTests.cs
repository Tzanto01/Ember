using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class HabitPictureWriteTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_portion_edit_from_a_stale_read_never_leaves_a_portion_on_pills()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var habitId = await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = TestUsers.Id(1), Logins = TestUsers.Discord(1), Timezone = "UTC" };
            var habit = new Habit
            {
                Name = "Read", CreatedAt = Now.UtcDateTime.AddDays(-3),
                Picture = HabitPicture.Shelf, PortionAmount = 15, PortionUnit = PortionUnit.Minutes,
            };
            user.Habits.Add(habit);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return habit.Id;
        });

        await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            await db.Habits.SingleAsync(h => h.Id == habitId);

            await host.WithScopeAsync(other => other.GetRequiredService<EmberDbContext>().Habits.Where(h => h.Id == habitId)
                .ExecuteUpdateAsync(x => x.SetProperty(h => h.Picture, HabitPicture.Pills)
                    .SetProperty(h => h.PortionAmount, (int?)null).SetProperty(h => h.PortionUnit, (PortionUnit?)null)));

            var (result, _) = await sp.GetRequiredService<HabitService>().EditHabitAsync(TestUsers.Id(1), habitId, null, null, false,
                portionAmount: 30, portionUnit: PortionUnit.Pages);
            result.Should().Be(HabitWriteResult.Saved);
        });

        var stored = await host.WithScopeAsync(sp => sp.GetRequiredService<EmberDbContext>().Habits.AsNoTracking().SingleAsync(h => h.Id == habitId));
        (stored.Picture, stored.PortionAmount, stored.PortionUnit).Should().Be((HabitPicture.Shelf, 30, PortionUnit.Pages));
    }
}
