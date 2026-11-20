using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Integration")]
public class ConsentGateMatrixTests
{
    private static readonly DateTimeOffset Now = new(2027, 1, 15, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2027, 1, 15);
    private static readonly Guid UserId = TestUsers.Id(1);

    private static readonly ImportData EmptyImport = new([], [], [], [], [], null, null, null, 0);

    private static readonly Dictionary<string, Func<HabitService, int, int, Task<string>>> Operations = new()
    {
        ["AddHabit"] = (s, _, _) => Outcome(() => s.AddHabitAsync(UserId, "Take meds", null)),
        ["AddLetGoHabit"] = (s, _, _) => Outcome(() => s.AddHabitAsync(UserId, "Smoking", null, kind: HabitKind.LetGo)),
        ["EditHabitRename"] = (s, b, _) => Outcome(() => s.EditHabitAsync(UserId, b, "Renamed", null, false)),
        ["EditHabitReminder"] = (s, b, _) => Outcome(async () => (await s.EditHabitAsync(UserId, b, null, new TimeOnly(7, 0), false))!.ReminderTime),
        ["Import"] = (s, _, _) => Outcome(() => s.ImportAsync(UserId, EmptyImport)),
        ["CheckIn"] = (s, b, _) => Outcome(() => s.CheckInAsync(UserId, b, Today, true)),
        ["AddCheckIn"] = (s, b, _) => Outcome(() => s.AddCheckInAsync(UserId, b, Today)),
        ["SetCount"] = (s, b, _) => Outcome(() => s.SetCountAsync(UserId, b, Today, 1)),
        ["SetLowDay"] = (s, _, _) => Outcome(() => s.SetLowDayAsync(UserId, Today)),
        ["AddSlip"] = (s, _, l) => Outcome(() => s.AddSlipAsync(UserId, l, Today)),
        ["ClearDay"] = (s, b, _) => Outcome(() => s.ClearDayAsync(UserId, b, Today)),
        ["ClearLowDay"] = (s, _, _) => Outcome(async () => { await s.ClearLowDayAsync(UserId, Today); return "done"; }),
        ["RemoveSlips"] = (s, _, l) => Outcome(() => s.RemoveSlipsAsync(UserId, l, Today)),
        ["SetSchedule"] = (s, b, _) => Outcome(async () => (await s.SetScheduleAsync(UserId, b, FrequencyType.Weekly, 2, null, 1))!.WeeklyTarget),
        ["Pause"] = (s, b, _) => Outcome(async () => (await s.PauseHabitAsync(UserId, b, Today.AddDays(2)))!.PausedUntil!.Value.ToString("yyyy-MM-dd")),
        ["DeleteHabit"] = (s, b, _) => Outcome(() => s.DeleteHabitAsync(UserId, b)),
        ["AddRoutine"] = (s, _, _) => Outcome(async () => (await s.AddRoutineAsync(UserId, "Morning", null, null, null)).Result),
        ["UseReminderActionToken"] = (s, b, _) => Outcome(async () =>
            await s.UseReminderActionTokenAsync(await s.CreateReminderActionTokenAsync(UserId, b, Today, Now.UtcDateTime.AddHours(5)))),
    };

    private static async Task<string> Outcome<T>(Func<Task<T>> call)
    {
        try
        {
            return Convert.ToString(await call(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
        }
        catch (ConsentRequiredException)
        {
            return "ConsentRequiredException";
        }
    }

    private static async Task<(int BuildId, int LetGoId)> SeedWithoutConsentAsync(IntegrationTestHost host)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var user = new User { Id = UserId, Logins = TestUsers.Discord(1), Timezone = "UTC" };
            var build = new Habit { Name = "Walk", CreatedAt = Now.UtcDateTime.AddDays(-30) };
            var letGo = new Habit { Name = "Smoking", Kind = HabitKind.LetGo, CreatedAt = Now.UtcDateTime.AddDays(-30) };
            user.Habits.Add(build);
            user.Habits.Add(letGo);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return (build.Id, letGo.Id);
        });

    [Theory]
    [InlineData("AddHabit", "ConsentRequiredException")]
    [InlineData("AddLetGoHabit", "ConsentRequiredException")]
    [InlineData("EditHabitRename", "ConsentRequiredException")]
    [InlineData("Import", "ConsentRequiredException")]
    [InlineData("CheckIn", "ConsentRequired")]
    [InlineData("AddCheckIn", "ConsentRequired")]
    [InlineData("SetCount", "ConsentRequired")]
    [InlineData("SetLowDay", "ConsentRequired")]
    [InlineData("AddSlip", "ConsentRequired")]
    [InlineData("UseReminderActionToken", "ConsentRequired")]
    [InlineData("EditHabitReminder", "07:00")]
    [InlineData("ClearDay", "Saved")]
    [InlineData("ClearLowDay", "done")]
    [InlineData("RemoveSlips", "Saved")]
    [InlineData("SetSchedule", "2")]
    [InlineData("Pause", "2027-01-17")]
    [InlineData("DeleteHabit", "True")]
    [InlineData("AddRoutine", "Saved")]
    public async Task WithoutConsent_EachMutatingServiceCall_IsRefusedOrAllowedAsPinned(string operation, string expected)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, letGoId) = await SeedWithoutConsentAsync(host);

        var outcome = await host.WithScopeAsync(sp => Operations[operation](sp.GetRequiredService<HabitService>(), buildId, letGoId));

        outcome.Should().Be(expected);
    }

    [Fact]
    public async Task WithoutConsent_ReadsStillWork()
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, _) = await SeedWithoutConsentAsync(host);

        await host.WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<HabitService>();
            (await service.GetHabitsAsync(UserId)).Should().HaveCount(2);
            (await service.GetHabitAsync(UserId, buildId)).Should().NotBeNull();
            (await service.GetHabitDaysAsync(UserId, buildId, 7))!.Should().HaveCount(7);
            (await service.GetLowDaysAsync(UserId)).Should().BeEmpty();
            (await service.GetGraceDaysAsync(UserId)).Should().Be(1);
            (await service.GetUserForExportAsync(UserId)).Should().NotBeNull();
        });
    }
}
