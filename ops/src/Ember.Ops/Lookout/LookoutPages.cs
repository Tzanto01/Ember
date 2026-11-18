using System.Globalization;
using System.Text.Json;

namespace Ember.Ops.Lookout;

/// <summary>Maps the dashboard's own records to Lookout pages, widgets and the summary.</summary>
public static class LookoutPages
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private const string NotOnProduction = "Not on production yet";

    /// <summary>The manifest; <paramref name="buildSha"/> becomes the version when set.</summary>
    public static Manifest Manifest(string? buildSha) => new(1, "ember", "Ember", "flame",
        string.IsNullOrWhiteSpace(buildSha) ? null : buildSha, "Europe/Berlin",
        [
            new("overview", "Overview", "layout-dashboard", 30),
            new("reminders", "Reminders", "bell", 30),
            new("feedback", "Feedback", "kanban", 0),
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
                new("Users", users.Total),
                new("Active 7 days", users.Active7, Hint: $"{Pct(users.Active7, users.Total)} of users"),
                new("Active 30 days", users.Active30, Hint: $"{Pct(users.Active30, users.Total)} of users"),
                new("Habits build / let go", $"{N(habits.Build)} / {(habits.LetGo is { } letGo ? N(letGo) : "–")}",
                    Hint: habits.LetGo is null ? "Let go: not on production yet" : $"{N(habits.Paused)} paused"),
                new("Routines", o.Routines.Total, Hint: $"{N(o.Routines.Users)} users"),
                new("Streak users", streaks.Users, Hint: $"{Pct(streaks.ShowStreaks, streaks.Users)} show streaks"),
            ]),
            Bars("new-users", "New users per week", "week", ["Count"], users.NewPerWeek.Select(w => new Bar(Date(w.Week), [w.Count])), "by first habit, proxy"),
            Bars("check-ins", "Check-ins per day", "day", ["Count"], Days(o.CheckInsPerDay), "last 90 days, completed"),
            Bars("journal", "Journal days per day", "day", ["Count"], Days(o.JournalPerDay), "journal days (note or mood)"),
            Bars("low-days", "Low days per day", "day", ["Low days"], Days(o.LowDaysPerDay)),
            Panel("grace", "Users by grace days per week", null,
            [
                Bars("grace-days", "Users by grace days per week", "category", ["Users"], streaks.Grace.Select(g => new Bar($"{g.Days} d", [g.Count]))),
                Stats("grace-habits", "Habits", "list", [new("Habits paused", habits.Paused), new("Habits with a reminder", habits.WithReminder)]),
            ]),
            o.Fireside is { } fireside
                ? Panel("fireside", "Fireside", null,
                [
                    Stats("fireside-counts", "Counts", "list", [new("Users with Fireside on", fireside.UsersOn), new("Friendships", fireside.Friendships)]),
                    Bars("warmth", "Warmth per day", "day", ["Count"], Days(fireside.WarmthPerDay), "kept rows (30-day retention)"),
                ])
                : Panel("fireside", "Fireside", NotOnProduction, []),
            o.LetGoSlipsPerWeek is { } slips
                ? Panel("let-go", "Let go", null,
                [
                    Bars("slips", "Slips per week", "week", ["Slips"], slips.Select(w => new Bar(Date(w.Week), [w.Slips])), "sum of slip counts"),
                    Stats("slip-totals", "Totals", "list", [new("Slips, 12 weeks", slips.Sum(w => w.Slips)), new("Days with a slip, 12 weeks", slips.Sum(w => w.Days))]),
                ])
                : Panel("let-go", "Let go", NotOnProduction, []),
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
                    ? new("Overdue reminders", overdue, Hint: "Advancing", HintTone: "ok")
                    : new("Overdue reminders", overdue, Hint: $"{N(r.Overdue.Habits)} habits, {N(r.Overdue.Routines)} routines", HintTone: "bad"),
                new("Discord DMs on", r.Discord.RemindersOn),
                new("Second reminders on", second.UsersOn, Hint: $"{N(second.PendingNow)} pending now"),
                new("Push enabled", r.Push.Enabled, Hint: $"of {N(r.Push.Subscriptions)} subscriptions"),
            ]),
            Bars("open", "Still-open reminders per day", "day", ["Discord", "Push"], Channels(r.OpenPerDay),
                "Ember deletes reminder rows when they close, so these are only the ones still open"),
            r.History.Since is { } since
                ? Bars("sent", "Reminders sent per day", "day", ["Discord", "Push"], Channels(r.History.PerDay),
                    $"history since {Local(since)}; includes second reminders and resends")
                : Bars("sent", "Reminders sent per day", "day", ["Discord", "Push"], []) with { Unavailable = "No snapshots yet" },
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
            Section(s, "disk", "Disk", s.Disk is { } disk
                ? [new("Free", disk.FreeBytes, "bytes", Hint: Pct(disk.FreeBytes, disk.TotalBytes)), new("Total", disk.TotalBytes, "bytes")]
                : null),
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

    /// <summary>A completed run that did not succeed, as the web badge counts it.</summary>
    public static bool Failed(CiRun run) => run.Status == "completed" && run.Conclusion != "success";

    /// <summary>
    /// The summary card. A null source is a failed database read: its tiles are null, the state is bad and the first alert says so.
    /// </summary>
    public static Summary Summary(OverviewCounters? overview, ReminderCounters? reminders, FeedbackList? feedback, SystemReport system, DateTime now)
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

        var bad = databaseFailed || system.Services.Any(x => !x.Ok) || overdue > 0 || failedCi.Any(run => run.Branch == "main");
        var generatedAt = new[] { overview?.GeneratedAt, reminders?.GeneratedAt, feedback is null ? null : now, system.GeneratedAt }.Where(t => t is not null).Min()!.Value;
        return new(generatedAt, bad ? "bad" : statusError is not null ? "warn" : "ok",
        [
            new("Users", overview?.Users),
            new("Active 7 days", overview?.Active7),
            new("Overdue reminders", overdue, Tone: overdue > 0 ? "bad" : null),
            new("Open feedback", feedback?.Items.Count(i => i.Status == "open")),
        ],
        alerts.Take(5).ToList());
    }

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
        var reason = s.Errors.GetValueOrDefault(section) ?? (section is "disk" or "backup" or "deployed" ? s.Errors.GetValueOrDefault("status") : null);
        return "Unavailable" + (reason is null ? "" : $": {reason}");
    }

    private static StatsWidget Stats(string id, string title, string variant, IReadOnlyList<StatItem> items) =>
        new() { Id = id, Title = title, Variant = variant, Items = items };

    private static BarsWidget Bars(string id, string title, string axis, string[] series, IEnumerable<Bar> bars, string? note = null) =>
        new() { Id = id, Title = title, Axis = axis, Series = series.Select(l => new SeriesRef(l)).ToList(), Bars = bars.ToList(), Note = note };

    private static PanelWidget Panel(string id, string title, string? unavailable, IReadOnlyList<Widget> children) =>
        new() { Id = id, Title = title, Unavailable = unavailable, Children = children };

    private static IEnumerable<Bar> Days(IEnumerable<DayCount> days) => days.Select(d => new Bar(Date(d.Date), [d.Count]));

    private static IEnumerable<Bar> Channels(IEnumerable<ChannelDay> days) => days.Select(d => new Bar(Date(d.Date), [d.Discord, d.Push]));

    private static string Pct(double part, double whole) =>
        whole > 0 ? Math.Round(part * 100.0 / whole, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "%" : "0%";

    private static string N(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Iso(DateTime utc) => NoticeRules.FormatTime(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));

    private static string Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string Local(DateTime utc) => Local(new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)));
}
