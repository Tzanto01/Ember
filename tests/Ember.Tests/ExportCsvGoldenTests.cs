using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

/// <summary>The CSV export is a file format users keep and re-import elsewhere: header, column order and row layout are the contract.</summary>
public class ExportCsvGoldenTests
{
    private static User FullUser()
    {
        var user = new User
        {
            Id = TestUsers.Id(1),
            Logins = TestUsers.Discord(1),
            Email = "sam@example.test",
            Timezone = "Europe/Berlin",
            GraceDaysPerWeek = 2,
            Theme = "tidepool",
            SecondReminderMinutes = 30,
            ShowStreaks = true,
            Language = "de",
            CalmMode = true,
            OfferHabitNote = true,
            TipsSeen = ["streaks", "routines"],
            TodayOrder = ["r:7", "h:10"],
            FiresideEnabled = true,
            FiresideName = "Sam",
            FiresideNotify = true,
            TwoFactorEnabled = true,
            DmFailureCount = 3,
            DmRemindersPausedAt = new DateTime(2027, 1, 17, 9, 0, 0, DateTimeKind.Utc),
            HealthDataConsentAt = new DateTime(2027, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            ConsentPolicyVersion = "2026-10",
            ConsentPromptedOn = new DateOnly(2027, 1, 1),
            CreatedUtc = new DateTime(2026, 12, 30, 7, 0, 0, DateTimeKind.Utc),
        };

        var read = new Habit
        {
            Id = 10,
            Name = "Read",
            FrequencyType = FrequencyType.Weekdays,
            Days = ScheduleDays.Mon | ScheduleDays.Wed | ScheduleDays.Sun,
            TimesPerDay = 2,
            TimerMinutes = 30,
            ReminderTimes = [new TimeOnly(8, 30), new TimeOnly(20, 0)],
            ReminderExclusions = ["discord"],
            RoutineId = 7,
            RoutinePosition = 1,
            CreatedAt = new DateTime(2027, 1, 2, 8, 0, 0, DateTimeKind.Utc),
            NextReminderUtc = new DateTime(2027, 1, 19, 7, 30, 0, DateTimeKind.Utc),
            PausedFrom = new DateOnly(2027, 1, 17),
            PausedUntil = new DateOnly(2027, 1, 20),
        };
        read.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 14), Completed = true, Count = 2, GuildId = 123456789012345678 });
        read.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 15), Completed = false, Count = 1 });
        read.Logs.Add(new HabitLog { Date = new DateOnly(2027, 1, 16), Completed = false, Count = 0, Skipped = true });
        read.Notes.Add(new HabitNote { Date = new DateOnly(2027, 1, 14), Text = "Two chapters" });
        read.Notes.Add(new HabitNote { Date = new DateOnly(2027, 1, 15), CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });

        var weekly = new Habit { Id = 11, Name = "Run, slowly", FrequencyType = FrequencyType.Weekly, WeeklyTarget = 3, CreatedAt = new DateTime(2027, 1, 3, 8, 0, 0, DateTimeKind.Utc) };
        var letGo = new Habit { Id = 12, Name = "Coffee", Kind = HabitKind.LetGo, CreatedAt = new DateTime(2027, 1, 4, 8, 0, 0, DateTimeKind.Utc) };
        letGo.Slips.Add(new HabitSlip { Date = new DateOnly(2027, 1, 10), Count = 2, Reasons = ["stress", "habit"] });
        letGo.Slips.Add(new HabitSlip { Date = new DateOnly(2027, 1, 12), Count = 1, Reasons = [] });
        user.Habits.Add(read);
        user.Habits.Add(weekly);
        user.Habits.Add(letGo);

        user.DayMarks.Add(new DayMark { Date = new DateOnly(2027, 1, 13), Kind = DayMarkKind.LowDay });
        user.PushSubscriptions.Add(new Ember.Core.Models.PushSubscription
        { Endpoint = "https://push.example.test/abc", P256dh = "k", Auth = "a", Label = "Laptop", CreatedAt = new DateTime(2027, 1, 5, 0, 0, 0, DateTimeKind.Utc) });
        user.ReminderPhones.Add(new ReminderPhone { InstallId = "install-1", Label = "Pixel", CreatedAt = new DateTime(2027, 1, 6, 0, 0, 0, DateTimeKind.Utc) });
        user.Routines.Add(new Routine
        {
            Id = 7,
            Name = "Morning",
            ReminderTime = new TimeOnly(7, 30),
            NextReminderUtc = new DateTime(2027, 1, 19, 6, 30, 0, DateTimeKind.Utc),
            ReminderExclusions = ["phone-4"],
            CreatedAt = new DateTime(2027, 1, 2, 8, 0, 0, DateTimeKind.Utc),
        });
        user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 13), Text = "=cmd|' /C calc'!A0", Mood = "custom:5" });
        user.DayNotes.Add(new DayNote { Date = new DateOnly(2027, 1, 14), CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });
        user.Moods.Add(new Mood { Id = 5, Label = "Proud", Emoji = "🦁", Archived = true, CreatedAt = new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        user.Moods.Add(new Mood { Id = 6, CipherVersion = 1, CipherIv = "bWl2", CipherText = "bWN0", CreatedAt = new DateTime(2027, 1, 3, 0, 0, 0, DateTimeKind.Utc) });
        user.JournalKey = new JournalKey
        {
            Version = 3,
            TurningOff = true,
            PassphraseSalt = "cHM=",
            PassphraseIterations = 600000,
            PassphraseIv = "cGl2",
            PassphraseWrapped = "cHc=",
            RecoverySalt = "cnM=",
            RecoveryIv = "cml2",
            RecoveryWrapped = "cnc=",
        };
        user.Helps = new UserHelps { Encrypted = "+aGVscHM=", Version = 2 };
        return user;
    }

    private static string WithCrlfRows(string csv)
    {
        csv.Should().EndWith("\r\n");
        csv.Replace("\r\n", "").Should().NotContain("\n", "every CSV row ends in CRLF");
        return csv;
    }

    [Fact]
    public void FullCsv_KeepsEveryColumnInOrderAndEveryRowValue_ButMayGrow()
        => GoldenFile.AssertCsvKeeps("export-full.csv",
            WithCrlfRows(ExportBuilder.BuildCsv(FullUser(), feedbackAndSessions: ExportBuilderTests.SampleFeedbackAndSessions(withData: true))));

    [Fact]
    public void DiscordCsv_KeepsEveryColumnInOrderAndEveryRowValue_ButMayGrow()
        => GoldenFile.AssertCsvKeeps("export-discord.csv",
            WithCrlfRows(ExportBuilder.BuildCsv(FullUser(), forDiscord: true, ExportBuilderTests.SampleFeedbackAndSessions(withData: true))));
}
