using System.Globalization;
using System.Text.Json;
using Ember.Core;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// <c>--lookout-import &lt;board.json|-&gt;</c>: the one-time import of the reports stored before the outbox into Lookout, run inside
/// the <c>lookout-sender</c> container against <c>ConnectionStrings:DefaultConnection</c> and the Lookout settings from the
/// environment. The board file (or stdin for <c>-</c>) is the ops board's <c>{"comments": [...], "positions": [...]}</c> as
/// exported with psql. Imports every <see cref="Feedback"/> without a <see cref="LookoutOutbox"/> row, with its status, resolve
/// time, comments and board order, under the key <c>import-feedback-{id}</c>, so a rerun sends the same bodies and Lookout answers
/// 200. Each request waits for <see cref="LookoutIngest.SendLockKey"/>, so it never overlaps one of the sender's. Prints one line per report without content; exit 0 when every report is imported or already imported, else 1. Refuses
/// with exit 1 when Lookout isn't configured. Any failure prints the exception type and message to stderr and exits 1.
/// </summary>
internal static class LookoutImportCli
{
    /// <summary>
    /// The first position sent: below every ticket the store already has. Lookout renumbers each column after every insert and
    /// takes positions up to 1,000,000.
    /// </summary>
    internal const int ImportPositionBase = 100_000;

    /// <summary>Most attempts per report on transient failures, waiting <see cref="LookoutIngest.Backoff"/> between them.</summary>
    internal const int MaxAttempts = 6;

    private static readonly JsonSerializerOptions BoardJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Whether <paramref name="args"/> start with the verb.</summary>
    public static bool Handles(string[] args) => args is ["--lookout-import", ..];

    /// <summary>Runs the import in <paramref name="args"/> and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is not ["--lookout-import", var file])
            {
                await Console.Error.WriteLineAsync("Usage: --lookout-import <board.json|->");
                return 1;
            }

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (!LookoutIngest.TryGetEndpoint(configuration, out var endpoint, out var keyFile))
            {
                await Console.Error.WriteLineAsync("Lookout ingest is not configured.");
                return 1;
            }

            Board board;
            await using (var input = file == "-" ? Console.OpenStandardInput() : File.OpenRead(file))
                board = await JsonSerializer.DeserializeAsync<Board>(input, BoardJson) ?? throw new InvalidDataException("The board file is empty.");

            var options = ServiceCollectionExtensions.EmberDbOptions(configuration);
            await using var db = new EmberDbContext(options);
            using var http = LookoutIngest.CreateClient();
            return await ImportAsync(db, http, endpoint, keyFile, board, Task.Delay, Console.Out, TimeProvider.System);
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Sends every report without an outbox row, column by column (status order) in the board's order within the column: stored
    /// position, else minus the creation time in Unix seconds, then newest first. When the sender's account was deleted while its
    /// report was being sent, queues a <see cref="LookoutOutbox.UserDelete"/> for its ref after the send. Returns the exit code.
    /// </summary>
    internal static async Task<int> ImportAsync(EmberDbContext db, HttpClient http, Uri endpoint, string keyFile, Board board,
        Func<TimeSpan, CancellationToken, Task> delay, TextWriter output, TimeProvider time, CancellationToken cancellationToken = default)
    {
        var reports = await db.Feedback.AsNoTracking().Include(f => f.Images.OrderBy(i => i.Id))
            .Where(f => !db.LookoutOutbox.Any(o => o.FeedbackId == f.Id))
            .ToListAsync(cancellationToken);
        var positions = board.Positions.ToDictionary(p => p.FeedbackId, p => p.Position);
        var comments = board.Comments.ToLookup(c => c.FeedbackId);

        var ok = true;
        foreach (var column in reports.GroupBy(f => f.Status).OrderBy(g => g.Key))
        {
            var ordered = column
                .OrderBy(f => positions.TryGetValue(f.Id, out var p) ? p : -new DateTimeOffset(DateTime.SpecifyKind(f.CreatedAt, DateTimeKind.Utc)).ToUnixTimeSeconds())
                .ThenByDescending(f => f.CreatedAt)
                .ToList();
            for (var index = 0; index < ordered.Count; index++)
            {
                var feedback = ordered[index];
                var import = new LookoutIngest.TicketImport(
                    feedback.Status.ToString().ToLowerInvariant(),
                    feedback.ResolvedAt is { } resolved ? LookoutIngest.Utc(resolved) : null,
                    ImportPositionBase + index,
                    [.. comments[feedback.Id].OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                        .Select(c => new LookoutIngest.TicketComment(c.Author, c.Body, LookoutIngest.Utc(c.CreatedAt.UtcDateTime)))]);
                var userRef = feedback.UserId is { } userId ? await UserRefAsync(db, userId, time, cancellationToken) : null;
                var body = LookoutIngest.TicketBody(feedback, userRef, import);
                var result = await SendAsync(db, http, endpoint, keyFile, LookoutIngest.TicketPath(string.Create(CultureInfo.InvariantCulture, $"import-feedback-{feedback.Id}")),
                    body, delay, cancellationToken);
                ok &= result is "imported" or "already imported";
                if (userRef is not null && !await db.Users.AnyAsync(u => u.Id == feedback.UserId, cancellationToken))
                {
                    db.LookoutOutbox.Add(new LookoutOutbox { Kind = LookoutOutbox.UserDelete, UserRef = userRef, CreatedAt = time.GetUtcNow().UtcDateTime });
                    await db.SaveChangesAsync(cancellationToken);
                }
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"Report {feedback.Id}: {result}"));
            }
        }
        return ok ? 0 : 1;
    }

    private static async Task<string> SendAsync(EmberDbContext db, HttpClient http, Uri endpoint, string keyFile, string path, byte[] body,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        var reason = "";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (attempt > 1) await delay(LookoutIngest.Backoff(attempt - 1), cancellationToken);
            try
            {
                using var response = await LookoutIngest.SendAsync(db, http, endpoint, keyFile, HttpMethod.Put, path, body, cancellationToken);
                var status = (int)response.StatusCode;
                if (status == 200) return "already imported";
                if (status is >= 200 and < 300) return "imported";
                if (LookoutIngest.IsRefused(status)) return string.Create(CultureInfo.InvariantCulture, $"refused {status}");
                reason = status.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (LookoutIngest.IsTransient(e, cancellationToken))
            {
                reason = e.GetType().Name;
            }
        }
        return "failed " + reason;
    }

    private static async Task<string> UserRefAsync(EmberDbContext db, Guid userId, TimeProvider time, CancellationToken cancellationToken)
    {
        var existing = await db.LookoutUserRefs.Where(r => r.UserId == userId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Ref)
            .Select(r => r.Ref).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null) return existing;

        var created = new LookoutUserRef { Ref = LookoutUserRef.NewRef(), UserId = userId, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.LookoutUserRefs.Add(created);
        await db.SaveChangesAsync(cancellationToken);
        return created.Ref;
    }

    /// <summary>The ops board export.</summary>
    internal sealed record Board(List<BoardComment> Comments, List<BoardPosition> Positions);

    /// <summary>One row of the ops <c>comments</c> table.</summary>
    internal sealed record BoardComment(long Id, int FeedbackId, string Author, string Body, DateTimeOffset CreatedAt);

    /// <summary>One row of the ops <c>card_positions</c> table.</summary>
    internal sealed record BoardPosition(int FeedbackId, double Position);
}
