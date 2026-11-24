using System.Text.Json;
using Ember.Core.Models;
using Ember.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ember.Api.Tests;

/// <summary>The SQL of the release-day check: <c>tools/feedback-move/export.sql</c> and <c>ops/sql/mark_imported_feedback.sql</c>.</summary>
public sealed class FeedbackMoveScriptsTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private readonly PostgresFixture _postgres = postgres;

    private string Connection => Host.Services.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")!;

    private static string Script(params string[] path)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Combine([dir.FullName, .. path]) is var script && File.Exists(script)) return script;
        throw new FileNotFoundException(Path.Combine(path) + " not found above the test binaries");
    }

    private Task MarkAsync(string ids) => _postgres.RunScriptAsync(Connection, Script("ops", "sql", "mark_imported_feedback.sql"), "FEEDBACK_IDS", ids);

    private async Task<JsonElement> ExportAsync(string? before = null)
    {
        await using var connection = new NpgsqlConnection(Connection);
        await connection.OpenAsync();
        if (before is not null) await new NpgsqlCommand(before, connection).ExecuteNonQueryAsync();
        await using var command = new NpgsqlCommand(await File.ReadAllTextAsync(Script("tools", "feedback-move", "export.sql")), connection);
        return JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!).RootElement;
    }

    private async Task<int> ReportAsync(Guid? userId, string title, bool queued)
    {
        await Host.Run<FeedbackService>(s => s.SaveFeedbackAsync(userId, "web", "1.0", "text of " + title, default, title,
            [new FeedbackImage { ContentType = "image/png", Data = SyntheticImages.Png() }]));
        return await Host.Db(async db =>
        {
            var id = await db.Feedback.Where(f => f.Title == title).Select(f => f.Id).SingleAsync();
            if (!queued) await db.LookoutOutbox.Where(o => o.FeedbackId == id).ExecuteDeleteAsync();
            return id;
        });
    }

    [DockerFact]
    public async Task Marking_gives_only_the_named_reports_without_a_row_a_sent_one_and_a_rerun_adds_none()
    {
        var sender = await Host.Service(s => s.ResolveOrCreateDiscordUserAsync(ApiHost.NewDiscordId()));
        await Host.Db(async db =>
        {
            db.LookoutUserRefs.Add(new LookoutUserRef { UserId = sender, Ref = "oldest-ref-000001", CreatedAt = DateTime.UtcNow.AddDays(-9) });
            return await db.SaveChangesAsync();
        });
        var linked = await ReportAsync(sender, "linked", queued: false);
        var anonymous = await ReportAsync(null, "anonymous", queued: false);
        var other = await ReportAsync(null, "other", queued: false);
        var queued = await ReportAsync(null, "queued", queued: true);

        await MarkAsync($"{linked},{anonymous},{queued}");
        await MarkAsync($"{linked},{anonymous},{queued}");

        var rows = await Host.Db(db => db.LookoutOutbox.AsNoTracking().ToListAsync());
        rows.Where(r => r.SentAt != null).Select(r => (r.FeedbackId, r.Kind, r.Key, r.UserRef)).Should().BeEquivalentTo([
            ((int?)linked, LookoutOutbox.Ticket, $"import-feedback-{linked}", (string?)"oldest-ref-000001"),
            ((int?)anonymous, LookoutOutbox.Ticket, $"import-feedback-{anonymous}", (string?)null)]);
        rows.Where(r => r.SentAt == null).Select(r => r.FeedbackId).Should().Equal(queued);
        rows.Should().NotContain(r => r.FeedbackId == other);
    }

    [DockerFact]
    public async Task Marking_changes_nothing_when_the_list_is_empty_names_an_unknown_report_or_is_malformed()
    {
        var id = await ReportAsync(null, "only", queued: false);

        foreach (var ids in new[] { "", $"{id},999999", $"{id};1" })
            await FluentActions.Awaiting(() => MarkAsync(ids)).Should().ThrowAsync<InvalidOperationException>(ids);

        (await Host.Db(db => db.LookoutOutbox.CountAsync())).Should().Be(0);
    }

    [DockerFact]
    public async Task The_export_gives_status_only_on_the_old_schema_and_never_a_title_or_text()
    {
        var id = await ReportAsync(null, "secret title", queued: false);
        var queued = await ReportAsync(null, "queued", queued: true);
        var size = SyntheticImages.Png().Length;

        var now = await ExportAsync();
        var old = await ExportAsync($"ALTER TABLE \"Feedback\" ADD \"Status\" varchar(16) DEFAULT 'planned'; UPDATE \"Feedback\" SET \"CreatedAt\" = '2026-01-02 03:04:05.123999+00' WHERE \"Id\" = {id}");

        now[0].GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
        old[0].GetProperty("status").GetString().Should().Be("planned");
        old[0].GetProperty("createdAt").GetString().Should().Be("2026-01-02T03:04:05.123Z");
        old[0].GetProperty("pictures").EnumerateArray().Select(p => p.GetInt32()).Should().Equal(size);
        old.EnumerateArray().Select(r => (r.GetProperty("id").GetInt32(), r.GetProperty("delivery").GetString())).Should().Equal((id, null), (queued, "waiting"));
        old.GetRawText().Should().NotContain("secret title").And.NotContain("text of");
    }
}
