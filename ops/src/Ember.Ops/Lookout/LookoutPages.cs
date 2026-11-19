using System.Globalization;

namespace Ember.Ops.Lookout;

/// <summary>Maps the dashboard's own records to Lookout pages, widgets and the summary.</summary>
public static class LookoutPages
{
    /// <summary>Activity figures with fewer distinct users than this (but not 0) are suppressed.</summary>
    public const int MinUsers = 3;

    public const string Suppressed = "<3";
    private const string SmallNote = "bars with 1 or 2 people are left out";

    /// <summary>The manifest; <paramref name="buildSha"/> becomes the version when set.</summary>
    public static Manifest Manifest(string? buildSha) => new(1, "ember", "Ember", "flame",
        string.IsNullOrWhiteSpace(buildSha) ? null : buildSha, "Europe/Berlin",
        [
            new("overview", "Overview", "layout-dashboard", 30),
            new("engagement", "Engagement", "activity", 60),
            new("devices", "Devices", "smartphone", 60),
            new("releases", "Releases", "rocket", 30),
            new("notices", "Notices", "megaphone", 30),
            new("system", "System", "server", 30),
            new("feedback", "Feedback", "kanban", 0),
        ]);

    public static Page Overview(Overview o, ActivityInsights a)
    {
        var users = o.Users;
        var figures = Figures(a);
        return new("overview", "Overview", o.GeneratedAt,
        [
            Stats("tiles", "Overview", "tiles",
            [
                UsersItem(users.Total, o.Series),
                new("New users this week", o.Series.NewPerWeek[^1].Count, Hint: o.Series.SignupNote),
                figures is null ? new("Active today", null, Hint: NoSnapshots, Max: users.Total) : WithMax(figures.Today, users.Total),
                Active7Item(users.Active7, users.Total, o.Series) with { Hint = $"{Pct(users.Active7, users.Total)} of users" },
                new("Active 14 days", users.Active14, Hint: $"{Pct(users.Active14, users.Total)} of users", Max: users.Total),
                figures?.Stickiness ?? new("Stickiness", null, "percent", Hint: NoSnapshots, Max: 1),
            ]) with { Note = figures is null ? null : $"{figures.History}; values for 1 or 2 people show as <3" },
            Bars("new-users", "New users per week", "week", ["Count"], users.NewPerWeek.Select(w => new Bar(Date(w.Week), [w.Count])), o.Series.SignupNote),
            ActivePerDay(a, figures),
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
        ]);
    }

    public static Page Releases(SystemReport s) => new("releases", "Releases", s.GeneratedAt,
    [
        Section(s, "deployed", "Deployed", s.Deployed is { } deployed
            ? [new("Production", deployed.Production, "code"), new("Staging", deployed.Staging, "code"), new("Dashboard", s.DashboardSha, "code")]
            : null),
        Section(s, "apk", "Android APK", s.Apk is { } apk
            ? [new("Version", apk.VersionName, "text"), new("Version code", apk.VersionCode.ToString(CultureInfo.InvariantCulture), "text")]
            : null),
        s.Ci is { } runs
            ? new CiWidget { Id = "ci", Title = "CI", Runs = runs }
            : new CiWidget { Id = "ci", Title = "CI", Runs = [], Unavailable = Unavailable(s, "ci") },
    ]);

    /// <summary>Sign-in activity and retention from the dashboard's snapshots; every figure of 1 or 2 people is suppressed.</summary>
    public static Page Engagement(ActivityInsights a, OverviewSeries series)
    {
        var daysActive = Bars("days-active", "Days active per user per week", "category", ["Users"], []) with
        {
            Unavailable = "Ember keeps only the last sign-in of each session, so days per user can't be counted without storing user ids",
        };
        if (Figures(a) is not { } figures)
            return new("engagement", "Engagement", a.GeneratedAt,
            [
                Stats("tiles", "Activity", "tiles", []) with { Unavailable = NoSnapshots },
                ActivePerDay(a, null),
                Bars("active-per-week", "Active users per week", "week", ["Users"], []) with { Unavailable = NoSnapshots },
                daysActive,
                Stats("retention", "Retention", "list", []) with { Unavailable = NoSnapshots },
            ]);

        return new("engagement", "Engagement", a.GeneratedAt,
        [
            Stats("tiles", "Activity", "tiles", [figures.Today, figures.Average, figures.Stickiness]) with { Note = $"{figures.History}; values for 1 or 2 people show as <3" },
            ActivePerDay(a, figures),
            Bars("active-per-week", "Active users per week", "week", ["Users"], Guarded(a.PerWeek.Select(w => (new Bar(w.Label, [w.Count]), w.Users))),
                $"{figures.History}; {SmallNote}") with { Better = "up" },
            daysActive,
            Retention(a, series),
        ]);
    }

    /// <summary>
    /// Of the users who signed up in each of the last 12 weeks, the share seen 1, 2 and 4 weeks later, pooled over the finished weeks
    /// with a snapshot; suppressed when the new or the returning users are 1 or 2.
    /// </summary>
    private static StatsWidget Retention(ActivityInsights a, OverviewSeries series)
    {
        if (a.Retained.Count == 0) return Stats("retention", "Retention", "list", []) with { Unavailable = NoSnapshots };
        var cohorts = series.NewPerWeek.Where(w => w.Count > 0).ToDictionary(w => w.Week, w => w.Count);
        var thisWeek = OpsCalendar.Monday(OpsCalendar.BerlinDate(a.GeneratedAt));
        var items = InsightQueries.RetentionWeeks.Select(weeks =>
        {
            var rows = a.Retained.Where(r => r.Week == r.Cohort.AddDays(7 * weeks) && r.Week < thisWeek && cohorts.ContainsKey(r.Cohort)).ToList();
            var joined = rows.Sum(r => cohorts[r.Cohort]);
            var kept = rows.Sum(r => Math.Min(r.Users, cohorts[r.Cohort]));
            var label = weeks == 1 ? "After 1 week" : $"After {weeks} weeks";
            if (Small(joined) || Small(kept)) return new StatItem(label, Suppressed);
            return rows.Count == 0
                ? new StatItem(label, null, "percent", Hint: "no finished week yet", Max: 1)
                : new StatItem(label, (double)kept / joined, "percent", Hint: $"{N(kept)} of {N(joined)} new users", Max: 1);
        }).ToList();
        return Stats("retention", "Retention", "list", items) with
        {
            Note = $"share of each week's new users ({series.SignupNote}) seen that many weeks later; history starts {Date(a.Retained.Min(r => r.Week))}; "
                + "values for 1 or 2 people show as <3",
        };
    }

    public static Page Devices(DeviceInsights d)
    {
        var recent = $"users seen in the last {InsightQueries.DeviceDays} days; someone on several counts in each; {SmallNote}";
        return new("devices", "Devices", d.GeneratedAt,
        [
            d.Platforms is { } platforms
                ? Bars("platforms", "Active users by platform", "category", ["Users"], Guarded(platforms.Select(p => (new Bar(p.Label, [p.Count]), p.Users))), recent)
                : Bars("platforms", "Active users by platform", "category", ["Users"], []) with { Unavailable = "Ember doesn't record the sign-in platform yet" },
            d.Versions is { } versions
                ? Bars("versions", "App versions in use", "category", ["Users"], Guarded(versions.Select(v => (new Bar(v.Label, [v.Count]), v.Users))).Take(10),
                    $"{recent}; top 10")
                : Bars("versions", "App versions in use", "category", ["Users"], []) with { Unavailable = "Ember doesn't record the app version on sign-in yet" },
            Stats("adoption", "Adoption", "tiles",
            [
                new("Web push on", d.WebPush, Hint: $"{Pct(d.WebPush, d.Users)} of users", Max: d.Users),
                new("Android app installs", d.AndroidInstalls, Hint: "phones registered with the app"),
                new("Discord linked", d.DiscordLinked, Hint: $"{Pct(d.DiscordLinked, d.Users)} of users", Max: d.Users),
            ]),
        ]);
    }

    private const string NoSnapshots = "No snapshots yet";

    private sealed record ActivityFigures(StatItem Today, StatItem Average, StatItem Stickiness, string History);

    /// <summary>Active today, the last full week's average per day and stickiness, guarded; null before the first snapshot.</summary>
    private static ActivityFigures? Figures(ActivityInsights a)
    {
        if (a.Since is not { } since) return null;
        var history = $"used a web or app sign-in; history starts {Date(since)}";
        var todayDate = OpsCalendar.BerlinDate(a.GeneratedAt);
        var today = a.PerDay.FirstOrDefault(d => d.Date == todayDate)?.Count;
        var lastMonday = OpsCalendar.Monday(todayDate).AddDays(-7);
        var lastWeekDays = a.PerDay.Where(d => d.Date >= lastMonday && d.Date < lastMonday.AddDays(7)).ToList();
        var lastWeek = since <= lastMonday && lastWeekDays.Count == 7 ? a.PerWeek.FirstOrDefault(w => w.Label == Date(lastMonday)) : null;
        double? average = lastWeek is null ? null : lastWeekDays.Average(d => d.Count);
        var weekUsers = lastWeek?.Users ?? 0;
        var fullWeek = lastWeek is null ? "needs a full week of history" : "last full week";
        return new(
            Guarded(new("Active today", today), today ?? 0),
            Guarded(new("Average per day", average is { } avg ? Math.Round(avg, 1) : null, Hint: fullWeek), weekUsers),
            Guarded(new("Stickiness", weekUsers == 0 ? null : average / weekUsers, "percent", Hint: lastWeek is null ? fullWeek : "average day of last week's active users",
                Max: 1), weekUsers),
            history);
    }

    private static BarsWidget ActivePerDay(ActivityInsights a, ActivityFigures? figures) => figures is null
        ? Bars("active-per-day", "Active users per day", "day", ["Users"], []) with { Unavailable = NoSnapshots }
        : Bars("active-per-day", "Active users per day", "day", ["Users"], Guarded(a.PerDay.Select(d => (Bar(d), d.Count))),
            $"{figures.History}; {SmallNote}") with { Better = "up" };

    private static StatItem WithMax(StatItem item, int max) => Equals(item.Value, Suppressed) ? item : item with { Max = max };

    /// <summary>A completed run that did not succeed, as the web badge counts it.</summary>
    public static bool Failed(CiRun run) => run.Status == "completed" && run.Conclusion != "success";

    /// <summary>
    /// The summary card. A null overview or feedback is a failed database read: its tiles are null, the state is bad and the first alert says so.
    /// Overdue reminders show only as the failing "Reminders advancing" health check.
    /// </summary>
    public static Summary Summary(OverviewCounters? overview, FeedbackList? feedback, SystemReport system, DateTime now,
        OverviewSeries? series = null, ActivityInsights? activity = null)
    {
        var databaseFailed = overview is null || feedback is null;
        var failedCi = (system.Ci ?? []).Where(Failed).ToList();
        system.Errors.TryGetValue("status", out var statusError);

        var alerts = new List<Alert>();
        if (databaseFailed) alerts.Add(new("Database unavailable", "bad", "system"));
        alerts.AddRange(system.Services.Where(x => !x.Ok && x.Name != RemindersAdvancing).Select(x => new Alert($"{x.Name}: {x.Detail ?? "failing"}", "bad", "system")));
        if (system.Services.Any(x => !x.Ok && x.Name == RemindersAdvancing)) alerts.Add(new("Reminders not advancing", "bad", "system"));
        alerts.AddRange(failedCi.Select(run => new Alert($"CI failed on {run.Branch}", "bad", "system")));
        if (statusError is not null) alerts.Add(new($"Host status: {statusError}", "warn", "system"));
        var lowDisks = (system.Disks ?? []).Where(d => DiskTone(d) is not null).ToList();
        alerts.AddRange(lowDisks.Select(d => new Alert($"Disk {d.Mount}: {Pct(d.FreeBytes, d.TotalBytes)} free", DiskTone(d)!, "system")));

        var bad = databaseFailed || system.Services.Any(x => !x.Ok) || failedCi.Any(run => run.Branch == "main") || lowDisks.Any(d => DiskTone(d) == "bad");
        var generatedAt = new[] { overview?.GeneratedAt, feedback is null ? null : now, system.GeneratedAt }.Where(t => t is not null).Min()!.Value;
        return new(generatedAt, bad ? "bad" : statusError is not null || lowDisks.Count > 0 ? "warn" : "ok",
        [
            overview is null ? new("Users", null) : UsersItem(overview.Users, series),
            overview is null ? new("Active 7 days", null) : Active7Item(overview.Active7, overview.Users, series),
            (activity is null ? null : Figures(activity))?.Stickiness ?? new("Stickiness", null, "percent"),
            feedback is null
                ? new("Open feedback", null)
                : new("Open feedback", feedback.Items.Count(i => i.Status == "open"), Goal: new(Below: 5),
                    Delta: new(feedback.Items.Count(i => i.CreatedAt >= now.AddDays(-7)), "last week", "down")),
        ],
        alerts.Take(5).ToList());
    }

    /// <summary>The health check <see cref="SystemInfo"/> adds for overdue reminders.</summary>
    public const string RemindersAdvancing = "Reminders advancing";

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
}
