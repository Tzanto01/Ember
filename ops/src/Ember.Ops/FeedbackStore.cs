using Npgsql;

namespace Ember.Ops;

/// <param name="Position">Sort key within the column, ascending: the stored card position, else minus the Unix seconds of
/// <c>CreatedAt</c>, so cards nobody moved yet sort newest first and a move can land between any two cards.</param>
public sealed record FeedbackItem(
    int Id, string Title, string Text, string Platform, string? Version, DateTime CreatedAt, string Status, DateTime? ResolvedAt,
    bool Anonymous, IReadOnlyList<Guid> ImageIds, int CommentCount, double Position);

public sealed record FeedbackList(IReadOnlyList<FeedbackItem> Items);

public sealed record Comment(long Id, string Author, string Body, DateTime CreatedAt);

public sealed record FeedbackImageData(string ContentType, byte[] Data);

/// <summary>The feedback board: reports and status in Ember, comments and card positions in emberops.</summary>
public static class FeedbackStore
{
    /// <summary>The statuses Ember stores, in board order.</summary>
    public static readonly IReadOnlyList<string> Statuses = ["open", "planned", "implemented", "released", "declined"];

    /// <summary>The image types Ember accepts; anything else is never served.</summary>
    public static readonly IReadOnlySet<string> ImageTypes = new HashSet<string> { "image/jpeg", "image/png", "image/webp" };

    public const int MaxCommentLength = 4000;

    /// <summary>Every report, ordered by position (unpositioned last) then newest first.</summary>
    public static async Task<FeedbackList> ListAsync(OpsDatabases db, CancellationToken ct)
    {
        var counts = new Dictionary<int, int>();
        var positions = new Dictionary<int, double>();
        await using (var connection = await db.Ops.OpenConnectionAsync(ct))
        await using (var reader = await Db.RunAsync(connection,
        [
            Db.Command("SELECT feedback_id, count(*)::int FROM comments GROUP BY 1"),
            Db.Command("SELECT feedback_id, position FROM card_positions"),
        ], ct))
        {
            while (await reader.ReadAsync(ct)) counts[reader.GetInt32(0)] = reader.GetInt32(1);
            await reader.NextResultAsync(ct);
            while (await reader.ReadAsync(ct)) positions[reader.GetInt32(0)] = reader.GetDouble(1);
        }

        var items = new List<FeedbackItem>();
        await using (var command = db.Ember.CreateCommand("""
            SELECT f."Id", f."Title", f."Text", f."Platform", f."Version", f."CreatedAt", f."Status", f."ResolvedAt", f."UserId" IS NULL,
                   coalesce(array_agg(i."Id" ORDER BY i."Id") FILTER (WHERE i."Id" IS NOT NULL), '{}')
            FROM "Feedback" f LEFT JOIN "FeedbackImages" i ON i."FeedbackId" = f."Id"
            GROUP BY f."Id"
            """))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetInt32(0);
                items.Add(new(id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetDateTime(5), reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetDateTime(7), reader.GetBoolean(8),
                    reader.GetFieldValue<Guid[]>(9), counts.GetValueOrDefault(id),
                    positions.TryGetValue(id, out var p) ? p : -new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc)).ToUnixTimeSeconds()));
            }
        }

        return new(items.OrderBy(i => i.Position).ThenByDescending(i => i.CreatedAt).ToList());
    }

    /// <summary>
    /// Moves a report: sets Ember's status (and <c>ResolvedAt</c> the way <c>--feedback-status</c> does), then the card position.
    /// False when the report does not exist. A null position clears it.
    /// </summary>
    public static async Task<bool> MoveAsync(OpsDatabases db, int id, string status, double? position, CancellationToken ct)
    {
        await using (var update = db.Ember.CreateCommand("""
            UPDATE "Feedback" SET "Status" = $2,
                "ResolvedAt" = CASE WHEN $2 IN ('released', 'declined') THEN coalesce("ResolvedAt", now()) ELSE NULL END
            WHERE "Id" = $1
            """))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = id });
            update.Parameters.Add(new NpgsqlParameter { Value = status });
            if (await update.ExecuteNonQueryAsync(ct) == 0) return false;
        }

        await using var command = position is { } value
            ? db.Ops.CreateCommand("""
                INSERT INTO card_positions (feedback_id, position) VALUES ($1, $2)
                ON CONFLICT (feedback_id) DO UPDATE SET position = excluded.position, updated_at = now()
                """)
            : db.Ops.CreateCommand("DELETE FROM card_positions WHERE feedback_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        if (position is { } p) command.Parameters.Add(new NpgsqlParameter { Value = p });
        await command.ExecuteNonQueryAsync(ct);
        return true;
    }

    /// <summary>The image, only when it belongs to that report and has an allowed type.</summary>
    public static async Task<FeedbackImageData?> ImageAsync(NpgsqlDataSource ember, int id, Guid imageId, CancellationToken ct)
    {
        await using var command = ember.CreateCommand("""SELECT "ContentType", "Data" FROM "FeedbackImages" WHERE "Id" = $1 AND "FeedbackId" = $2""");
        command.Parameters.Add(new NpgsqlParameter { Value = imageId });
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var type = reader.GetString(0);
        return ImageTypes.Contains(type) ? new(type, reader.GetFieldValue<byte[]>(1)) : null;
    }

    public static async Task<bool> ExistsAsync(NpgsqlDataSource ember, int id, CancellationToken ct)
    {
        await using var command = ember.CreateCommand("""SELECT EXISTS (SELECT FROM "Feedback" WHERE "Id" = $1)""");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        return (bool)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The comments on a report, oldest first.</summary>
    public static async Task<IReadOnlyList<Comment>> CommentsAsync(NpgsqlDataSource ops, int id, CancellationToken ct)
    {
        await using var command = ops.CreateCommand("SELECT id, author, body, created_at FROM comments WHERE feedback_id = $1 ORDER BY created_at, id");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        var comments = new List<Comment>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) comments.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetDateTime(3)));
        return comments;
    }

    public static async Task<Comment> AddCommentAsync(NpgsqlDataSource ops, int id, string author, string body, CancellationToken ct)
    {
        await using var command = ops.CreateCommand("INSERT INTO comments (feedback_id, author, body) VALUES ($1, $2, $3) RETURNING id, created_at");
        command.Parameters.Add(new NpgsqlParameter { Value = id });
        command.Parameters.Add(new NpgsqlParameter { Value = author });
        command.Parameters.Add(new NpgsqlParameter { Value = body });
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.RowAsync(ct);
        return new(reader.GetInt64(0), author, body, reader.GetDateTime(1));
    }
}
