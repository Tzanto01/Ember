using System.Globalization;
using Ember.Bot;
using Ember.Core.Data;
using Ember.Core.Data.Migrations;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Ember.Tests;

[Collection("Postgres")]
public class PostgresMigrationTests(PostgresFixture postgres)
{
    [DockerFact]
    public async Task InitializeAsync_AppliesAllMigrationsAndMatchesModel()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        using var host = AppHost.CreateHostBuilder([], new EmberHostOptions
        {
            IncludeDiscordGateway = false,
            ConfigureDbContext = (options, _) => options.UseNpgsql(connectionString)
        }).Build();

        await AppHost.InitializeAsync(host);

        int habitId;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmberDbContext>();
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            db.Database.HasPendingModelChanges().Should().BeFalse();

            var sp = scope.ServiceProvider;
            var service = sp.GetRequiredService<HabitService>();
            await sp.GetRequiredService<ConsentService>().GiveHealthDataConsentAsync(await TestUsers.ResolveOrCreateAsync(sp, 4242));
            var habit = (await service.AddHabitAsync(await TestUsers.UserIdAsync(sp, 4242), "Stretch", new TimeOnly(7, 30))).Habit!;
            habitId = habit.Id;
            (await service.CheckInAsync(await TestUsers.UserIdAsync(sp, 4242), habitId, await sp.GetRequiredService<UserSettingsService>().GetLocalTodayAsync(await TestUsers.UserIdAsync(sp, 4242)), completed: true, guildId: 99))
                .Should().Be(DayEditResult.Saved);
        }

        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<EmberDbContext>();
            var stored = await db.Habits.Include(h => h.User).ThenInclude(u => u.Logins).Include(h => h.Logs).SingleAsync(h => h.Id == habitId);

            stored.User.DiscordId().Should().Be(4242UL);
            stored.Name.Should().Be("Stretch");
            stored.ReminderTime.Should().Be(new TimeOnly(7, 30));
            stored.Logs.Should().ContainSingle(l => l.Completed && l.GuildId == 99);
        }
    }

    [DockerFact]
    public async Task FeaturesV16Migration_KeepsHabitsAndLogs_AndCountsCompletedDaysOnce()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261102120000_AddUserTheme");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"LeaderboardOptIn\", \"DmFailureCount\", " +
            "\"EmailConfirmed\", \"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\") " +
            "VALUES ('00000000-0000-0000-0000-000000000001', 'Europe/Berlin', 1, TRUE, 0, FALSE, FALSE, FALSE, FALSE, 0);" +
            "INSERT INTO \"Habits\" (\"Id\", \"UserId\", \"Name\", \"CreatedAt\", \"FrequencyType\", \"WeeklyTarget\", \"ReminderChannels\") VALUES " +
            "(1, '00000000-0000-0000-0000-000000000001', 'Stretch', now(), 0, NULL, 1), " +
            "(2, '00000000-0000-0000-0000-000000000001', 'Read', now(), 1, 3, 1);" +
            "INSERT INTO \"HabitLogs\" (\"HabitId\", \"Date\", \"Completed\") VALUES " +
            "(1, '2026-10-01', TRUE), (1, '2026-10-02', FALSE), (2, '2026-10-01', TRUE)");

        await migrator.MigrateAsync();

        var habits = await db.Habits.AsNoTracking().Include(h => h.Logs).OrderBy(h => h.Id).ToListAsync();
        habits.Select(h => h.FrequencyType).Should().Equal(FrequencyType.Daily, FrequencyType.Weekly);
        habits[1].WeeklyTarget.Should().Be(3);
        habits.Should().OnlyContain(h => h.TimesPerDay == 1 && h.Days == null && h.RoutineId == null);
        habits[0].Logs.OrderBy(l => l.Date).Select(l => (l.Completed, l.Count)).Should().Equal((true, 1), (false, 0));
        habits[1].Logs.Should().ContainSingle(l => l.Completed && l.Count == 1);
    }

    [DockerFact]
    public async Task PausedFromMigration_StartsRunningPausesOnTheMigrationDate_AndEmptiesEndedPauses()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261013073035_AddDayMarks");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Users\" (\"DiscordUserId\", \"Timezone\", \"GraceDaysPerWeek\", \"LeaderboardOptIn\") VALUES (1, 'UTC', 1, FALSE);" +
            "INSERT INTO \"Habits\" (\"UserId\", \"Name\", \"CreatedAt\", \"FrequencyType\", \"PausedUntil\") VALUES " +
            "(1, 'Paused', now(), 0, '2999-01-01'), (1, 'Ended', now(), 0, '2020-01-10'), (1, 'Active', now(), 0, NULL)");

        var before = await CurrentDateAsync(db);
        await migrator.MigrateAsync();
        var after = await CurrentDateAsync(db);

        var habits = await db.Habits.AsNoTracking().OrderBy(h => h.Name).ToListAsync();
        habits.Select(h => h.Name).Should().Equal("Active", "Ended", "Paused");
        habits[0].PausedFrom.Should().BeNull();
        habits[1].PausedFrom.Should().Be(new DateOnly(2020, 1, 11));
        habits[1].PausedUntil.Should().Be(new DateOnly(2020, 1, 10));
        HabitRules.IsPausedOn(habits[1], new DateOnly(2020, 1, 1)).Should().BeFalse("an ended pause no longer hides earlier history");
        habits[2].PausedFrom.Should().NotBeNull();
        habits[2].PausedFrom!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [DockerFact]
    public async Task HabitReminderTimesMigration_CarriesEachReminderTimeOver()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261111163159_PushSubscriptionSession");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\") " +
            "VALUES ('00000000-0000-0000-0000-000000000001', 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0);" +
            "INSERT INTO \"Habits\" (\"Id\", \"UserId\", \"Name\", \"ReminderTime\", \"CreatedAt\", \"FrequencyType\", \"ReminderChannels\", \"RoutinePosition\") VALUES " +
            "(1, '00000000-0000-0000-0000-000000000001', 'Stretch', '07:30', now(), 0, 1, 0), " +
            "(2, '00000000-0000-0000-0000-000000000001', 'Read', NULL, now(), 0, 1, 0)");

        await migrator.MigrateAsync();

        var habits = await db.Habits.AsNoTracking().OrderBy(h => h.Id).ToListAsync();
        habits[0].ReminderTimes.Should().Equal(new TimeOnly(7, 30));
        habits[1].ReminderTimes.Should().BeEmpty();
    }

    [DockerFact]
    public async Task MultiDeviceRemindersMigration_StartsEverythingEverywhere_KeepsTodaysDiscordUsersAsYes_AndGoesBackDown()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261112001345_AddFeedback");
        string Id(int n) => $"'00000000-0000-0000-0000-00000000000{n}'";
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\") VALUES " +
            string.Join(", ", Enumerable.Range(1, 4).Select(n => $"({Id(n)}, 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0)")) + ";" +
            "INSERT INTO \"AspNetUserLogins\" (\"LoginProvider\", \"ProviderKey\", \"ProviderDisplayName\", \"UserId\") VALUES " +
            $"('Discord', '101', 'Discord', {Id(1)}), ('Discord', '102', 'Discord', {Id(2)}), ('Discord', '103', 'Discord', {Id(3)});" +
            "INSERT INTO \"Habits\" (\"Id\", \"UserId\", \"Name\", \"ReminderTimes\", \"CreatedAt\", \"FrequencyType\", \"ReminderChannels\", \"RoutinePosition\") VALUES " +
            $"(1, {Id(1)}, 'Stretch', ARRAY['07:30'::time], now(), 0, 3, 0), " +
            $"(2, {Id(2)}, 'Read', ARRAY[]::time[], now(), 0, 1, 0), " +
            $"(3, {Id(4)}, 'Walk', ARRAY['09:00'::time], now(), 0, 1, 0);" +
            "INSERT INTO \"Routines\" (\"Id\", \"UserId\", \"Name\", \"ReminderTime\", \"CreatedAt\", \"ReminderChannels\") VALUES " +
            $"(1, {Id(2)}, 'Evening', '21:00', now(), 2), (2, {Id(3)}, 'Morning', '07:00', now(), 1);" +
            "INSERT INTO \"PushSubscriptions\" (\"UserId\", \"Endpoint\", \"P256dh\", \"Auth\", \"CreatedAt\") VALUES " +
            $"({Id(1)}, 'https://fcm.googleapis.com/fcm/send/a', 'k', 'a', now())");

        await migrator.MigrateAsync();

        (await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.DiscordReminders).ToListAsync()).Should().Equal(true, null, true, null);
        (await db.Habits.AsNoTracking().ToListAsync()).Should().OnlyContain(h => h.ReminderExclusions.Count == 0);
        (await db.Routines.AsNoTracking().ToListAsync()).Should().OnlyContain(r => r.ReminderExclusions.Count == 0);
        (await db.PushSubscriptions.AsNoTracking().SingleAsync()).Should().Match<Ember.Core.Models.PushSubscription>(s => s.Enabled && s.Label == null);

        await db.Habits.Where(h => h.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(h => h.ReminderExclusions, new List<string> { "discord" }));
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO \"Habits\" (\"Id\", \"UserId\", \"Name\", \"ReminderTimes\", \"CreatedAt\", \"FrequencyType\", \"RoutinePosition\") VALUES (4, {Id(1)}, 'Water', ARRAY[]::time[], now(), 0, 0);" +
            $"INSERT INTO \"ReminderPhones\" (\"UserId\", \"InstallId\", \"Label\", \"Enabled\", \"CreatedAt\") VALUES ({Id(1)}, 'install-0000000001', 'Pixel 8', TRUE, now());" +
            $"INSERT INTO \"PushReminders\" (\"UserId\", \"HabitId\", \"Date\") VALUES ({Id(1)}, 4, '2027-01-15')");

        await migrator.MigrateAsync("20261112001345_AddFeedback");

        (await db.Database.SqlQueryRaw<int>("SELECT \"ReminderChannels\" AS \"Value\" FROM \"Habits\" ORDER BY \"Id\"").ToListAsync())
            .Should().Equal(6, 6, 6, 7);
        (await db.Database.SqlQueryRaw<int>("SELECT \"ReminderChannels\" AS \"Value\" FROM \"Routines\" ORDER BY \"Id\"").ToListAsync())
            .Should().Equal(6, 7);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name IN ('ReminderPhones', 'PushReminders')").ToListAsync())
            .Should().Equal(0);
    }

    [DockerFact]
    public async Task FeedbackTitleMigration_GivesEachExistingReportATitleFromItsFirstLine()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261112001345_AddFeedback");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Feedback\" (\"Platform\", \"Text\", \"CreatedAt\") VALUES " +
            "('web', E'Dark mode please\r\nIt hurts my eyes at night', now()), " +
            "('android', E'one line', now()), " +
            "('web', repeat('x', 150), now())");

        await migrator.MigrateAsync();

        var stored = await db.Feedback.AsNoTracking().OrderBy(f => f.Id).ToListAsync();
        stored.Select(f => f.Title).Should().Equal("Dark mode please", "one line", new string('x', 100));
        stored.Select(f => f.Text).Should().Equal("Dark mode please\r\nIt hurts my eyes at night", "one line", new string('x', 150));
    }

    [DockerFact]
    public async Task ShowStreaksMigration_KeepsStreaksOnForExistingAccounts_NewAccountsStartWithThemOff()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261112101409_AddFeedbackTitleAndImages");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\") " +
            "VALUES ('00000000-0000-0000-0000-000000000001', 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0)");

        await migrator.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\") " +
            "VALUES ('00000000-0000-0000-0000-000000000002', 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0)");

        (await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.ShowStreaks).ToListAsync()).Should().Equal(true, false);

        await migrator.MigrateAsync("20261112101409_AddFeedbackTitleAndImages");
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE column_name = 'ShowStreaks'").ToListAsync())
            .Should().Equal(0);
    }

    [DockerFact]
    public async Task LanguageCalmModeAndHelpsMigration_GivesExistingAccountsEnglishAndCalmModeOff_AndGoesBackDown()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261113125055_AddShowStreaks";

        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\") " +
            "VALUES ('00000000-0000-0000-0000-000000000001', 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0)");

        await migrator.MigrateAsync();
        var user = await db.Users.AsNoTracking().SingleAsync();
        (user.Language, user.CalmMode).Should().Be(("en", false));
        db.UserHelps.Add(new UserHelps { UserId = user.Id, Encrypted = "ciphertext", Version = 1, UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync(previous);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_name = 'AspNetUsers' AND column_name IN ('Language', 'CalmMode')").ToListAsync()).Should().Equal(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.tables WHERE table_name = 'UserHelps'")
            .ToListAsync()).Should().Equal(0);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM \"AspNetUsers\"").ToListAsync()).Should().Equal(1);
    }

    [DockerFact]
    public async Task TipsSeenMigration_MarksStreaksSeenForExistingAccounts_NewAccountsStartEmpty_AndGoesBackDown()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();
        const string previous = "20261113152127_AddLanguageCalmModeAndHelps";
        const string insert =
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"SecondReminderMinutes\", " +
            "\"ShowStreaks\", \"Language\", \"CalmMode\") VALUES ({0}, 'Europe/Berlin', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, 0, FALSE, 'en', FALSE)";

        await migrator.MigrateAsync(previous);
        await db.Database.ExecuteSqlRawAsync(insert, Guid.Parse("00000000-0000-0000-0000-000000000001"));

        await migrator.MigrateAsync();
        await db.Database.ExecuteSqlRawAsync(insert, Guid.Parse("00000000-0000-0000-0000-000000000002"));
        db.Users.Add(new User { Id = Guid.Parse("00000000-0000-0000-0000-000000000003") });
        await db.SaveChangesAsync();

        (await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.TipsSeen).ToListAsync())
            .Should().BeEquivalentTo(new List<string>[] { ["streaks"], [], [] }, o => o.WithStrictOrdering());

        await migrator.MigrateAsync(previous);
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_name = 'AspNetUsers' AND column_name = 'TipsSeen'").ToListAsync()).Should().Equal(0);
    }

    [DockerFact]
    public async Task FeedbackStatusMigration_MarksResolvedReportsReleased_OthersOpen()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261114114442_AddTipsSeen");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Feedback\" (\"Platform\", \"Title\", \"Text\", \"CreatedAt\", \"ResolvedAt\") VALUES " +
            "('web', 'handled', 'handled', now(), now()), ('web', 'pending', 'pending', now(), NULL)");

        await migrator.MigrateAsync("20261114234929_AddFeedbackStatus");

        (await db.Database.SqlQueryRaw<string>("SELECT \"Status\" AS \"Value\" FROM \"Feedback\" ORDER BY \"Id\"").ToListAsync())
            .Should().Equal("released", "open");
    }

    [DockerFact]
    public async Task FeedbackIntakeMigration_CountsTheReportsOfTheLast24HoursWithTheirPictureBytes()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261121062801_AddCheckInSkipped");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Feedback" ("Id", "Platform", "Title", "Text", "CreatedAt") VALUES
                (1, 'web', 'today', 'today', now() - interval '2 hours'), (2, 'web', 'plain', 'plain', now() - interval '3 hours'),
                (3, 'web', 'older', 'older', now() - interval '25 hours');
            INSERT INTO "FeedbackImages" ("Id", "FeedbackId", "ContentType", "Size", "Data") VALUES
                (gen_random_uuid(), 1, 'image/png', 3, '\x010203'), (gen_random_uuid(), 1, 'image/png', 2, '\x0102'),
                (gen_random_uuid(), 3, 'image/png', 1, '\x01');
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        (await db.FeedbackIntake.AsNoTracking().OrderByDescending(i => i.CreatedAt).Select(i => i.ImageBytes).ToListAsync()).Should().Equal(5, 0);
    }

    [DockerFact]
    public async Task DroppingTheFeedbackBoardColumns_KeepsEveryReportItsPicturesAndItsOutboxRow_InEveryDeliveryState()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        await db.GetService<IMigrator>().MigrateAsync("20261121062801_AddCheckInSkipped");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUsers" ("Id", "Timezone", "GraceDaysPerWeek", "DmFailureCount", "EmailConfirmed", "PhoneNumberConfirmed",
                "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            VALUES ('00000000-0000-0000-0000-000000000001', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0);
            INSERT INTO "LookoutUserRefs" ("Ref", "UserId", "CreatedAt") VALUES ('synthetic-ref-1', '00000000-0000-0000-0000-000000000001', now());
            INSERT INTO "Feedback" ("Id", "UserId", "Platform", "Version", "Title", "Text", "CreatedAt", "Status", "ResolvedAt", "DigestedAt") VALUES
                (1, '00000000-0000-0000-0000-000000000001', 'web', '1.0', 'linked sent', 'a', now() - interval '40 days', 'released', now() - interval '35 days', now()),
                (2, '00000000-0000-0000-0000-000000000001', 'android', NULL, 'linked unsent', 'b', now() - interval '2 days', 'open', NULL, NULL),
                (3, NULL, 'discord', NULL, 'anonymous sent', 'c', now() - interval '3 days', 'planned', NULL, now()),
                (4, NULL, 'web', '1.0', 'anonymous parked', 'd', now() - interval '4 days', 'declined', now(), now()),
                (5, NULL, 'web', '1.0', 'never queued', 'e', now() - interval '1 hour', 'implemented', NULL, now());
            INSERT INTO "FeedbackImages" ("Id", "FeedbackId", "ContentType", "Size", "Data")
                SELECT gen_random_uuid(), id, 'image/png', 3, '\x010203' FROM generate_series(1, 5) id;
            INSERT INTO "LookoutOutbox" ("Kind", "Key", "FeedbackId", "UserRef", "CreatedAt", "SentAt", "ParkedAt", "ParkedStatus") VALUES
                ('ticket', 'key-1', 1, 'synthetic-ref-1', now(), now(), NULL, NULL), ('ticket', 'key-2', 2, 'synthetic-ref-1', now(), NULL, NULL, NULL),
                ('ticket', 'key-3', 3, NULL, now(), now(), NULL, NULL), ('ticket', 'key-4', 4, NULL, now(), NULL, now(), 400),
                ('user-delete', NULL, NULL, 'synthetic-ref-0', now(), NULL, NULL, NULL);
            """);

        await db.GetService<IMigrator>().MigrateAsync();

        (await db.Feedback.AsNoTracking().OrderBy(f => f.Id).Select(f => new { f.Title, f.Text, f.Platform, Linked = f.UserId != null, Pictures = f.Images.Count }).ToListAsync())
            .Select(f => (f.Title, f.Text, f.Platform, f.Linked, f.Pictures)).Should().Equal(
                ("linked sent", "a", "web", true, 1), ("linked unsent", "b", "android", true, 1), ("anonymous sent", "c", "discord", false, 1),
                ("anonymous parked", "d", "web", false, 1), ("never queued", "e", "web", false, 1));
        (await db.LookoutOutbox.AsNoTracking().OrderBy(o => o.Id).ToListAsync())
            .Select(o => (o.Key, o.FeedbackId, o.UserRef, Sent: o.SentAt != null, o.ParkedStatus)).Should().Equal(
                ("key-1", 1, "synthetic-ref-1", true, null), ("key-2", 2, "synthetic-ref-1", false, null), ("key-3", 3, null, true, null),
                ("key-4", 4, null, false, 400), (null, null, "synthetic-ref-0", false, null));
        (await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns " +
            "WHERE table_name = 'Feedback' AND column_name IN ('Status', 'ResolvedAt', 'DigestedAt')").ToListAsync()).Should().Equal(0);
    }

    [DockerFact]
    public async Task RoutinePositionMigration_NumbersEachUsersRoutinesOldestFirst()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261118120000_AddPushShowNames");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\") VALUES " +
            "('00000000-0000-0000-0000-000000000001', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0), " +
            "('00000000-0000-0000-0000-000000000002', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0);" +
            "INSERT INTO \"Routines\" (\"Id\", \"UserId\", \"Name\", \"CreatedAt\", \"ReminderExclusions\") VALUES " +
            "(1, '00000000-0000-0000-0000-000000000001', 'Newest', '2026-11-03', ARRAY[]::text[]), " +
            "(2, '00000000-0000-0000-0000-000000000002', 'Other', '2026-11-02', ARRAY[]::text[]), " +
            "(3, '00000000-0000-0000-0000-000000000001', 'Oldest', '2026-11-01', ARRAY[]::text[]), " +
            "(4, '00000000-0000-0000-0000-000000000001', 'Middle', '2026-11-02', ARRAY[]::text[])");

        await migrator.MigrateAsync();

        (await db.Routines.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Position).ToListAsync()).Should().Equal(2, 0, 0, 1);
    }

    [DockerFact]
    public async Task ConsentWithdrawalMigration_WithdrawsOnlyUsersWithHealthDataWhoNeverAgreed_AndTurnsFiresideOffForEveryone()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();

        await migrator.MigrateAsync("20261119070014_AddWidgetKey");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"AspNetUsers\" (\"Id\", \"Timezone\", \"GraceDaysPerWeek\", \"DmFailureCount\", \"EmailConfirmed\", " +
            "\"PhoneNumberConfirmed\", \"TwoFactorEnabled\", \"LockoutEnabled\", \"AccessFailedCount\", \"HealthDataConsentAt\", \"ConsentPolicyVersion\") VALUES " +
            "('00000000-0000-0000-0000-000000000001', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, NULL, NULL), " +
            "('00000000-0000-0000-0000-000000000002', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, NULL, NULL), " +
            "('00000000-0000-0000-0000-000000000003', 'UTC', 1, 0, FALSE, FALSE, FALSE, FALSE, 0, '2026-11-01', '2026-11');" +
            "INSERT INTO \"DayMarks\" (\"UserId\", \"Date\", \"Kind\") VALUES " +
            "('00000000-0000-0000-0000-000000000001', '2026-11-10', 0), ('00000000-0000-0000-0000-000000000003', '2026-11-10', 0);" +
            "UPDATE \"AspNetUsers\" SET \"FiresideEnabled\" = TRUE, \"FiresideName\" = 'Fi', \"WarmthAskUntilUtc\" = now() + interval '1 hour' " +
            "WHERE \"Id\" IN ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000003');" +
            "INSERT INTO \"FiresideLinks\" (\"OwnerId\", \"FriendId\", \"Muted\", \"ShareFlame\", \"KnownName\", \"JoinedViaMyCode\", \"JoinNotifyPending\", \"CreatedAt\") VALUES " +
            "('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000003', FALSE, TRUE, 'Fi', FALSE, FALSE, now()), " +
            "('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001', FALSE, TRUE, 'Fi', TRUE, FALSE, now());" +
            "INSERT INTO \"Warmth\" (\"FromUserId\", \"ToUserId\", \"Message\", \"SentAtUtc\", \"NotifyPending\") VALUES " +
            "('00000000-0000-0000-0000-000000000003', '00000000-0000-0000-0000-000000000001', 'thinking', now(), FALSE);" +
            "INSERT INTO \"FiresideCodes\" (\"UserId\", \"CodeHash\", \"ExpiresAtUtc\") VALUES " +
            "('00000000-0000-0000-0000-000000000001', 'hash', now() + interval '30 minutes')");

        await migrator.MigrateAsync();

        var withdrawn = await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.ConsentWithdrawnAt).ToListAsync();
        withdrawn[0].Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        withdrawn.Skip(1).Should().OnlyContain(w => w == null).And.HaveCount(2);
        (await db.Users.AsNoTracking().AnyAsync(u => u.FiresideEnabled || u.WarmthAskUntilUtc != null)).Should().BeFalse();
        (await db.FiresideLinks.CountAsync(), await db.Warmth.CountAsync(), await db.FiresideCodes.CountAsync()).Should().Be((0, 0, 0));
        (await db.Users.AsNoTracking().CountAsync(u => u.FiresideName == "Fi")).Should().Be(2, "the name is kept for turning it on again");
    }

    [DockerFact]
    public async Task AccountCreatedMigration_BackfillsTheEarliestOfTheFirstHabitAndTheFirstSession_ElseNull()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
        DateTime At(int month, int day) => new(2026, month, day, 8, 0, 0, DateTimeKind.Utc);
        db.Users.AddRange(new User { Id = Id(1) }, new User { Id = Id(2) }, new User { Id = Id(3) });
        db.Habits.AddRange(new Habit { UserId = Id(1), Name = "Read", CreatedAt = At(10, 1) }, new Habit { UserId = Id(1), Name = "Walk", CreatedAt = At(10, 5) },
            new Habit { UserId = Id(2), Name = "Stretch", CreatedAt = At(9, 20) });
        db.AuthSessions.AddRange(
            new AuthSession { Id = Guid.NewGuid(), UserId = Id(1), CreatedUtc = At(9, 15), ExpiresUtc = At(12, 1) },
            new AuthSession { Id = Guid.NewGuid(), UserId = Id(2), CreatedUtc = At(10, 2), ExpiresUtc = At(12, 1) });
        await db.SaveChangesAsync();

        await migrator.MigrateAsync("20261119102403_ConsentWithdrawal");
        await migrator.MigrateAsync();

        (await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.CreatedUtc).ToListAsync())
            .Should().Equal(At(9, 15), At(9, 20), null);
    }

    [DockerFact]
    public async Task RestoreTodayOrderMigration_FillsTheOldTodayOrderForAccountsWithRowsFromBeforeTheRelease_Once()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new EmberDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync();
        Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");
        DateTime At(int month, int day) => new(2026, month, day, 8, 0, 0, DateTimeKind.Utc);
        var release = DateTimeOffset.Parse(RestoreTodayOrder.TodayOrderRelease, CultureInfo.InvariantCulture).UtcDateTime;
        db.Users.AddRange(new User { Id = Id(1) }, new User { Id = Id(2), TodayOrder = ["h:30"] }, new User { Id = Id(3) },
            new User { Id = Id(4) }, new User { Id = Id(5) }, new User { Id = Id(6) });
        db.Routines.AddRange(
            new Routine { Id = 1, UserId = Id(1), Name = "Morning", Position = 1, CreatedAt = At(10, 1) },
            new Routine { Id = 2, UserId = Id(1), Name = "Evening", Position = 0, CreatedAt = At(10, 3) },
            new Routine { Id = 3, UserId = Id(1), Name = "Noon", Position = 0, CreatedAt = At(10, 2) },
            new Routine { Id = 4, UserId = Id(1), Name = "Empty", Position = 2, CreatedAt = At(10, 4) },
            new Routine { Id = 5, UserId = Id(1), Name = "Emptied", Position = 3, CreatedAt = At(10, 4) },
            new Routine { Id = 6, UserId = Id(4), Name = "Later", Position = 0, CreatedAt = release.AddDays(1) },
            new Routine { Id = 7, UserId = Id(5), Name = "Someday", Position = 0, CreatedAt = At(10, 1) },
            new Routine { Id = 8, UserId = Id(1), Name = "Lunch", Position = 0, CreatedAt = At(10, 2) },
            new Routine { Id = 9, UserId = Id(6), Name = "Morning", Position = 0, CreatedAt = release.AddMinutes(-1) });
        db.Habits.AddRange(
            new Habit { Id = 10, UserId = Id(1), RoutineId = 1, Name = "Stretch", CreatedAt = At(10, 1) },
            new Habit { Id = 11, UserId = Id(1), RoutineId = 2, Name = "Floss", CreatedAt = At(10, 3) },
            new Habit { Id = 12, UserId = Id(1), RoutineId = 3, Name = "Water", CreatedAt = At(10, 2) },
            new Habit { Id = 14, UserId = Id(1), RoutineId = 8, Name = "Tea", CreatedAt = At(10, 2) },
            new Habit { Id = 20, UserId = Id(1), Name = "Read", CreatedAt = At(10, 5) },
            new Habit { Id = 21, UserId = Id(1), Name = "Snacking", Kind = HabitKind.LetGo, CreatedAt = At(10, 4) },
            new Habit { Id = 23, UserId = Id(1), Name = "Walk", CreatedAt = At(10, 6) },
            new Habit { Id = 22, UserId = Id(1), Name = "Journal", Kind = HabitKind.LetGo, CreatedAt = At(10, 6) },
            new Habit { Id = 30, UserId = Id(2), Name = "Read", CreatedAt = At(10, 1) },
            new Habit { Id = 31, UserId = Id(2), Name = "Walk", CreatedAt = At(10, 2) },
            new Habit { Id = 40, UserId = Id(3), Name = "Read", CreatedAt = release },
            new Habit { Id = 50, UserId = Id(4), Name = "Read", CreatedAt = release.AddMinutes(-1) },
            new Habit { Id = 51, UserId = Id(4), Name = "Walk", CreatedAt = release.AddDays(1) },
            new Habit { Id = 52, UserId = Id(4), RoutineId = 6, Name = "Stretch", CreatedAt = release.AddDays(1) },
            new Habit { Id = 60, UserId = Id(6), RoutineId = 9, Name = "Stretch", CreatedAt = release.AddDays(1) });
        var removed = new Habit { Id = 13, UserId = Id(1), RoutineId = 5, Name = "Gone", CreatedAt = At(10, 4) };
        db.Habits.Add(removed);
        await db.SaveChangesAsync();
        db.Habits.Remove(removed);
        await db.SaveChangesAsync();

        async Task<List<List<string>?>> OrdersAsync()
            => await db.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.TodayOrder).ToListAsync();
        await migrator.MigrateAsync("20261120221811_AddMadeItDays");
        await migrator.MigrateAsync();

        var orders = await OrdersAsync();
        orders.Should().BeEquivalentTo(new List<string>?[]
        {
            ["r:3", "r:8", "r:2", "r:1", "h:21", "h:20", "h:22", "h:23"],
            ["h:30"],
            null,
            ["r:6", "h:50", "h:51"],
            null,
            ["r:9"]
        }, o => o.WithStrictOrdering());

        await migrator.MigrateAsync("20261120221811_AddMadeItDays");
        await migrator.MigrateAsync();

        (await OrdersAsync()).Should().BeEquivalentTo(orders, o => o.WithStrictOrdering());
    }

    private static async Task<DateOnly> CurrentDateAsync(EmberDbContext db)
        => (await db.Database.SqlQueryRaw<DateOnly>("SELECT CURRENT_DATE AS \"Value\"").ToListAsync()).Single();

    [DockerFact]
    public async Task UpgradingAnExistingDatabase_FillsNextReminderForHabitsWithAReminder()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<EmberDbContext>().UseNpgsql(connectionString).Options;
        await using (var db = new EmberDbContext(options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20260428021004_AddFrequencyPauseLeaderboard");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Users\" (\"DiscordUserId\", \"Timezone\", \"GraceDaysPerWeek\", \"LeaderboardOptOut\") VALUES (7, 'Asia/Tokyo', 1, FALSE);" +
                "INSERT INTO \"Habits\" (\"UserId\", \"Name\", \"ReminderTime\", \"CreatedAt\", \"FrequencyType\") " +
                "VALUES (7, 'Stretch', '09:00', now(), 0), (7, 'Read', NULL, now(), 0)");
        }

        var now = new DateTimeOffset(2027, 1, 15, 12, 0, 0, TimeSpan.Zero);
        using var host = AppHost.CreateHostBuilder([], new EmberHostOptions
        {
            IncludeDiscordGateway = false,
            ConfigureDbContext = (o, _) => o.UseNpgsql(connectionString),
            ConfigureServices = (services, _) => services.AddSingleton<TimeProvider>(new FakeTimeProvider(now))
        }).Build();

        await AppHost.InitializeAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var next = await scope.ServiceProvider.GetRequiredService<EmberDbContext>().Habits
            .OrderBy(h => h.Name).Select(h => new { h.Name, h.NextReminderUtc }).ToListAsync();
        next.Should().BeEquivalentTo([
            new { Name = "Read", NextReminderUtc = (DateTime?)null },
            new { Name = "Stretch", NextReminderUtc = (DateTime?)new DateTime(2027, 1, 16, 0, 0, 0, DateTimeKind.Utc) }]);
    }
}
