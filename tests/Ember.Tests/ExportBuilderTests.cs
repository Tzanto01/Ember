using System.Text.Json;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;

namespace Ember.Tests;

public class ExportBuilderTests
{
    private static User SampleUser()
    {
        var user = new User
        {
            Id = TestUsers.Id(1), Logins = TestUsers.Discord(1),
            Timezone = "Europe/Berlin",
            GraceDaysPerWeek = 2,
            SecondReminderMinutes = 30,
            ShowStreaks = true,
            Language = "de",
            CalmMode = true,
            TipsSeen = ["streaks", "routines"],
            DmFailureCount = 3,
            DmRemindersPausedAt = new DateTime(2025, 6, 17, 9, 0, 0, DateTimeKind.Utc),
            DmPauseNoticeSentAt = new DateTime(2025, 6, 18, 7, 45, 0, DateTimeKind.Utc),
            HealthDataConsentAt = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            ConsentPolicyVersion = "2026-10",
            ConsentPromptedOn = new DateOnly(2025, 6, 1),
            ConsentWithdrawnAt = new DateTime(2025, 6, 2, 8, 0, 0, DateTimeKind.Utc),
            CreatedUtc = new DateTime(2025, 5, 30, 7, 0, 0, DateTimeKind.Utc),
        };

        var logged = HabitFactory.Make((1, true), (2, false));
        logged.Id = 10;
        logged.Name = "Read";
        logged.Logs.First().GuildId = 123456789012345678;

        var unlogged = HabitFactory.Make();
        unlogged.Id = 11;
        unlogged.Name = "Stretch";
        unlogged.ReminderTime = new TimeOnly(8, 30);
        unlogged.NextReminderUtc = new DateTime(2025, 6, 19, 6, 30, 0, DateTimeKind.Utc);
        unlogged.PausedFrom = new DateOnly(2025, 6, 17);
        unlogged.PausedUntil = new DateOnly(2025, 6, 20);

        user.Habits.Add(logged);
        user.Habits.Add(unlogged);
        user.DayMarks.Add(new DayMark { Date = new DateOnly(2025, 6, 16), Kind = DayMarkKind.LowDay });
        return user;
    }

    private static User UserWithV17Data()
    {
        var user = SampleUser();
        user.TwoFactorEnabled = true;
        var read = user.Habits.First();
        read.FrequencyType = FrequencyType.Weekdays;
        read.Days = ScheduleDays.Sun | ScheduleDays.Mon | ScheduleDays.Wed;
        read.TimesPerDay = 3;
        read.RoutineId = 7;
        read.RoutinePosition = 1;
        read.Logs.First().Count = 2;
        var stretch = user.Habits.Last();
        stretch.RoutineId = 7;
        stretch.RoutinePosition = 0;
        user.Routines.Add(new Routine
        {
            Id = 7, Name = "Morning", ReminderTime = new TimeOnly(7, 30),
            NextReminderUtc = new DateTime(2025, 6, 19, 5, 30, 0, DateTimeKind.Utc),
            ReminderExclusions = ["discord", "phone-4"],
            CreatedAt = new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        user.DayNotes.Add(new DayNote { Date = new DateOnly(2025, 6, 17), Text = "=cmd|' /C calc'!A0", Mood = "custom:5" });
        user.DayNotes.Add(new DayNote { Date = new DateOnly(2025, 6, 18), CipherVersion = 1, CipherIv = "aXY=", CipherText = "Y3Q=" });
        user.Moods.Add(new Mood { Id = 5, Label = "Proud", Emoji = "🦁", Archived = true, CreatedAt = new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc) });
        user.Moods.Add(new Mood { Id = 6, CipherVersion = 1, CipherIv = "bWl2", CipherText = "bWN0", CreatedAt = new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc) });
        user.JournalKey = new JournalKey
        {
            Version = 3, TurningOff = true,
            PassphraseSalt = "cHM=", PassphraseIterations = 600000, PassphraseIv = "cGl2", PassphraseWrapped = "cHc=",
            RecoverySalt = "cnM=", RecoveryIv = "cml2", RecoveryWrapped = "cnc=",
        };
        user.Helps = new UserHelps { Encrypted = "+aGVscHM=", Version = 2 };
        return user;
    }

    private static List<Dictionary<string, string>> Rows(string csv)
    {
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var header = lines[0].Split(',');
        lines.Skip(1).Should().OnlyContain(l => l.Split(',').Length == header.Length);
        return lines.Skip(1)
            .Select(l => header.Zip(l.Split(',')).ToDictionary(p => p.First, p => p.Second))
            .ToList();
    }

    [Fact]
    public void Csv_IncludesSettingsHabitsWithoutLogsAndGuildIds()
    {
        var rows = Rows(ExportBuilder.BuildCsv(SampleUser()));

        rows.Should().ContainSingle(r => r["record_type"] == "settings"
            && r["timezone"] == "Europe/Berlin" && r["grace_days_per_week"] == "2"
            && r["dm_failure_count"] == "3"
            && r["dm_reminders_paused_at"] == "2025-06-17T09:00:00Z"
            && r["dm_pause_notice_sent_at"] == "2025-06-18T07:45:00Z"
            && r["health_data_consent_at"] == "2025-06-01T12:00:00Z" && r["consent_policy_version"] == "2026-10"
            && r["consent_prompted_on"] == "2025-06-01" && r["consent_withdrawn_at"] == "2025-06-02T08:00:00Z"
            && r["account_created_at"] == "2025-05-30T07:00:00Z"
            && r["second_reminder_minutes"] == "30" && r["show_streaks"] == "true"
            && r["language"] == "de" && r["calm_mode"] == "true" && r["tips_seen"] == "streaks;routines");
        rows.Where(r => r["record_type"] == "habit").Select(r => r["habit_id"]).Should().BeEquivalentTo(["10", "11"]);
        rows.Should().ContainSingle(r => r["record_type"] == "habit" && r["habit_id"] == "11"
            && r["reminder_time"] == "08:30" && r["next_reminder_utc"] == "2025-06-19T06:30:00Z"
            && r["paused_from"] == "2025-06-17" && r["paused_until"] == "2025-06-20");
        rows.Where(r => r["record_type"] == "log").Should().HaveCount(2)
            .And.ContainSingle(r => r["guild_id"] == "123456789012345678");
        rows.Should().ContainSingle(r => r["record_type"] == "day_mark" && r["date"] == "2025-06-16" && r["kind"] == "low_day");
    }

    private static User UserWithLetGoHabit()
    {
        var user = SampleUser();
        var letGo = new Habit { Id = 12, Name = "Coffee", Kind = HabitKind.LetGo, CreatedAt = new DateTime(2025, 5, 1, 8, 0, 0, DateTimeKind.Utc) };
        letGo.Slips.Add(new HabitSlip { Date = new DateOnly(2025, 6, 10), Count = 2, Reasons = ["stressed", "bored"] });
        letGo.Slips.Add(new HabitSlip { Date = new DateOnly(2025, 6, 2), Count = 1, Reasons = [] });
        user.Habits.Add(letGo);
        return user;
    }

    [Fact]
    public void Export_CarriesTheHabitKindAndEachLetGoHabitsSlipsOldestFirst_InJsonAndCsv()
    {
        var user = UserWithLetGoHabit();

        var json = JsonElement.Parse(ExportBuilder.BuildJson(user));
        var rows = Rows(ExportBuilder.BuildCsv(user));

        json.GetProperty("habits").EnumerateArray().ToDictionary(h => h.GetProperty("id").GetInt32(), h => h.GetProperty("kind").GetString())
            .Should().BeEquivalentTo(new Dictionary<int, string?> { [10] = "build", [11] = "build", [12] = "let_go" });
        var slips = json.GetProperty("slips").EnumerateArray().ToList();
        slips.Select(sl => sl.GetProperty("date").GetString()).Should().Equal("2025-06-02", "2025-06-10");
        slips.Select(sl => sl.GetProperty("habit_id").GetInt32()).Should().OnlyContain(id => id == 12);
        slips[1].GetProperty("count").GetInt32().Should().Be(2);
        slips[1].GetProperty("reasons").EnumerateArray().Select(r => r.GetString()).Should().Equal("stressed", "bored");
        rows.Should().ContainSingle(r => r["record_type"] == "habit" && r["habit_id"] == "12" && r["kind"] == "let_go");
        rows.Should().ContainSingle(r => r["record_type"] == "slip" && r["habit_id"] == "12" && r["date"] == "2025-06-10"
            && r["count"] == "2" && r["reasons"] == "stressed;bored");
        rows.Count(r => r["record_type"] == "slip").Should().Be(2);
    }

    [Fact]
    public void Export_CarriesEachHabitsMadeItDaysOldestFirst_InFullAndDiscordJsonAndInCsv()
    {
        var user = UserWithLetGoHabit();
        var letGo = user.Habits.Single(h => h.Id == 12);
        letGo.MadeItDays.Add(new MadeItDay { Date = new DateOnly(2025, 6, 12) });
        letGo.MadeItDays.Add(new MadeItDay { Date = new DateOnly(2025, 6, 4) });

        var full = JsonElement.Parse(ExportBuilder.BuildJson(user));
        var discord = JsonElement.Parse(ExportBuilder.BuildJson(user, forDiscord: true));
        var rows = Rows(ExportBuilder.BuildCsv(user));

        foreach (var json in new[] { full, discord })
            json.GetProperty("made_it_days").EnumerateArray().Select(m => (m.GetProperty("habit_id").GetInt32(), m.GetProperty("date").GetString()))
                .Should().Equal((12, "2025-06-04"), (12, "2025-06-12"));
        rows.Where(r => r["record_type"] == "made_it").Select(r => (r["habit_id"], r["date"]))
            .Should().Equal(("12", "2025-06-04"), ("12", "2025-06-12"));
    }

    [Fact]
    public void Csv_NeutralizesFormulaInjectionInHabitNames()
    {
        var user = SampleUser();
        user.Habits.First().Name = "=HYPERLINK(\"x\")";
        user.Habits.Last().Name = "@SUM(1,2)";

        var csv = ExportBuilder.BuildCsv(user);

        csv.Should().Contain(",\"'=HYPERLINK(\"\"x\"\")\",");
        csv.Should().Contain(",\"'@SUM(1,2)\",");
        csv.Split("\r\n").Should().NotContain(line => line.Contains(",=") || line.Contains(",@") || line.Contains(",\"="));
    }

    [Fact]
    public void Csv_NeutralizesFormulasHiddenBehindLeadingWhitespace()
    {
        var user = SampleUser();
        user.Habits.First().Name = " =HYPERLINK(\"x\")";
        user.Habits.Last().Name = "\t\n+SUM(1)";

        var csv = ExportBuilder.BuildCsv(user);

        csv.Should().Contain(",\"' =HYPERLINK(\"\"x\"\")\",");
        csv.Should().Contain(",\"'\t\n+SUM(1)\",");
    }

    [Fact]
    public void Json_RoundTripsWithAllRecords()
    {
        var json = ExportBuilder.BuildJson(SampleUser());

        var doc = JsonSerializer.Deserialize<ExportBuilder.ExportDocument>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        doc.Should().NotBeNull();
        doc!.Settings.Should().BeEquivalentTo(new ExportBuilder.SettingsRecord("Europe/Berlin", 2, null, 3,
            new DateTime(2025, 6, 17, 9, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 18, 7, 45, 0, DateTimeKind.Utc),
            new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc), "2026-10", new DateOnly(2025, 6, 1), false, 30, true, "de", true, ["streaks", "routines"],
            ConsentWithdrawnAt: new DateTime(2025, 6, 2, 8, 0, 0, DateTimeKind.Utc)));
        doc.Habits.Should().HaveCount(2).And.Contain(h => h.Id == 11 && h.ReminderTime == new TimeOnly(8, 30)
            && h.NextReminderUtc == new DateTime(2025, 6, 19, 6, 30, 0, DateTimeKind.Utc));
        doc.Logs.Should().HaveCount(2).And.Contain(l => l.HabitId == 10 && l.GuildId == "123456789012345678");
        doc.DayMarks.Should().Equal(new ExportBuilder.DayMarkRecord(new DateOnly(2025, 6, 16), "low_day"));
    }

    [Fact]
    public void Json_RoundTripsSchedulesRoutinesJournalAndKey()
    {
        var json = ExportBuilder.BuildJson(UserWithV17Data());

        var doc = JsonSerializer.Deserialize<ExportBuilder.ExportDocument>(json,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!;

        doc.Settings.TwoFactorEnabled.Should().BeTrue();
        var read = doc.Habits.Single(h => h.Id == 10);
        read.Days.Should().Equal("mon", "wed", "sun");
        (read.TimesPerDay, read.RoutineId, read.RoutinePosition, read.Frequency).Should().Be((3, 7, 1, "weekdays"));
        var stretch = doc.Habits.Single(h => h.Id == 11);
        (stretch.Days, stretch.RoutinePosition).Should().Be((null, 0));
        doc.Logs.Single(l => l.HabitId == 10 && l.GuildId != null).Count.Should().Be(2);

        var routine = doc.Routines.Should().ContainSingle().Subject;
        routine.Should().BeEquivalentTo(new ExportBuilder.RoutineRecord(7, "Morning", new TimeOnly(7, 30),
            new DateTime(2025, 6, 19, 5, 30, 0, DateTimeKind.Utc), ["discord", "phone-4"],
            new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc)), o => o.WithStrictOrdering());

        doc.DayNotes.Should().BeEquivalentTo(new[]
        {
            new ExportBuilder.DayNoteRecord(new DateOnly(2025, 6, 17), "=cmd|' /C calc'!A0", "custom:5", null),
            new ExportBuilder.DayNoteRecord(new DateOnly(2025, 6, 18), null, null, new ExportBuilder.EncryptedRecord(1, "aXY=", "Y3Q=")),
        }, o => o.WithStrictOrdering());
        doc.Moods.Should().BeEquivalentTo(new[]
        {
            new ExportBuilder.MoodRecord(5, "Proud", "🦁", true, null, new DateTime(2025, 6, 2, 0, 0, 0, DateTimeKind.Utc)),
            new ExportBuilder.MoodRecord(6, null, null, false, new ExportBuilder.EncryptedRecord(1, "bWl2", "bWN0"),
                new DateTime(2025, 6, 3, 0, 0, 0, DateTimeKind.Utc)),
        }, o => o.WithStrictOrdering());
        doc.JournalKey.Should().BeEquivalentTo(new ExportBuilder.JournalKeyRecord(3, true,
            new ExportBuilder.PassphraseWrapRecord("cHM=", 600000, "cGl2", "cHc="), new ExportBuilder.RecoveryWrapRecord("cnM=", "cml2", "cnc=")));
        doc.HelpsEncrypted.Should().Be("+aGVscHM=");
        json.Should().StartWith("{\n  \"format_version\": 2,").And.NotContain("habit_ids");
        json.Should().Contain("\"journal_key\"").And.Contain("\"turning_off\"").And.Contain("\"ct\"").And.Contain("\"times_per_day\"");
    }

    [Fact]
    public void Json_HasNullJournalKeyWhenThePrivateJournalIsOff()
    {
        var json = ExportBuilder.BuildJson(SampleUser());

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("journal_key").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("helps_encrypted").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("habits")[0].GetProperty("routine_position").ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.GetProperty("routines").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Json_UsesTheFormatV2Names()
    {
        using var doc = JsonDocument.Parse(ExportBuilder.BuildJson(UserWithV17Data()));
        var root = doc.RootElement;
        static IEnumerable<string> Names(JsonElement e) => e.EnumerateObject().Select(p => p.Name);
        JsonElement First(string section, Func<JsonElement, bool>? where = null)
            => root.GetProperty(section).EnumerateArray().First(where ?? (_ => true));

        Names(root).First().Should().Be("format_version");
        Names(First("habits")).Should().Contain(["id", "days", "times_per_day", "routine_id", "routine_position"]).And.NotContain("habit_ids");
        Names(First("logs")).Should().Contain(["habit_id", "date", "completed", "count"]);
        Names(First("routines")).Should().Contain(["id", "name", "reminder_time", "reminder_exclusions"]).And.NotContain("habit_ids");
        Names(First("moods", m => m.GetProperty("label").ValueKind == JsonValueKind.String)).Should().Contain(["id", "label", "emoji", "archived"]);
        var encryptedMood = First("moods", m => m.GetProperty("encrypted").ValueKind == JsonValueKind.Object);
        Names(encryptedMood).Should().Contain(["id", "encrypted", "archived"]);
        Names(encryptedMood.GetProperty("encrypted")).Should().Equal("v", "iv", "ct");
        Names(First("day_notes")).Should().Contain(["date", "text", "mood", "encrypted"]);
        var key = root.GetProperty("journal_key");
        Names(key.GetProperty("passphrase")).Should().Equal("salt", "iterations", "iv", "wrapped");
        Names(key.GetProperty("recovery")).Should().Equal("salt", "iv", "wrapped");
    }

    [Fact]
    public void Csv_HasRowsForRoutinesDayNotesMoodsAndTheJournalKey()
    {
        var csv = ExportBuilder.BuildCsv(UserWithV17Data());
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("record_type,timezone,grace_days_per_week,theme,").And.Contain(",email,discord_id,account_created_at,two_factor_enabled,");
        var rows = Rows(csv);

        rows.Should().ContainSingle(r => r["record_type"] == "settings" && r["two_factor_enabled"] == "true");
        rows.Should().ContainSingle(r => r["record_type"] == "habit" && r["habit_id"] == "10"
            && r["days"] == "mon;wed;sun" && r["times_per_day"] == "3" && r["routine_id"] == "7" && r["routine_position"] == "1");
        rows.Should().ContainSingle(r => r["record_type"] == "log" && r["guild_id"] == "123456789012345678" && r["count"] == "2");
        rows.Should().ContainSingle(r => r["record_type"] == "routine" && r["routine_id"] == "7" && r["name"] == "Morning"
            && r["reminder_time"] == "07:30" && r["reminder_exclusions"] == "discord;phone-4" && r["routine_position"] == ""
            && r["created_at"] == "2025-06-01T08:00:00Z");
        rows.Should().ContainSingle(r => r["record_type"] == "day_note" && r["date"] == "2025-06-18"
            && r["text"] == "" && r["cipher_version"] == "1" && r["cipher_iv"] == "aXY=" && r["cipher_text"] == "Y3Q=");
        rows.Should().ContainSingle(r => r["record_type"] == "mood" && r["mood_id"] == "5" && r["label"] == "Proud"
            && r["emoji"] == "🦁" && r["archived"] == "true" && r["cipher_version"] == "");
        rows.Should().ContainSingle(r => r["record_type"] == "mood" && r["mood_id"] == "6" && r["cipher_text"] == "bWN0");
        rows.Should().ContainSingle(r => r["record_type"] == "journal_key" && r["version"] == "3" && r["turning_off"] == "true"
            && r["passphrase_salt"] == "cHM=" && r["passphrase_iterations"] == "600000" && r["passphrase_iv"] == "cGl2"
            && r["passphrase_wrapped"] == "cHc=" && r["recovery_salt"] == "cnM=" && r["recovery_iv"] == "cml2" && r["recovery_wrapped"] == "cnc=");
        rows.Should().ContainSingle(r => r["record_type"] == "helps" && r["helps_encrypted"] == "+aGVscHM=");
    }

    [Fact]
    public void Csv_NeutralizesFormulaInjectionInJournalText()
    {
        var csv = ExportBuilder.BuildCsv(UserWithV17Data());

        csv.Split("\r\n").Should().ContainSingle(l => l.StartsWith("day_note,"
            + string.Concat(Enumerable.Repeat(",", 19)) + "2025-06-17,")
            && l.Contains(",'=cmd|' /C calc'!A0,custom:5,"));
    }

    [Fact]
    public void Csv_WritesBase64CipherAndKeyColumnsVerbatim()
    {
        var user = UserWithV17Data();
        user.DayNotes.Last().CipherText = "+3Q=";
        user.JournalKey!.RecoveryWrapped = "-nc=";

        var rows = Rows(ExportBuilder.BuildCsv(user));

        rows.Should().ContainSingle(r => r["record_type"] == "day_note" && r["cipher_text"] == "+3Q=");
        rows.Should().ContainSingle(r => r["record_type"] == "journal_key" && r["recovery_wrapped"] == "-nc=");
    }

    [Fact]
    public void DiscordExport_LeavesOutJournalMoodsKeyEmailPushAndLogins()
    {
        var user = UserWithV17Data();
        user.Email = "someone@example.com";
        user.PushSubscriptions.Add(new PushSubscription { Id = 1, Endpoint = "https://push.example/abc", CreatedAt = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) });

        var rows = Rows(ExportBuilder.BuildCsv(user, forDiscord: true));
        using var json = JsonDocument.Parse(ExportBuilder.BuildJson(user, forDiscord: true));

        rows.Select(r => r["record_type"]).Distinct().Should().BeEquivalentTo(["settings", "habit", "log", "day_mark", "routine"]);
        rows.Should().OnlyContain(r => r["email"] == "" && r["discord_id"] == "");
        foreach (var key in new[] { "account", "push_subscriptions", "day_notes", "moods", "journal_key", "helps_encrypted" })
            json.RootElement.GetProperty(key).ValueKind.Should().Be(JsonValueKind.Null, key);
        json.RootElement.GetProperty("routines").GetArrayLength().Should().Be(1);
        json.RootElement.GetProperty("logs").GetArrayLength().Should().Be(2);
    }

    private static User UserWithHabitNotes()
    {
        var user = UserWithV17Data();
        user.OfferHabitNote = true;
        user.TodayOrder = ["r:7", "h:12"];
        var read = user.Habits.First();
        read.Notes.Add(new HabitNote { Date = new DateOnly(2025, 6, 18), CipherVersion = 1, CipherIv = "aXY=", CipherText = "+3Q=" });
        read.Notes.Add(new HabitNote { Date = new DateOnly(2025, 6, 17), Text = "=Felt slow" });
        return user;
    }

    [Fact]
    public void FullExport_HasHabitNotesOldestFirstAndTheNoteSettings_InJsonAndCsv()
    {
        var user = UserWithHabitNotes();

        using var json = JsonDocument.Parse(ExportBuilder.BuildJson(user));
        var rows = Rows(ExportBuilder.BuildCsv(user));

        var settings = json.RootElement.GetProperty("settings");
        settings.GetProperty("offer_habit_note").GetBoolean().Should().BeTrue();
        settings.GetProperty("today_order").EnumerateArray().Select(k => k.GetString()).Should().Equal("r:7", "h:12");
        var notes = json.RootElement.GetProperty("habit_notes").EnumerateArray().ToList();
        notes.Select(n => (n.GetProperty("habit_id").GetInt32(), n.GetProperty("date").GetString())).Should().Equal((10, "2025-06-17"), (10, "2025-06-18"));
        notes[0].GetProperty("text").GetString().Should().Be("=Felt slow");
        notes[0].GetProperty("encrypted").ValueKind.Should().Be(JsonValueKind.Null);
        notes[1].GetProperty("text").ValueKind.Should().Be(JsonValueKind.Null);
        notes[1].GetProperty("encrypted").GetProperty("ct").GetString().Should().Be("+3Q=");

        rows.Should().ContainSingle(r => r["record_type"] == "settings" && r["offer_habit_note"] == "true" && r["today_order"] == "r:7;h:12");
        rows.Should().ContainSingle(r => r["record_type"] == "habit_note" && r["habit_id"] == "10" && r["date"] == "2025-06-17"
            && r["text"] == "'=Felt slow" && r["cipher_version"] == "");
        rows.Should().ContainSingle(r => r["record_type"] == "habit_note" && r["date"] == "2025-06-18"
            && r["text"] == "" && r["cipher_version"] == "1" && r["cipher_iv"] == "aXY=" && r["cipher_text"] == "+3Q=");
    }

    [Fact]
    public void DiscordExport_LeavesOutHabitNotesAndTheTodayOrder()
    {
        var user = UserWithHabitNotes();

        using var json = JsonDocument.Parse(ExportBuilder.BuildJson(user, forDiscord: true));
        var rows = Rows(ExportBuilder.BuildCsv(user, forDiscord: true));

        json.RootElement.GetProperty("habit_notes").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("settings").GetProperty("today_order").ValueKind.Should().Be(JsonValueKind.Null);
        rows.Should().NotContain(r => r["record_type"] == "habit_note");
        rows.Should().ContainSingle(r => r["record_type"] == "settings" && r["today_order"] == "");
    }

    private static readonly Guid Picture1 = new("0190a1b2-c3d4-7e5f-8a9b-000000000001");
    private static readonly Guid Picture2 = new("0190a1b2-c3d4-7e5f-8a9b-000000000002");
    private static readonly Guid Picture3 = new("0190a1b2-c3d4-7e5f-8a9b-000000000003");

    internal static readonly Dictionary<Guid, byte[]> PictureBytes = new()
    {
        [Picture1] = [.. Enumerable.Range(0, 40_000).Select(i => (byte)(i * 7))],
        [Picture2] = [0xFF, 0xD8, 0xFF, 0x00, 0x22, 0x5C],
        [Picture3] = [0x52, 0x49, 0x46, 0x46],
    };

    /// <summary>Two linked reports (one with a formula-like title, one resolved) with pictures, and two sessions; <c>Data</c> only with <paramref name="withData"/>.</summary>
    internal static ExportBuilder.FeedbackAndSessions SampleFeedbackAndSessions(bool withData = false)
    {
        ExportBuilder.FeedbackImageRecord Image(Guid id, string type)
            => new(id, type, PictureBytes[id].Length, withData ? PictureBytes[id] : null);
        return new(
            [
                new(3, "web", "1.4.0", "=1+1", "Der \"Speichern\"-Knopf fehlt seit gestern", new DateTime(2025, 6, 3, 10, 0, 0, DateTimeKind.Utc), "open", null,
                    [Image(Picture1, "image/webp"), Image(Picture2, "image/jpeg")]),
                new(5, "android", null, "Dark mode", "Please", new DateTime(2025, 6, 5, 10, 0, 0, DateTimeKind.Utc), "released",
                    new DateTime(2025, 6, 9, 12, 30, 0, DateTimeKind.Utc), [Image(Picture3, "image/png")]),
            ],
            [
                new(new DateTime(2025, 6, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 15, 8, 0, 0, DateTimeKind.Utc),
                    new DateTime(2025, 6, 2, 9, 0, 0, DateTimeKind.Utc), "android", "1.4.0", true),
                new(new DateTime(2025, 6, 4, 8, 0, 0, DateTimeKind.Utc), new DateTime(2025, 6, 18, 8, 0, 0, DateTimeKind.Utc), null, null, null, false),
            ]);
    }

    [Fact]
    public async Task StreamedJson_IsByteIdenticalToBuildJson_FetchingEachPictureOnceInOrder()
    {
        var user = SampleUser();
        var fetched = new List<Guid>();
        using var stream = new MemoryStream();

        await ExportBuilder.WriteJsonAsync(stream, ExportBuilder.BuildDocument(user, feedbackAndSessions: SampleFeedbackAndSessions()),
            (id, _) => { fetched.Add(id); return Task.FromResult<byte[]?>(PictureBytes[id]); });

        stream.ToArray().Should().Equal(System.Text.Encoding.UTF8.GetBytes(
            ExportBuilder.BuildJson(user, feedbackAndSessions: SampleFeedbackAndSessions(withData: true))));
        fetched.Should().Equal(Picture1, Picture2, Picture3);
    }

    [Theory]
    [InlineData("no feedback")]
    [InlineData("a report without pictures")]
    [InlineData("a picture deleted before it is fetched")]
    [InlineData("an empty picture")]
    [InlineData("a picture over one base64 chunk")]
    [InlineData("a title with non-ASCII and escaped characters")]
    public async Task StreamedJson_IsByteIdenticalToBuildJson_For(string edgeCase)
    {
        var user = SampleUser();
        var at = new DateTime(2025, 6, 3, 10, 0, 0, DateTimeKind.Utc);
        ExportBuilder.FeedbackRecord Report(string title, params ExportBuilder.FeedbackImageRecord[] images)
            => new(7, "web", "1.4.0", title, "Text", at, "open", null, [.. images]);
        ExportBuilder.FeedbackImageRecord Image(byte[]? data) => new(Picture1, "image/webp", data?.Length ?? 3, data);
        List<ExportBuilder.FeedbackRecord> feedback = edgeCase switch
        {
            "no feedback" => [],
            "a report without pictures" => [Report("Crash")],
            "a picture deleted before it is fetched" => [Report("Crash", Image(null))],
            "an empty picture" => [Report("Crash", Image([]))],
            "a picture over one base64 chunk" => [Report("Crash", Image([.. Enumerable.Range(0, 150_001).Select(i => (byte)(i * 13))]))],
            "a title with non-ASCII and escaped characters" => [Report("Grüße \"quoted\" <b>&</b> \\ \n\t 😀 ✓", Image(PictureBytes[Picture2]))],
            _ => throw new ArgumentOutOfRangeException(nameof(edgeCase)),
        };
        var stored = feedback.SelectMany(r => r.Images).ToDictionary(i => i.Id, i => i.Data);
        var streamed = new ExportBuilder.FeedbackAndSessions([.. feedback.Select(r => r with { Images = [.. r.Images.Select(i => i with { Data = null })] })], []);
        using var stream = new MemoryStream();

        await ExportBuilder.WriteJsonAsync(stream, ExportBuilder.BuildDocument(user, feedbackAndSessions: streamed),
            (id, _) => Task.FromResult(stored[id]));

        System.Text.Encoding.UTF8.GetString(stream.ToArray())
            .Should().Be(ExportBuilder.BuildJson(user, feedbackAndSessions: new ExportBuilder.FeedbackAndSessions(feedback, [])));
    }

    [Fact]
    public async Task StreamedJson_OfTheDiscordExport_IsByteIdenticalToBuildJson_AndFetchesNoPicture()
    {
        var user = SampleUser();
        using var stream = new MemoryStream();

        await ExportBuilder.WriteJsonAsync(stream, ExportBuilder.BuildDocument(user, forDiscord: true, SampleFeedbackAndSessions()),
            (_, _) => throw new InvalidOperationException("The Discord export has no pictures."));

        System.Text.Encoding.UTF8.GetString(stream.ToArray()).Should().Be(ExportBuilder.BuildJson(user, forDiscord: true));
    }

    [Fact]
    public void Json_HoldsFeedbackWithBase64PicturesAndSessionsWithoutIds_AndTheDiscordExportHasNeitherSection()
    {
        var user = SampleUser();

        using var web = JsonDocument.Parse(ExportBuilder.BuildJson(user, feedbackAndSessions: SampleFeedbackAndSessions(withData: true)));
        using var discord = JsonDocument.Parse(ExportBuilder.BuildJson(user, forDiscord: true, SampleFeedbackAndSessions(withData: true)));

        var resolved = web.RootElement.GetProperty("feedback")[1];
        resolved.EnumerateObject().Select(p => p.Name).Should()
            .Equal("id", "platform", "version", "title", "text", "created_at", "status", "resolved_at", "images");
        resolved.GetProperty("status").GetString().Should().Be("released");
        resolved.GetProperty("resolved_at").GetString().Should().Be("2025-06-09T12:30:00Z");
        var picture = resolved.GetProperty("images")[0];
        picture.EnumerateObject().Select(p => p.Name).Should().Equal("id", "content_type", "size", "data");
        picture.GetProperty("id").GetGuid().Should().Be(Picture3);
        picture.GetProperty("data").GetBytesFromBase64().Should().Equal(PictureBytes[Picture3]);
        web.RootElement.GetProperty("sessions")[0].EnumerateObject().Select(p => p.Name).Should()
            .Equal("created_at", "expires_at", "last_seen_at", "platform", "app_version", "widget_key_set");
        discord.RootElement.TryGetProperty("feedback", out _).Should().BeFalse();
        discord.RootElement.TryGetProperty("sessions", out _).Should().BeFalse();
    }

    [Fact]
    public void Csv_HasFeedbackPictureAndSessionRows_WithoutPictureBytes_AndGuardsAFormulaTitle()
    {
        var csv = ExportBuilder.BuildCsv(SampleUser(), feedbackAndSessions: SampleFeedbackAndSessions(withData: true));
        var rows = Rows(csv);

        rows.Where(r => r["record_type"] == "feedback").Select(r => (r["feedback_id"], r["platform"], r["app_version"], r["title"], r["text"],
                r["created_at"], r["status"], r["resolved_at"]))
            .Should().Equal(("3", "web", "1.4.0", "'=1+1", "\"Der \"\"Speichern\"\"-Knopf fehlt seit gestern\"", "2025-06-03T10:00:00Z", "open", ""),
                ("5", "android", "", "Dark mode", "Please", "2025-06-05T10:00:00Z", "released", "2025-06-09T12:30:00Z"));
        rows.Where(r => r["record_type"] == "feedback_image").Select(r => (r["feedback_id"], r["image_id"], r["content_type"], r["size"]))
            .Should().Equal(("3", Picture1.ToString(), "image/webp", "40000"), ("3", Picture2.ToString(), "image/jpeg", "6"),
                ("5", Picture3.ToString(), "image/png", "4"));
        rows.Where(r => r["record_type"] == "session").Select(r => (r["created_at"], r["expires_at"], r["last_seen_at"], r["platform"],
                r["app_version"], r["widget_key_set"]))
            .Should().Equal(("2025-06-01T08:00:00Z", "2025-06-15T08:00:00Z", "2025-06-02T09:00:00Z", "android", "1.4.0", "true"),
                ("2025-06-04T08:00:00Z", "2025-06-18T08:00:00Z", "", "", "", "false"));
        PictureBytes.Values.Should().OnlyContain(bytes => !csv.Contains(Convert.ToBase64String(bytes)));
    }
}
