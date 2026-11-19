using Npgsql;

namespace Ember.Ops.Tests;

/// <summary>
/// A small synthetic dataset with hand-countable numbers. "Today" is Wednesday 2026-11-18; <c>D(n)</c> is n days before.
/// Users u1..u6; every expectation in the tests is derived from the rows listed here.
/// </summary>
public sealed class Dataset
{
    public List<Guid> UserIds { get; } = [];

    public int FeedbackOpen { get; private set; }

    public int FeedbackPlanned { get; private set; }

    public int FeedbackAnonymous { get; private set; }

    public int[] HabitIds { get; private set; } = [];

    public static async Task<Dataset> InsertAsync(string superuserEmberConnection, DateOnly today, bool includeV2)
    {
        var data = new Dataset();
        await using var connection = new NpgsqlConnection(superuserEmberConnection);
        await connection.OpenAsync();
        var rows = new SyntheticRows(connection);

        DateOnly D(int n) => today.AddDays(-n);
        static DateTime Utc(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);
        DateTime At(int daysAgo, int hour = 12) => D(daysAgo).ToDateTime(new TimeOnly(hour, 0), DateTimeKind.Utc);
        var now = DateTime.UtcNow;

        // grace days 0,1,1,2,0,0; streaks shown u1..u3; discord reminders on u1,u2, off u3, unset u4..u6;
        // dm failures u2,u4; dm paused u3; second reminder minutes 15,15,30; designs tidepool u1,u2, marigold u3
        var grace = new[] { 0, 1, 1, 2, 0, 0 };
        var discord = new bool?[] { true, true, false, null, null, null };
        var minutes = new[] { 15, 15, 30, 0, 0, 0 };
        var themes = new[] { "tidepool", "tidepool", "marigold", null, null, null };
        for (var i = 0; i < 6; i++)
        {
            var id = Guid.NewGuid();
            data.UserIds.Add(id);
            var user = new Dictionary<string, object?>
            {
                ["Id"] = id, ["UserName"] = $"synthetic-user-{i + 1}", ["Email"] = $"synthetic-user-{i + 1}@example.test",
                ["ShowStreaks"] = i < 3, ["GraceDaysPerWeek"] = grace[i], ["SecondReminderMinutes"] = minutes[i],
                ["DmFailureCount"] = i is 1 or 3 ? i : 0, ["DmRemindersPausedAt"] = i == 2 ? now : null, ["DiscordReminders"] = discord[i],
                ["Timezone"] = "Europe/Berlin", ["Language"] = "en", ["Theme"] = themes[i],
            };
            await rows.AddAsync("AspNetUsers", user);
        }

        // discord logins u1,u2
        foreach (var i in new[] { 0, 1 })
            await rows.AddAsync("AspNetUserLogins", new() { ["LoginProvider"] = "Discord", ["ProviderKey"] = $"{1000 + i}", ["UserId"] = data.UserIds[i] });

        // created D1, D1, D8; reminder time 07:00 on the first
        var routine = await rows.AddReturningIdAsync("Routines", new()
        {
            ["UserId"] = data.UserIds[0], ["CreatedAt"] = At(1), ["NextReminderUtc"] = now.AddHours(1), ["Position"] = 0, ["ReminderTime"] = new TimeOnly(7, 0),
        });
        await rows.AddAsync("Routines", new() { ["UserId"] = data.UserIds[0], ["CreatedAt"] = At(1), ["NextReminderUtc"] = now.AddHours(-2), ["Position"] = 1 });
        await rows.AddAsync("Routines", new() { ["UserId"] = data.UserIds[1], ["CreatedAt"] = At(8), ["SecondReminderUtc"] = now.AddMinutes(5), ["Position"] = 0 });

        // (user index, created days ago, letGo, pause from/until, next reminder, second reminder)
        // schedules: h2 times per week, h3 set weekdays, others daily; times per day h5 3, h6 5, others 1;
        // reminder times h1 08:00, h2 08:30 and 20:00, h5 21:15; h1 in the 07:00 routine
        var habits = new (int User, int Ago, bool LetGo, (DateOnly? From, DateOnly Until)? Pause, DateTime? Next, DateTime? Second)[]
        {
            (0, 1, false, null, now.AddHours(1), null),     // h1: u1 first habit, week of 2026-11-16
            (1, 10, false, null, now.AddHours(-1), null),   // h2: u2 first habit, week of 2026-11-02; overdue
            (1, 3, false, (today.AddDays(1), today.AddDays(5)), null, null), // h3: pause starts tomorrow
            (2, 10, true, null, null, null),                // h4: u3 first habit, let go
            (2, 2, false, null, now.AddHours(1), now.AddMinutes(10)), // h5
            (3, 100, false, null, null, null),              // h6: u4, older than 12 weeks
            (4, 20, false, (null, today.AddDays(3)), null, null), // h7: u5 first habit, week of 2026-10-26; paused
            (4, 15, true, null, null, null),                // h8: let go
        };
        var ids = new List<int>();
        foreach (var h in habits)
        {
            var habit = new Dictionary<string, object?>
            {
                ["UserId"] = data.UserIds[h.User], ["Name"] = "Synthetic habit", ["CreatedAt"] = At(h.Ago), ["PausedFrom"] = h.Pause?.From, ["PausedUntil"] = h.Pause?.Until,
                ["NextReminderUtc"] = h.Next, ["SecondReminderUtc"] = h.Second, ["TimesPerDay"] = ids.Count switch { 4 => 3, 5 => 5, _ => 1 },
                ["FrequencyType"] = ids.Count switch { 1 => 1, 2 => 2, _ => 0 }, ["RoutineId"] = ids.Count == 0 ? routine : null,
            };
            TimeOnly[] times = ids.Count switch { 0 => [new(8, 0)], 1 => [new(8, 30), new(20, 0)], 4 => [new(21, 15)], _ => [] };
            if (times.Length > 0) habit["ReminderTimes"] = times;
            if (includeV2) habit["Kind"] = h.LetGo ? 1 : 0;
            ids.Add(await rows.AddReturningIdAsync("Habits", habit));
        }
        data.HabitIds = [.. ids];
        int H(int n) => ids[n - 1];

        // sign-in sessions as (user, created, last seen); Berlin is UTC+1, so the 7-day window opens 2026-11-11 23:00Z and the 14-day window 2026-11-04 23:00Z,
        // today's day 2026-11-17 23:00Z and this week 2026-11-15 23:00Z.
        // u1: two sessions, last seen today 08:00Z and 2 days ago (one user, counted once); u2: created at the 7-day edge and never seen again, plus one last seen D2;
        // u3: last seen one minute before the 7-day edge, created long before; u4: created at the 14-day edge, never seen; u5: last seen one minute before the 14-day edge; u6: none
        foreach (var (user, created, lastSeen) in new (int, DateTime, DateTime?)[]
        {
            (0, At(30), At(0, 8)),
            (0, At(5), At(2)),
            (1, Utc(2026, 11, 11, 23, 0), null),
            (1, At(40), At(2)),
            (2, At(30), Utc(2026, 11, 11, 22, 59)),
            (3, Utc(2026, 11, 4, 23, 0), null),
            (4, At(40), Utc(2026, 11, 4, 22, 59)),
        })
            await rows.AddAsync("AuthSessions", new()
            {
                ["Id"] = Guid.NewGuid(), ["UserId"] = data.UserIds[user], ["CreatedUtc"] = created, ["ExpiresUtc"] = created.AddDays(60), ["LastSeenUtc"] = lastSeen,
            });

        // discord reminders: D0 x2, D1 x1, D14 (outside the 14-day window) x1; push: D0 x1, D5 x2, D20 x1
        foreach (var (habit, ago) in new[] { (1, 0), (2, 0), (1, 1), (1, 14) })
            await rows.AddAsync("ReminderMessages", new() { ["UserId"] = data.UserIds[habit == 1 ? 0 : 1], ["HabitId"] = H(habit), ["Date"] = D(ago), ["ChannelId"] = 1m, ["MessageId"] = 1m });
        foreach (var (habit, ago) in new[] { (1, 0), (1, 5), (2, 5), (1, 20) })
            await rows.AddAsync("PushReminders", new() { ["UserId"] = data.UserIds[habit == 1 ? 0 : 1], ["HabitId"] = H(habit), ["Date"] = D(ago) });

        // 3 push subscriptions (2 enabled), 2 phones (1 enabled)
        for (var i = 0; i < 3; i++)
            await rows.AddAsync("PushSubscriptions", new() { ["UserId"] = data.UserIds[i], ["Endpoint"] = $"https://push.example.test/{i}", ["Enabled"] = i != 2, ["CreatedAt"] = now });
        for (var i = 0; i < 2; i++)
            await rows.AddAsync("ReminderPhones", new() { ["UserId"] = data.UserIds[i], ["InstallId"] = $"synthetic-install-{i}", ["Enabled"] = i == 0, ["CreatedAt"] = now });

        // feedback: open (by u1, one image), planned (anonymous), released (by u2)
        var open = await rows.AddReturningIdAsync("Feedback", new()
        {
            ["UserId"] = data.UserIds[0], ["Platform"] = "web", ["Title"] = "Synthetic report one", ["Text"] = "Synthetic text one", ["CreatedAt"] = now.AddDays(-3), ["Status"] = "open",
        });
        await rows.AddAsync("FeedbackImages", new() { ["Id"] = Guid.NewGuid(), ["FeedbackId"] = open, ["ContentType"] = "image/png", ["Size"] = 4, ["Data"] = new byte[] { 1, 2, 3, 4 } });
        var planned = await rows.AddReturningIdAsync("Feedback", new()
        {
            ["UserId"] = null, ["Platform"] = "android", ["Title"] = "Synthetic report two", ["Text"] = "Synthetic text two", ["CreatedAt"] = now.AddDays(-2), ["Status"] = "planned",
        });
        await rows.AddReturningIdAsync("Feedback", new()
        {
            ["UserId"] = data.UserIds[1], ["Platform"] = "web", ["Title"] = "Synthetic report three", ["Text"] = "Synthetic text three", ["CreatedAt"] = now.AddDays(-1),
            ["Status"] = "released", ["ResolvedAt"] = now.AddHours(-5),
        });
        (data.FeedbackOpen, data.FeedbackPlanned, data.FeedbackAnonymous) = (open, planned, planned);
        return data;
    }
}
