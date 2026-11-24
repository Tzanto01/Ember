using Ember.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Lookout;

/// <summary>The database rights <see cref="LookoutSender"/> and <see cref="LookoutDeletedRefsCli"/> use, as <c>ops/sql/ember_lookout.sql</c> grants them to the sender's own login.</summary>
internal static class LookoutSenderRights
{
    /// <summary>One privilege on one column of a table in <c>public</c>, or on the whole table when <paramref name="Column"/> is null.</summary>
    public sealed record Right(string Table, string Privilege, string? Column)
    {
        /// <summary><c>SELECT ("Id") ON "LookoutOutbox"</c>.</summary>
        public override string ToString() => $"{Privilege}{(Column is null ? "" : $" (\"{Column}\")")} ON \"{Table}\"";
    }

    /// <summary>Every right, and no other.</summary>
    public static readonly IReadOnlyList<Right> Required =
    [
        .. Columns("LookoutOutbox", "SELECT", "Id", "Kind", "Key", "FeedbackId", "UserRef", "SentAt", "ParkedAt"),
        .. Columns("LookoutOutbox", "UPDATE", "SentAt", "ParkedAt", "ParkedStatus"),
        new("LookoutOutbox", "DELETE", null),
        .. Columns("Feedback", "SELECT", "Id", "Platform", "Version", "Title", "Text", "CreatedAt"),
        new("Feedback", "DELETE", null),
        .. Columns("FeedbackImages", "SELECT", "Id", "FeedbackId", "ContentType", "Data"),
        new("FeedbackImages", "DELETE", null),
        .. Columns("LookoutDiagnostics", "SELECT", "Id", "Key", "Body", "CreatedAt", "NextAttemptAt", "Attempts"),
        .. Columns("LookoutDiagnostics", "UPDATE", "NextAttemptAt", "Attempts"),
        new("LookoutDiagnostics", "DELETE", null),
    ];

    /// <summary>The <see cref="Required"/> rights the login of <paramref name="db"/> lacks, also when the table or column doesn't exist.</summary>
    public static async Task<IReadOnlyList<Right>> MissingAsync(EmberDbContext db, CancellationToken cancellationToken)
    {
        var tables = Required.Select(r => r.Table).ToArray();
        var privileges = Required.Select(r => r.Privilege).ToArray();
        var columns = Required.Select(r => r.Column).ToArray();
        var missing = await db.Database.SqlQuery<int>($"""
            SELECT (r.n - 1)::int AS "Value"
            FROM unnest({tables}, {privileges}, {columns}) WITH ORDINALITY AS r(t, p, c, n)
            LEFT JOIN pg_class k ON k.relname = r.t AND k.relnamespace = 'public'::regnamespace
            LEFT JOIN pg_attribute a ON a.attrelid = k.oid AND a.attname = r.c AND NOT a.attisdropped
            WHERE NOT coalesce(CASE WHEN r.c IS NULL THEN has_table_privilege(k.oid, r.p) ELSE has_column_privilege(k.oid, a.attnum, r.p) END, false)
            """).ToListAsync(cancellationToken);
        return [.. missing.Order().Select(i => Required[i])];
    }

    private static IEnumerable<Right> Columns(string table, string privilege, params string[] columns) => columns.Select(c => new Right(table, privilege, c));
}
