using System.Globalization;
using Ember.Ops.Lookout;
using FluentAssertions;

namespace Ember.Ops.Tests;

public sealed class LookoutSummaryTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private const long Gb = 1024L * 1024 * 1024;

    private static OverviewCounters Overview() => new(Now, 6, 4, 5);

    private static FeedbackList Feedback(params string[] statuses) =>
        new(statuses.Select((s, i) => new FeedbackItem(i + 1, "t", "x", "web", null, Now, s, null, false, [], 0, i)).ToList());

    private static CiRun Ci(string branch, string conclusion = "failure", string status = "completed") =>
        new(branch, status, conclusion, "https://example.test/run", Now, "abc");

    private static HostDisk Disk(string mount, double freePercent) => new(mount, (long)(freePercent * Gb), 100 * Gb);

    private static SystemReport System(IReadOnlyList<ServiceState>? services = null, IReadOnlyList<HostDisk>? disks = null, IReadOnlyList<CiRun>? ci = null,
        string? statusError = null) =>
        new(Now, Now, services ?? [new("API", true, null)], null, disks ?? [Disk("/", 50)], null, null, ci ?? [Ci("main", "success")], null, null, null,
            statusError is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["status"] = statusError }, NightlyBackup: Nightly(8.5));

    private static BackupInfo Nightly(double ageHours) => new("pg_dumpall_20261118_033000.sql.gz", new(2026, 11, 18, 3, 30, 0, DateTimeKind.Utc), ageHours, 12_345_678);

    private static StatsWidget NightlyWidget(SystemReport report) =>
        LookoutPages.System(report).Widgets.OfType<StatsWidget>().Single(w => w.Id == "nightly-backup");

    private static Summary Build(SystemReport? system = null) => LookoutPages.Summary(Overview(), Feedback("open", "open", "planned"), system ?? System(), Now);

    [Fact]
    public void Everything_healthy_is_ok_without_alerts_and_counts_the_tiles()
    {
        var summary = Build();

        summary.State.Should().Be("ok");
        summary.Alerts.Should().BeEmpty();
        summary.Tiles.Select(t => t.Label).Should().Equal("Users", "Active 7 days", "API p95", "Open feedback");
        summary.Tiles.Select(t => t.Value?.ToString()).Should().Equal("6", "4", null, "2");
    }

    [Fact]
    public void A_host_status_error_alone_is_a_warning_alert()
    {
        var summary = Build(System(statusError: "status file missing"));

        summary.State.Should().Be("warn");
        summary.Alerts.Should().Equal(new Alert("Host status: status file missing", "warn", "system"));
    }

    [Fact]
    public void A_failed_ci_run_off_main_alerts_without_turning_the_state_bad()
    {
        var summary = Build(System(ci: [Ci("feature/x"), Ci("dev", "failure", "in_progress")]));

        summary.State.Should().Be("ok");
        summary.Alerts.Should().Equal(new Alert("CI failed on feature/x", "bad", "releases"));
    }

    [Fact]
    public void A_failed_main_run_is_bad()
    {
        var summary = Build(System(ci: [Ci("main")]));

        summary.State.Should().Be("bad");
        summary.Alerts.Select(a => a.Text).Should().Equal("CI failed on main");
    }

    [Fact]
    public void A_failing_service_alerts_with_its_detail_and_failing_reminders_advancing_as_reminders_not_advancing()
    {
        var summary = Build(System([new("Database", false, "connection refused"), new("Reminders advancing", false, "1,201 overdue"), new("Cache", false, null)]));

        summary.State.Should().Be("bad");
        summary.Alerts.Should().Equal(new Alert("Database: connection refused", "bad", "system"), new Alert("Cache: failing", "bad", "system"),
            new Alert("Reminders not advancing", "bad", "system"));
        summary.Tiles.Select(t => t.Label).Should().NotContain("Overdue reminders");
    }

    [Fact]
    public void A_failed_database_read_nulls_the_tiles_and_leads_with_its_alert()
    {
        var summary = LookoutPages.Summary(null, Feedback(), System([new("API", false, "down")]), Now);

        summary.State.Should().Be("bad");
        summary.Tiles.Select(t => t.Value?.ToString()).Should().Equal(null, null, null, "0");
        summary.Alerts.Select(a => a.Text).Should().Equal("Database unavailable", "API: down");
        LookoutPages.Summary(Overview(), null, System(), Now).Tiles.Single(t => t.Label == "Open feedback").Value.Should().BeNull();
    }

    [Theory]
    [InlineData(8, "warn", "warn")]
    [InlineData(4, "bad", "bad")]
    [InlineData(10, "ok", null)]
    [InlineData(5, "warn", "warn")]
    public void A_low_disk_alerts_in_its_tone_and_sets_the_state(double freePercent, string state, string? tone)
    {
        var summary = Build(System(disks: [Disk("/srv", freePercent)]));

        summary.State.Should().Be(state);
        if (tone is null) summary.Alerts.Should().BeEmpty();
        else summary.Alerts.Should().Equal(new Alert($"Disk /srv: {Math.Round(freePercent)}% free", tone, "system"));
    }

    [Fact]
    public void At_most_five_alerts_survive_in_the_order_service_reminders_ci_status_disk()
    {
        var summary = Build(System([new("API", false, "down"), new("Reminders advancing", false, "2 overdue")], [Disk("/a", 3), Disk("/b", 3), Disk("/c", 3)], [Ci("main")],
            "status file missing"));

        summary.Alerts.Select(a => a.Text).Should().Equal("API: down", "Reminders not advancing", "CI failed on main", "Host status: status file missing", "Disk /a: 3% free");
    }

    [Theory]
    [InlineData(0, 59, null)]
    [InlineData(1, 0, "Lookout outbox: 1 parked")]
    [InlineData(0, 61, "Lookout outbox: oldest pending 1 h")]
    public void Parked_outbox_rows_or_a_pending_row_over_an_hour_old_warn_on_the_system_page(int parked, int oldestMinutes, string? alert)
    {
        var summary = Build(System() with { Outbox = new(3, parked, TimeSpan.FromMinutes(oldestMinutes)) });

        summary.State.Should().Be(alert is null ? "ok" : "warn");
        if (alert is null) summary.Alerts.Should().BeEmpty();
        else summary.Alerts.Should().Equal(new Alert(alert, "warn", "system"));
    }

    [Theory]
    [InlineData(25.9, "ok", null)]
    [InlineData(26.1, "warn", "warn")]
    [InlineData(50, "warn", "warn")]
    [InlineData(50.1, "bad", "bad")]
    public void A_nightly_backup_past_26_hours_warns_and_past_50_is_bad_on_the_page_and_in_the_summary(double ageHours, string state, string? tone)
    {
        var report = System() with { NightlyBackup = Nightly(ageHours) };

        var summary = Build(report);

        summary.State.Should().Be(state);
        if (tone is null) summary.Alerts.Should().BeEmpty();
        else summary.Alerts.Should().Equal(new Alert($"Nightly backup overdue: {ageHours.ToString(CultureInfo.InvariantCulture)} h", tone, "system"));
        NightlyWidget(report).Items.Select(i => (i.Label, i.Value, i.Format, i.Tone)).Should().Equal(
            ("File", (object?)"pg_dumpall_20261118_033000.sql.gz", (string?)"code", (string?)null), ("Written", "2026-11-18T03:30:00Z", "time", null),
            ("Age", ageHours, "hours", tone), ("Size", 12_345_678L, "bytes", null));
    }

    [Fact]
    public void A_readable_host_status_without_a_nightly_backup_says_none_was_found_and_warns()
    {
        var report = System() with { NightlyBackup = null };

        var summary = Build(report);

        summary.State.Should().Be("warn");
        summary.Alerts.Should().Equal(new Alert("Nightly backup: none found", "warn", "system"));
        NightlyWidget(report).Unavailable.Should().Be("No nightly backup found in ~/backups/postgres/daily");
    }

    [Fact]
    public void An_unreadable_host_status_raises_only_its_own_alert_for_the_nightly_backup()
    {
        var report = System(statusError: "status file missing") with { StatusWrittenAt = null, NightlyBackup = null };

        Build(report).Alerts.Should().Equal(new Alert("Host status: status file missing", "warn", "system"));
        NightlyWidget(report).Unavailable.Should().Be("Unavailable: status file missing");
    }

    [Fact]
    public void The_outbox_widget_shows_the_counts_and_tones_each_warning()
    {
        var widget = LookoutPages.System(System() with { Outbox = new(4, 2, TimeSpan.FromMinutes(150)) }).Widgets.OfType<StatsWidget>().Single(w => w.Id == "outbox");

        widget.Items.Select(i => (i.Label, i.Value, i.Format, i.Tone)).Should().Equal(
            ("Pending", (object?)4, (string?)null, (string?)null), ("Parked", 2, null, "warn"), ("Oldest pending", 2.5, "hours", "warn"));
    }

    [Fact]
    public void An_empty_outbox_has_no_oldest_pending_age_and_no_warning()
    {
        var report = System() with { Outbox = new(0, 0, null) };
        var widget = LookoutPages.System(report).Widgets.OfType<StatsWidget>().Single(w => w.Id == "outbox");

        widget.Items.Select(i => (i.Label, i.Value, i.Tone)).Should().Equal(("Pending", (object?)0, (string?)null), ("Parked", 0, null), ("Oldest pending", null, null));
        Build(report).Alerts.Should().BeEmpty();
    }

    private static StatsWidget DisksWidget(SystemReport report) =>
        LookoutPages.System(report).Widgets.OfType<StatsWidget>().Single(w => w.Id == "disks");

    [Fact]
    public void The_disks_widget_has_one_byte_row_per_mount_with_the_free_share_as_its_hint()
    {
        var widget = DisksWidget(System(disks: [new("/", 16156483584, 117545504768), new("/srv/data", 3400 * Gb / 1000, 3600 * Gb / 1000)]));

        widget.Unavailable.Should().BeNull();
        widget.Items.Select(i => i.Label).Should().Equal("/", "/srv/data");
        widget.Items[0].Value.Should().Be(16156483584);
        widget.Items[0].Format.Should().Be("bytes");
        widget.Items[0].Hint.Should().Be("of 109 GB (14% free)");
    }

    [Theory]
    [InlineData(4, "bad")]
    [InlineData(8, "warn")]
    [InlineData(50, null)]
    public void A_disk_row_is_toned_by_its_free_share(double freePercent, string? tone) =>
        DisksWidget(System(disks: [Disk("/", freePercent)])).Items.Single().Tone.Should().Be(tone);

    [Fact]
    public void Without_disk_data_the_widget_is_unavailable_with_the_status_error()
    {
        var widget = DisksWidget(System(statusError: "status file missing") with { Disks = null });

        widget.Unavailable.Should().Be("Unavailable: status file missing");
        widget.Items.Should().BeEmpty();
    }
}
