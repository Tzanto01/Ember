using System.Globalization;
using System.Text.Json;

namespace Ember.Ops.Lookout;

/// <summary>Maps the dashboard's own records to Lookout pages, widgets and the summary.</summary>
public static class LookoutPages
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    /// <summary>Activity figures with fewer distinct users than this (but not 0) are suppressed.</summary>
    public const int MinUsers = 3;

    public const string Suppressed = "<3";
    private const string SmallNote = "bars with 1 or 2 people are left out";

    /// <summary>The manifest; <paramref name="buildSha"/> becomes the version when set.</summary>
    public static Manifest Manifest(string? buildSha) => new(1, "ember", "Ember", "flame",
        string.IsNullOrWhiteSpace(buildSha) ? null : buildSha, "Europe/Berlin",
        [
            new("overview", "Overview", "layout-dashboard", 30),
            new("activity", "Activity", "activity", 60),
            new("habits", "Habits", "chart-column", 60),
            new("users", "Users", "users", 60),
            new("reminders", "Reminders", "bell", 30),
            new("feedback", "Feedback", "kanban", 0),
            new("feedback-trends", "Feedback trends", "inbox", 60),
            new("notices", "Notices", "megaphone", 30),
            new("system", "System", "server", 30),
        ]);

    public static Page Overview(Overview o)
    {
        var (users, habits, streaks) = (o.Users, o.Habits, o.Streaks);
        return new("overview", "Overview", o.GeneratedAt,
        [
            Stats("tiles", "Overview", "tiles",
            [
                UsersItem(users.Total, o.Series),
                Active7Item(users.Active7, users.Total, o.Series) with { Hint = $"{Pct(users.Active7, users.Total)} of users" },
                new("Active 14 days", users.Active14, Hint: $"{Pct(users.Active14, users.Total)} of users", Max: users.Total),
                new("Habits", habits.Total, Hint: $"{N(habits.Paused)} paused"),
                new("Routines", o.Routines.Total, Hint: $"{N(o.Routines.Users)} users"),
                new("Streak users", streaks.Users, Hint: $"{Pct(streaks.ShowStreaks, streaks.Users)} show streaks", Max: users.Total),
            ]),
            Bars("new-users", "New users per week", "week", ["Count"], users.NewPerWeek.Select(w => new Bar(Date(w.Week), [w.Count])), "by first habit, proxy"),
            Panel("grace", "Users by grace days per week", null,
            [
                Bars("grace-days", "Users by grace days per week", "category", ["Users"], streaks.Grace.Select(g => new Bar($"{g.Days} d", [g.Count]))),
                Stats("grace-habits", "Habits", "list", [new("Habits paused", habits.Paused), new("Habits with a reminder", habits.WithReminder)]),
            ]),
        ]);
    }

    public static Page Reminders(Reminders r)
    {
        var overdue = r.Overdue.Habits + r.Overdue.Routines;
        var second = r.SecondReminders;
        return new("reminders", "Reminders", r.GeneratedAt,
        [
            Stats("tiles", "Reminders", "tiles",
            [
                overdue == 0
                    ? new("Overdue reminders", overdue, Hint: "Advancing", HintTone: "ok", Goal: new(Below: 1))
                    : new("Overdue reminders", overdue, Hint: $"{N(r.Overdue.Habits)} habits, {N(r.Overdue.Routines)} routines", HintTone: "bad", Goal: new(Below: 1)),
                new("Discord DMs on", r.Discord.RemindersOn),
                new("Second reminders on", second.UsersOn, Hint: $"{N(second.PendingNow)} pending now"),
                new("Push enabled", r.Push.Enabled, Hint: $"of {N(r.Push.Subscriptions)} subscriptions", Max: r.Push.Subscriptions),
            ]),
            Bars("open", "Still-open reminders per day", "day", ["Discord", "Push"], Channels(r.OpenPerDay),
                "Ember deletes reminder rows when they close, so these are only the ones still open"),
            r.History.Since is { } since
                ? Bars("sent", "Reminders sent per day", "day", ["Discord", "Push"], Channels(r.History.PerDay),
                    $"history since {Local(since)}; includes second reminders and resends")
                : Bars("sent", "Reminders sent per day", "day", ["Discord", "Push"], []) with { Unavailable = "No snapshots yet" },
            r.History.Since is not { } historySince
                ? Stats("delivery", "Sent, last 7 days", "list", []) with { Unavailable = "No snapshots yet" }
                : Stats("delivery", "Sent, last 7 days", "list",
                [
                    SentItem("Discord", r.History.PerDay.Select(d => d.Discord).ToList(), historySince <= r.GeneratedAt.AddDays(-14)),
                    SentItem("Push", r.History.PerDay.Select(d => d.Push).ToList(), historySince <= r.GeneratedAt.AddDays(-14)),
                ]) with { Note = "from the dashboard's snapshots; trend is the last 30 days" },
            Stats("discord", "Discord DMs", "list",
            [
                new("Users with DM reminders", r.Discord.RemindersOn),
                new("Failing", r.Discord.Failing, Tone: r.Discord.Failing > 0 ? "bad" : null),
                new("Paused", r.Discord.Paused),
            ]),
            Panel("second", "Second reminders", null,
            [
                Stats("second-counts", "Counts", "list", [new("Users on", second.UsersOn), new("Pending now", second.PendingNow)]),
                Bars("second-delays", "Users by delay", "category", ["Users"], second.ByMinutes.Select(m => new Bar($"{m.Minutes} min", [m.Count]))),
            ]),
            Stats("push", "Push and phones", "list",
            [
                new("Push subscriptions", r.Push.Subscriptions),
                new("Push enabled", r.Push.Enabled),
                new("Phones", r.Phones.Total),
                new("Phones enabled", r.Phones.Enabled),
            ]),
            Stats("overdue", "Overdue", "list", [new("Habits", r.Overdue.Habits), new("Routines", r.Overdue.Routines)]) with
            {
                Note = "Next reminder more than 15 minutes in the past.",
            },
        ]);
    }

    public static Page Feedback(FeedbackList list, DateTime generatedAt) => new("feedback", "Feedback", generatedAt,
    [
        new BoardWidget
        {
            Id = "feedback",
            Title = "Feedback",
            Columns = FeedbackStore.Statuses.Select(s => new BoardColumn(s, char.ToUpperInvariant(s[0]) + s[1..])).ToList(),
            Facets = [new("platform", "Platform")],
            Comments = true,
            Cards = list.Items.Select(Card).ToList(),
        },
    ]);

    public static Page Notices(IResult notices, DateTime generatedAt) => new("notices", "Notices", generatedAt,
    [
        notices is Microsoft.AspNetCore.Http.HttpResults.Ok<NoticesView> { Value: { } view }
            ? Notices(view)
            : Notices(new NoticesView(false, [])) with
            {
                Unavailable = notices is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult { ProblemDetails.Detail: { } detail } ? detail : "Unavailable",
            },
    ]);

    public static NoticesWidget Notices(NoticesView view) => new()
    {
        Id = "notices",
        Title = "Notices",
        Kinds = ["info", "maintenance"],
        Languages = [new("en", "English"), new("de", "German")],
        MaxLength = NoticeRules.MaxTextLength,
        MaxNotices = NoticeRules.MaxNotices,
        Publishing = view.Publishing,
        Notices = view.Notices.Select(n => new NoticeItem(n.Id, n.Kind, new Dictionary<string, string> { ["en"] = n.En, ["de"] = n.De },
            n.ShowFrom, n.ShowUntil, n.State)).ToList(),
    };

    public static Page System(SystemReport s)
    {
        var services = new StatusWidget
        {
            Id = "services",
            Title = "Services",
            CheckedAt = s.StatusWrittenAt,
            StaleAfterSeconds = (int)SystemInfo.StaleAfter.TotalSeconds,
            Items = s.Services.Select(x => new StatusItem(x.Name, x.Ok ? "ok" : "bad", x.Detail)).ToList(),
            Note = s.StatusWrittenAt is null && s.Errors.TryGetValue("status", out var statusError) ? $"Host status unavailable: {statusError}" : null,
        };
        return new("system", "System", s.GeneratedAt,
        [
            services,
            Section(s, "database", "Database", s.Database is { } db
                ? [new("Size", db.SizeBytes, "bytes"), new("Latest migration", db.LatestMigration, "code")]
                : null),
            Section(s, "disks", "Disks", s.Disks?.Select(d =>
                new StatItem(d.Mount, d.FreeBytes, "bytes", DiskTone(d), $"of {Bytes(d.TotalBytes)} ({Pct(d.FreeBytes, d.TotalBytes)} free)",
                    Max: d.TotalBytes, Goal: new(Above: d.TotalBytes * 0.1))).ToList()),
            Section(s, "backup", "Newest pre-deploy dump", s.Backup is { } backup
                ? [new("File", backup.Name, "code"), new("Written", Iso(backup.At), "time"), new("Age", backup.AgeHours, "hours"), new("Size", backup.SizeBytes, "bytes")]
                : null),
            Section(s, "deployed", "Deployed", s.Deployed is { } deployed
                ? [new("Production", deployed.Production, "code"), new("Staging", deployed.Staging, "code"), new("Dashboard", s.DashboardSha, "code")]
                : null),
            Section(s, "apk", "Android APK", s.Apk is { } apk
                ? [new("Version", apk.VersionName, "text"), new("Version code", apk.VersionCode.ToString(CultureInfo.InvariantCulture), "text")]
                : null),
            s.Notice is { } notice
                ? new ListWidget { Id = "live-notice", Title = "Live notice", Empty = "No live notice", Items = NoticeItems(notice) }
                : new ListWidget { Id = "live-notice", Title = "Live notice", Empty = "No live notice", Items = [], Unavailable = Unavailable(s, "notice") },
            s.Ci is { } runs
                ? new CiWidget { Id = "ci", Title = "CI", Runs = runs }
                : new CiWidget { Id = "ci", Title = "CI", Runs = [], Unavailable = Unavailable(s, "ci") },
        ]);
    }

    /// <summary>Sign-in activity from the dashboard's snapshots; every figure of 1 or 2 people is suppressed.</summary>
    public static Page Activity(ActivityInsights a)
    {
        if (a.Since is not { } since)
            return new("activity", "Activity", a.GeneratedAt,
            [
                Stats("tiles", "Activity", "tiles", []) with { Unavailable = "No snapshots yet" },
                Bars("active-per-day", "Active users per day", "day", ["Users"], []) with { Unavailable = "No snapshots yet" },
                Bars("active-per-week", "Active users per week", "week", ["Users"], []) with { Unavailable = "No snapshots yet" },
            ]);

        var history = $"used a web or app sign-in; history starts {Date(since)}";
        var todayDate = OpsCalendar.BerlinDate(a.GeneratedAt);
        var today = a.PerDay.FirstOrDefault(d => d.Date == todayDate)?.Count;
        var lastMonday = OpsCalendar.Monday(todayDate).AddDays(-7);
        var lastWeekDays = a.PerDay.Where(d => d.Date >= lastMonday && d.Date < lastMonday.AddDays(7)).ToList();
        var lastWeek = since <= lastMonday && lastWeekDays.Count == 7 ? a.PerWeek.FirstOrDefault(w => w.Label == Date(lastMonday)) : null;
        double? average = lastWeek is null ? null : lastWeekDays.Average(d => d.Count);
        var weekUsers = lastWeek?.Users ?? 0;
        var fullWeek = lastWeek is null ? "needs a full week of history" : "last full week";
        return new("activity", "Activity", a.GeneratedAt,
        [
            Stats("tiles", "Activity", "tiles",
            [
                Guarded(new("Active today", today), today ?? 0),
                Guarded(new("Average per day", average is { } avg ? Math.Round(avg, 1) : null, Hint: fullWeek), weekUsers),
                Guarded(new("Stickiness", weekUsers == 0 ? null : average / weekUsers, "percent", Hint: lastWeek is null ? fullWeek : "average day of last week's active users",
                    Max: 1), weekUsers),
            ]) with { Note = $"{history}; values for 1 or 2 people show as <3" },
            Bars("active-per-day", "Active users per day", "day", ["Users"], Guarded(a.PerDay.Select(d => (Bar(d), d.Count))),
                $"{history}; {SmallNote}") with { Better = "up" },
            Bars("active-per-week", "Active users per week", "week", ["Users"], Guarded(a.PerWeek.Select(w => (new Bar(w.Label, [w.Count]), w.Users))),
                $"{history}; {SmallNote}") with { Better = "up" },
        ]);
    }

    public static Page Habits(HabitInsights h)
    {
        var users = h.UsersByHabits.Sum(b => b.Count);
        return new("habits", "Habits", h.GeneratedAt,
        [
            Stats("tiles", "Habits", "tiles",
            [
                new("Habits", h.Habits),
                new("Users with a habit", h.UsersWithHabits, Hint: $"{Pct(h.UsersWithHabits, users)} of users", Max: users),
                new("Habits per user", h.UsersWithHabits == 0 ? null : Math.Round((double)h.Habits / h.UsersWithHabits, 1), Hint: "of users with a habit"),
                new("In a routine", h.InRoutine, Hint: $"{Pct(h.InRoutine, h.Habits)} of habits", Max: h.Habits),
                new("With a reminder time", h.WithReminderTime, Hint: $"{Pct(h.WithReminderTime, h.Habits)} of habits", Max: h.Habits),
                new("Routines", h.Routines),
            ]),
            Bars("schedule", "Habits by schedule", "category", ["Habits"], h.BySchedule.Select(b => new Bar(b.Label, [b.Count]))),
            Bars("times-per-day", "Habits by check-ins per day", "category", ["Habits"], h.ByTimesPerDay.Select(b => new Bar(b.Label, [b.Count]))),
            Bars("reminder-hours", "Reminder times by hour", "category", ["Habits", "Routines"],
                h.ReminderHours.Select(r => new Bar(r.Hour.ToString("00", CultureInfo.InvariantCulture), [r.Habits, r.Routines])),
                "each user's local time; a habit can have several") with { Size = "full" },
            Bars("habits-per-user", "Users by number of habits", "category", ["Users"], h.UsersByHabits.Select(b => new Bar(b.Label, [b.Count]))),
            Bars("created", "Created per week", "week", ["Habits", "Routines"], h.CreatedPerWeek.Select(w => new Bar(Date(w.Week), [w.Habits, w.Routines])),
                "still existing; deleted ones are not counted"),
        ]);
    }

    public static Page Users(UserInsights u) => new("users", "Users", u.GeneratedAt,
    [
        Stats("tiles", "Users", "tiles",
        [
            new("Users", u.Users),
            new("Discord linked", u.DiscordLinked, Hint: $"{Pct(u.DiscordLinked, u.Users)} of users", Max: u.Users),
            new("With a reminder destination", u.Users - u.NoDestination, Hint: $"{Pct(u.Users - u.NoDestination, u.Users)} of users", Max: u.Users),
            new("Web push on", u.WebPush, Max: u.Users),
            new("Android phone on", u.Phone, Max: u.Users),
        ]),
        Bars("destinations", "Users by reminder destination", "category", ["Users"],
        [
            new("Discord DMs", [u.DiscordDms]), new("Web push", [u.WebPush]), new("Android phone", [u.Phone]), new("None", [u.NoDestination]),
        ], "switched on; a user can have several"),
        Bars("designs", "Users by design", "category", ["Users"], u.ByTheme.Select(t => new Bar(char.ToUpperInvariant(t.Label[0]) + t.Label[1..], [t.Count]))),
    ]);

    /// <summary>Feedback over time from the reports Ember keeps (12 months); Ember stores no status history.</summary>
    public static Page FeedbackTrends(FeedbackList list, DateTime now)
    {
        var items = list.Items;
        var today = OpsCalendar.BerlinDate(now);
        var weeks = OpsCalendar.Weeks(today, InsightQueries.Weeks).ToList();
        var resolved = items.Where(i => i.ResolvedAt is not null).Select(i => (i.ResolvedAt!.Value - i.CreatedAt).TotalHours).Order().ToList();
        var sent7 = items.Count(i => i.CreatedAt >= now.AddDays(-7));
        var sentBefore = items.Count(i => i.CreatedAt >= now.AddDays(-14) && i.CreatedAt < now.AddDays(-7));
        var versions = items.Where(i => !string.IsNullOrWhiteSpace(i.Version)).GroupBy(i => i.Version!).OrderByDescending(g => g.Count()).ThenByDescending(g => g.Key).ToList();
        var versionBars = versions.Take(7).Select(g => new Bar(g.Key, [g.Count()])).ToList();
        if (versions.Count > 7) versionBars.Add(new("Other", [versions.Skip(7).Sum(g => g.Count())]));
        versionBars.Add(new("Unknown", [items.Count(i => string.IsNullOrWhiteSpace(i.Version))]));
        return new("feedback-trends", "Feedback trends", now,
        [
            Stats("tiles", "Feedback", "tiles",
            [
                new("Open", items.Count(i => i.Status == "open"), Goal: new(Below: 5)),
                new("New, 7 days", sent7, Delta: new(sent7 - sentBefore, "the 7 days before", "down")),
                new("Resolved, 30 days", items.Count(i => i.ResolvedAt >= now.AddDays(-30))),
                new("Median time to resolve", resolved.Count == 0 ? null : Math.Round((resolved[(resolved.Count - 1) / 2] + resolved[resolved.Count / 2]) / 2, 1), "hours",
                    Hint: "released or declined reports"),
            ]),
            Bars("per-week", "Reports per week by platform", "week", Platforms,
                weeks.Select(w => new Bar(Date(w), Platforms.Select(p => (double)items.Count(i => OpsCalendar.Monday(OpsCalendar.BerlinDate(i.CreatedAt)) == w && Platform(i) == p)).ToList()))),
            Bars("flow", "Sent and resolved per week", "week", ["Sent", "Resolved"], weeks.Select(w => new Bar(Date(w),
            [
                items.Count(i => OpsCalendar.Monday(OpsCalendar.BerlinDate(i.CreatedAt)) == w),
                items.Count(i => i.ResolvedAt is { } at && OpsCalendar.Monday(OpsCalendar.BerlinDate(at)) == w),
            ]))),
            Bars("by-status", "Reports by status and platform", "category", Platforms,
                FeedbackStore.Statuses.Select(s => new Bar(char.ToUpperInvariant(s[0]) + s[1..], Platforms.Select(p => (double)items.Count(i => i.Status == s && Platform(i) == p)).ToList())),
                "current status"),
            Bars("versions", "Reports by app version", "category", ["Reports"], versionBars, "top 7 versions"),
        ]);
    }

    private static readonly string[] Platforms = ["Web", "Android", "Discord", "Other"];

    private static string Platform(FeedbackItem item) => item.Platform switch { "web" => "Web", "android" => "Android", "discord" => "Discord", _ => "Other" };

    /// <summary>A completed run that did not succeed, as the web badge counts it.</summary>
    public static bool Failed(CiRun run) => run.Status == "completed" && run.Conclusion != "success";

    /// <summary>
    /// The summary card. A null source is a failed database read: its tiles are null, the state is bad and the first alert says so.
    /// </summary>
    public static Summary Summary(OverviewCounters? overview, ReminderCounters? reminders, FeedbackList? feedback, SystemReport system, DateTime now,
        OverviewSeries? series = null)
    {
        var databaseFailed = overview is null || reminders is null || feedback is null;
        int? overdue = reminders is null ? null : reminders.Overdue.Habits + reminders.Overdue.Routines;
        var failedCi = (system.Ci ?? []).Where(Failed).ToList();
        system.Errors.TryGetValue("status", out var statusError);

        var alerts = new List<Alert>();
        if (databaseFailed) alerts.Add(new("Database unavailable", "bad", "system"));
        alerts.AddRange(system.Services.Where(x => !x.Ok && x.Name != "Reminders advancing").Select(x => new Alert($"{x.Name}: {x.Detail ?? "failing"}", "bad", "system")));
        if (overdue > 0) alerts.Add(new($"{N(overdue.Value)} reminders overdue", "bad", "reminders"));
        alerts.AddRange(failedCi.Select(run => new Alert($"CI failed on {run.Branch}", "bad", "system")));
        if (statusError is not null) alerts.Add(new($"Host status: {statusError}", "warn", "system"));
        var lowDisks = (system.Disks ?? []).Where(d => DiskTone(d) is not null).ToList();
        alerts.AddRange(lowDisks.Select(d => new Alert($"Disk {d.Mount}: {Pct(d.FreeBytes, d.TotalBytes)} free", DiskTone(d)!, "system")));

        var bad = databaseFailed || system.Services.Any(x => !x.Ok) || overdue > 0 || failedCi.Any(run => run.Branch == "main")
            || lowDisks.Any(d => DiskTone(d) == "bad");
        var generatedAt = new[] { overview?.GeneratedAt, reminders?.GeneratedAt, feedback is null ? null : now, system.GeneratedAt }.Where(t => t is not null).Min()!.Value;
        return new(generatedAt, bad ? "bad" : statusError is not null || lowDisks.Count > 0 ? "warn" : "ok",
        [
            overview is null ? new("Users", null) : UsersItem(overview.Users, series),
            overview is null ? new("Active 7 days", null) : Active7Item(overview.Active7, overview.Users, series),
            new("Overdue reminders", overdue, Tone: overdue > 0 ? "bad" : null, Goal: new(Below: 1)),
            feedback is null
                ? new("Open feedback", null)
                : new("Open feedback", feedback.Items.Count(i => i.Status == "open"), Goal: new(Below: 5),
                    Delta: new(feedback.Items.Count(i => i.CreatedAt >= now.AddDays(-7)), "last week", "down")),
        ],
        alerts.Take(5).ToList());
    }

    private static StatItem UsersItem(int total, OverviewSeries? series)
    {
        if (series is null) return new("Users", total);
        var running = series.UsersBefore;
        var trend = series.NewPerWeek.Select(w => (double)(running += w.Count)).ToList();
        return new("Users", total, Trend: trend, Delta: new(series.NewPerWeek[^1].Count, "last week", "up"));
    }

    private static StatItem Active7Item(int active7, int total, OverviewSeries? series) => series is null
        ? new("Active 7 days", active7, Max: total)
        : new("Active 7 days", active7, Max: total, Trend: series.Active7PerDay.Count < 2 ? null : series.Active7PerDay.Select(d => (double)d.Count).ToList(),
            Delta: series.Active7Before is { } before ? new(active7 - before, "the 7 days before", "up") : null);

    private static StatItem SentItem(string channel, IReadOnlyList<int> perDay, bool twoWeeksOfHistory)
    {
        var last7 = perDay.TakeLast(7).Sum();
        var before = perDay.SkipLast(7).TakeLast(7).Sum();
        return new(channel, last7, Trend: perDay.TakeLast(30).Select(n => (double)n).ToList(),
            Delta: twoWeeksOfHistory ? new(last7 - before, "the 7 days before") : null);
    }

    /// <summary>True for a bucket of 1 or 2 distinct users, which is effectively one person.</summary>
    public static bool Small(int users) => users is > 0 and < MinUsers;

    /// <summary>The item, or <c>"&lt;3"</c> without hint, trend, delta, max or goal when <paramref name="users"/> is small.</summary>
    private static StatItem Guarded(StatItem item, int users) => Small(users) ? new(item.Label, Suppressed) : item;

    /// <summary>The bars without those whose bucket has a small number of distinct users.</summary>
    private static IEnumerable<Bar> Guarded(IEnumerable<(Bar Bar, int Users)> bars) => bars.Where(b => !Small(b.Users)).Select(b => b.Bar);

    private static Bar Bar(DayCount d) => new(Date(d.Date), [d.Count]);

    private static Card Card(FeedbackItem item)
    {
        var fields = new List<CardField> { new("Platform", item.Platform) };
        if (item.Version is not null) fields.Add(new("Version", item.Version));
        fields.Add(new("Sent", Iso(item.CreatedAt), "time"));
        fields.Add(new("Sent anonymously", item.Anonymous ? "Yes" : "No"));
        if (item.ResolvedAt is { } resolved) fields.Add(new("Resolved", Iso(resolved), "time"));
        return new(item.Id.ToString(CultureInfo.InvariantCulture), item.Status, item.Position, item.Title, item.Text, item.CreatedAt, fields,
            new Dictionary<string, string> { ["platform"] = item.Platform },
            item.Anonymous ? [new("anonymous", "muted")] : null,
            item.ImageIds.Select(id => id.ToString("D")).ToList(), item.CommentCount);
    }

    private static List<ListItem> NoticeItems(JsonElement notice)
    {
        if (notice.ValueKind != JsonValueKind.Object || !notice.TryGetProperty("notices", out var list) || list.ValueKind != JsonValueKind.Array)
            return [new(Lines: [notice.GetRawText()])];
        return list.EnumerateArray().Select(entry =>
        {
            var meta = new List<string>();
            if (NoticeRules.ParseTime(Text(entry, "showFrom")) is { } from) meta.Add($"from {Local(from)}");
            if (NoticeRules.ParseTime(Text(entry, "showUntil")) is { } until) meta.Add($"until {Local(until)}");
            return new ListItem(Text(entry, "id"), new[] { Text(entry, "en"), Text(entry, "de") }.OfType<string>().Where(t => t.Length > 0).ToList(),
                [new(Text(entry, "kind") ?? "notice", "muted")], meta);
        }).ToList();
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static StatsWidget Section(SystemReport s, string id, string title, IReadOnlyList<StatItem>? items) =>
        items is null ? Stats(id, title, "list", []) with { Unavailable = Unavailable(s, id) } : Stats(id, title, "list", items);

    private static string Unavailable(SystemReport s, string section)
    {
        var reason = s.Errors.GetValueOrDefault(section) ?? (section is "disks" or "backup" or "deployed" ? s.Errors.GetValueOrDefault("status") : null);
        return "Unavailable" + (reason is null ? "" : $": {reason}");
    }

    private static StatsWidget Stats(string id, string title, string variant, IReadOnlyList<StatItem> items) =>
        new() { Id = id, Title = title, Variant = variant, Items = items };

    private static BarsWidget Bars(string id, string title, string axis, string[] series, IEnumerable<Bar> bars, string? note = null) =>
        new() { Id = id, Title = title, Axis = axis, Series = series.Select(l => new SeriesRef(l)).ToList(), Bars = bars.ToList(), Note = note };

    private static PanelWidget Panel(string id, string title, string? unavailable, IReadOnlyList<Widget> children) =>
        new() { Id = id, Title = title, Unavailable = unavailable, Children = children };

    private static IEnumerable<Bar> Channels(IEnumerable<ChannelDay> days) => days.Select(d => new Bar(Date(d.Date), [d.Discord, d.Push]));

    private static string Pct(double part, double whole) =>
        whole > 0 ? Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%" : "0%";

    /// <summary><c>bad</c> below 5 % free, <c>warn</c> below 10 %, else null (as the web <c>diskTone</c>).</summary>
    public static string? DiskTone(HostDisk disk) =>
        disk.TotalBytes <= 0 ? null : (double)disk.FreeBytes / disk.TotalBytes is var ratio && ratio < 0.05 ? "bad" : ratio < 0.1 ? "warn" : null;

    private static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var i = 0;
        for (; value >= 1024 && i < units.Length - 1; i++) value /= 1024;
        return value.ToString(i == 0 || value >= 100 ? "0" : "0.0", CultureInfo.InvariantCulture) + " " + units[i];
    }

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => NoticeRules.FormatTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    private static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Local(DateTime utc) => Local(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
}
