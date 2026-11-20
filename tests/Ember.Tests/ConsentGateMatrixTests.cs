using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
        ["AddHabit"] = (s, _, _) => Outcome(async () => (await s.AddHabitAsync(UserId, "Take meds", null)).Result),
        ["AddLetGoHabit"] = (s, _, _) => Outcome(async () => (await s.AddHabitAsync(UserId, "Smoking", null, kind: HabitKind.LetGo)).Result),
        ["EditHabitRename"] = (s, b, _) => Outcome(async () => (await s.EditHabitAsync(UserId, b, "Renamed", null, false)).Result),
        ["EditHabitReminder"] = (s, b, _) => Outcome(async () => (await s.EditHabitAsync(UserId, b, null, new TimeOnly(7, 0), false)).Habit!.ReminderTime),
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

    private static readonly Dictionary<string, Func<AccountService, int, int, Task<string>>> AccountOperations = new()
    {
        ["Import"] = (a, _, _) => Outcome(() => a.ImportAsync(UserId, EmptyImport)),
    };

    private static readonly Dictionary<string, Func<JournalService, int, int, Task<string>>> JournalOperations = new()
    {
        ["SetDayNote"] = (j, _, _) => Outcome(async () => (await j.SetDayNoteAsync(UserId, Today, "Changed", null, null)).Result),
        ["SetNewDayNote"] = (j, _, _) => Outcome(async () => (await j.SetDayNoteAsync(UserId, Today.AddDays(-1), null, "good", null)).Result),
        ["SetHabitNote"] = (j, b, _) => Outcome(async () => (await j.SetHabitNoteAsync(UserId, b, Today, "Changed", null)).Result),
        ["AddMood"] = (j, _, _) => Outcome(async () => (await j.AddMoodAsync(UserId, "Calm", null, null, false)).Result),
        ["EditMood"] = (j, _, m) => Outcome(async () => (await j.EditMoodAsync(UserId, m, "Changed", null, null, true)).Result),
        ["PutHelps"] = (j, _, _) => Outcome(async () => (await j.PutHelpsAsync(UserId, "changed", 1)).Result),
        ["ClearDayNote"] = (j, _, _) => Outcome(async () => (await j.SetDayNoteAsync(UserId, Today, null, null, null)).Result),
        ["DeleteDayNote"] = (j, _, _) => Outcome(async () => { await j.DeleteDayNoteAsync(UserId, Today); return "done"; }),
        ["DeleteHabitNote"] = (j, b, _) => Outcome(() => j.DeleteHabitNoteAsync(UserId, b, Today)),
        ["DeleteHelps"] = (j, _, _) => Outcome(async () => { await j.DeleteHelpsAsync(UserId); return "done"; }),
    };

    private static async Task<string> Outcome<T>(Func<Task<T>> call)
        => Convert.ToString(await call(), System.Globalization.CultureInfo.InvariantCulture) ?? "null";

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
    [InlineData("AddHabit", "ConsentRequired")]
    [InlineData("AddLetGoHabit", "ConsentRequired")]
    [InlineData("EditHabitRename", "ConsentRequired")]
    [InlineData("Import", "ConsentRequired")]
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

        var outcome = await host.WithScopeAsync(sp => Operations.TryGetValue(operation, out var habitOperation)
            ? habitOperation(sp.GetRequiredService<HabitService>(), buildId, letGoId)
            : AccountOperations[operation](sp.GetRequiredService<AccountService>(), buildId, letGoId));

        outcome.Should().Be(expected);
    }

    /// <summary>A day note and a habit note for today, an own mood and a "What helps me" list for the user without consent.</summary>
    private static async Task<int> SeedJournalAsync(IntegrationTestHost host, int buildId)
        => await host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var mood = new Mood { UserId = UserId, Label = "Tired", CreatedAt = Now.UtcDateTime };
            db.Moods.Add(mood);
            db.DayNotes.Add(new DayNote { UserId = UserId, Date = Today, Text = "A note" });
            db.HabitNotes.Add(new HabitNote { HabitId = buildId, Date = Today, Text = "A habit note" });
            db.UserHelps.Add(new UserHelps { UserId = UserId, Encrypted = "stored", Version = 1, UpdatedAt = Now.UtcDateTime });
            await db.SaveChangesAsync();
            return mood.Id;
        });

    private static Task<string> JournalSnapshotAsync(IntegrationTestHost host)
        => host.WithScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<EmberDbContext>();
            var dayNotes = await db.DayNotes.OrderBy(n => n.Date).Select(n => $"{n.Date}:{n.Text}:{n.Mood}").ToListAsync();
            var habitNotes = await db.HabitNotes.OrderBy(n => n.Date).Select(n => $"{n.HabitId}:{n.Date}:{n.Text}").ToListAsync();
            var moods = await db.Moods.OrderBy(m => m.Id).Select(m => $"{m.Label}:{m.Archived}").ToListAsync();
            var helps = await db.UserHelps.Select(h => $"{h.Encrypted}:{h.Version}").ToListAsync();
            return string.Join("|", dayNotes.Concat(habitNotes).Concat(moods).Concat(helps));
        });

    [Theory]
    [InlineData("SetDayNote", "ConsentRequired")]
    [InlineData("SetNewDayNote", "ConsentRequired")]
    [InlineData("SetHabitNote", "ConsentRequired")]
    [InlineData("AddMood", "ConsentRequired")]
    [InlineData("EditMood", "ConsentRequired")]
    [InlineData("PutHelps", "ConsentRequired")]
    [InlineData("ClearDayNote", "Saved")]
    [InlineData("DeleteDayNote", "done")]
    [InlineData("DeleteHabitNote", "True")]
    [InlineData("DeleteHelps", "done")]
    public async Task WithoutConsent_EachJournalWrite_IsRefusedWritingNothing_OrAllowedAsPinned(string operation, string expected)
    {
        await using var host = await IntegrationTestHost.CreateAsync(start: false, time: new FakeTimeProvider(Now));
        var (buildId, _) = await SeedWithoutConsentAsync(host);
        var moodId = await SeedJournalAsync(host, buildId);
        var before = await JournalSnapshotAsync(host);

        var outcome = await host.WithScopeAsync(sp => JournalOperations[operation](sp.GetRequiredService<JournalService>(), buildId, moodId));

        outcome.Should().Be(expected);
        if (expected == "ConsentRequired") (await JournalSnapshotAsync(host)).Should().Be(before);
        else (await JournalSnapshotAsync(host)).Should().NotBe(before, "a deletion without consent still deletes");
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
            (await sp.GetRequiredService<AccountService>().GetUserForExportAsync(UserId)).Should().NotBeNull();
        });
    }
}
