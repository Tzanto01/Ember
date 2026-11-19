using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ember.Core.Services;

/// <summary>
/// The list of committed deletions and consent withdrawals an operator repeats after restoring a backup: one JSON line
/// <c>{"at","kind","userId"}</c> per account deletion, health-data deletion or consent withdrawal, ids and UTC times only, in the file <see cref="FileSetting"/> names. Off when that
/// setting is unset or empty. A new file starts with a <see cref="Start"/> line <c>{"at","kind"}</c> that pruning keeps, so a replay
/// knows how far back the ledger reaches. The bot and the API append to the same file, so every access holds an exclusive lock on it.
/// </summary>
public sealed class DeletionLedger(string? path, TimeProvider time, ILogger<DeletionLedger> logger)
{
    /// <summary>The configuration key of the ledger file (<c>Ember__DeletionLedgerFile</c>).</summary>
    public const string FileSetting = "Ember:DeletionLedgerFile";

    /// <summary><see cref="Entry.Kind"/> of a deleted account.</summary>
    public const string Account = "account";

    /// <summary><see cref="Entry.Kind"/> of deleted health data.</summary>
    public const string Health = "health";

    /// <summary><see cref="Entry.Kind"/> of a withdrawn health-data consent (also a "No thanks" that withdraws).</summary>
    public const string Withdraw = "withdraw";

    /// <summary><see cref="Entry.Kind"/> of the first line, when the ledger began; its <see cref="Entry.UserId"/> is empty.</summary>
    public const string Start = "start";

    /// <summary>How long <see cref="Prune"/> keeps an entry.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(40);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One entry: when it committed (UTC), <see cref="Account"/>, <see cref="Health"/>, <see cref="Withdraw"/> or <see cref="Start"/>, and whose.</summary>
    public sealed record Entry(DateTime At, string Kind, Guid UserId);

    /// <summary>The ledger file, or null when the ledger is off.</summary>
    public string? Path { get; } = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>
    /// Appends an entry for now, after the <see cref="Start"/> line when the file is new and after a line break when the last write
    /// was torn, and flushes it to disk. A failure logs a warning without the id and is not thrown.
    /// </summary>
    public void Record(string kind, Guid userId)
    {
        if (Path is null) return;
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            var line = JsonSerializer.Serialize(new Entry(now, kind, userId), Json) + "\n";
            Locked(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, stream =>
            {
                var prefix = "";
                if (stream.Length == 0) prefix = JsonSerializer.Serialize(new { at = now, kind = Start }) + "\n";
                else
                {
                    stream.Seek(-1, SeekOrigin.End);
                    if (stream.ReadByte() != '\n') prefix = "\n";
                }
                stream.Seek(0, SeekOrigin.End);
                stream.Write(Encoding.UTF8.GetBytes(prefix + line));
                stream.Flush(flushToDisk: true);
                return 0;
            });
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not write a deletion ({Kind}) to the deletion ledger: {ExceptionType}.", kind, e.GetType().Name);
        }
    }

    /// <summary>
    /// Drops the entries older than <see cref="Retention"/> and returns how many it dropped; keeps the <see cref="Start"/> line and lines
    /// it can't read. Writes the kept lines over the start of the file before cutting it short, so a crash midway leaves every kept line
    /// whole in either the new or the old part. A failure logs a warning and returns 0.
    /// </summary>
    public int Prune()
    {
        if (Path is null || !File.Exists(Path)) return 0;
        var cutoff = time.GetUtcNow().UtcDateTime - Retention;
        try
        {
            return Locked(Path, FileMode.Open, FileAccess.ReadWrite, stream =>
            {
                var lines = ReadLines(stream);
                var keep = lines.Where(l => TryParse(l) is not { } entry || entry.Kind == Start || entry.At >= cutoff).ToList();
                if (keep.Count == lines.Count) return 0;
                var kept = Encoding.UTF8.GetBytes(string.Concat(keep.Select(l => l + "\n")));
                stream.Seek(0, SeekOrigin.Begin);
                stream.Write(kept);
                stream.Flush(flushToDisk: true);
                stream.SetLength(kept.Length);
                stream.Flush(flushToDisk: true);
                return lines.Count - keep.Count;
            });
        }
        catch (Exception e)
        {
            logger.LogWarning("Could not prune the deletion ledger: {ExceptionType}.", e.GetType().Name);
            return 0;
        }
    }

    /// <summary>Every entry of the ledger at <paramref name="ledgerPath"/>. Throws when the file is missing or any line isn't an entry.</summary>
    public static List<Entry> Read(string ledgerPath) => Locked(ledgerPath, FileMode.Open, FileAccess.Read, stream =>
        ReadLines(stream).Select((line, i) => TryParse(line) ?? throw new InvalidDataException($"Line {i + 1} of the deletion ledger is not an entry.")).ToList());

    private static List<string> ReadLines(FileStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd().Split('\n').Where(l => l.Trim().Length > 0).ToList();
    }

    private static Entry? TryParse(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<Entry>(line, Json) is { Kind: Account or Health or Withdraw or Start } entry
                && (entry.Kind == Start) == (entry.UserId == Guid.Empty) && entry.At.Kind == DateTimeKind.Utc ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Opens the file with an exclusive lock, waiting up to 5 seconds while another process holds it.</summary>
    private static T Locked<T>(string file, FileMode mode, FileAccess access, Func<FileStream, T> use)
    {
        for (var attempt = 1; ; attempt++)
        {
            FileStream stream;
            try
            {
                stream = new FileStream(file, mode, access, FileShare.None);
            }
            catch (IOException e) when (e is not (FileNotFoundException or DirectoryNotFoundException) && attempt < 50)
            {
                Thread.Sleep(100);
                continue;
            }
            using (stream) return use(stream);
        }
    }
}
