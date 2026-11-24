using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ember.Core.Services;

namespace Ember.Api.Lookout;

/// <summary>
/// The <see cref="Core.Models.LookoutUserRef"/> refs whose deletion Lookout confirmed, so an operator can repeat the deletions in a
/// restored Lookout store: one JSON line <c>{"at","ref"}</c> per deletion, refs and UTC times only, in the file
/// <see cref="FileSetting"/> names, outside emberdb. Off when that setting is unset or empty. A new file starts with a
/// <c>{"at","kind":"start"}</c> line that pruning keeps. Written by the Lookout sender only.
/// </summary>
internal sealed partial class LookoutDeletedRefs(string? path, TimeProvider time)
{
    /// <summary>The configuration key of the file (<c>Lookout__DeletedRefsFile</c>).</summary>
    public const string FileSetting = "Lookout:DeletedRefsFile";

    /// <summary>How long <see cref="Prune"/> keeps a ref after Lookout confirmed its deletion.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string? _path = string.IsNullOrWhiteSpace(path) ? null : path;

    /// <summary>Whether no ref is recorded.</summary>
    public bool IsOff => _path is null;

    /// <summary>Creates the file with its start line when it is missing or empty. Does nothing when off.</summary>
    public void Start()
    {
        if (_path is null) return;
        File.Delete(Temporary(_path));
        Write(_path, "");
    }

    /// <summary>
    /// Appends <paramref name="userRef"/> with the current time and flushes it to disk; does nothing when off. Throws when that
    /// fails. Drops a last line without its line break first: that write was cut off, so its caller never took it as recorded.
    /// </summary>
    public void Append(string userRef)
    {
        if (_path is null) return;
        Write(_path, JsonSerializer.Serialize(new { at = Now(), @ref = userRef }) + "\n");
    }

    /// <summary>
    /// Drops the refs older than <see cref="Retention"/> and returns how many; keeps the start line and lines it can't read. Replaces
    /// the file in one step, so a crash leaves the old or the new one.
    /// </summary>
    public int Prune()
    {
        if (_path is null || !File.Exists(_path)) return 0;
        var cutoff = time.GetUtcNow().UtcDateTime - Retention;
        return LockedFile.Use(_path, FileMode.Open, FileAccess.Read, stream =>
        {
            var lines = Text(stream).Split('\n');
            var keep = lines.Where(l => Parse(l) is not { Ref: not null } entry || entry.At > cutoff).ToArray();
            if (keep.Length == lines.Length) return 0;
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
            using (var temporary = new FileStream(Temporary(_path), options))
            {
                temporary.Write(Encoding.UTF8.GetBytes(string.Join('\n', keep)));
                temporary.Flush(flushToDisk: true);
            }
            File.Move(Temporary(_path), _path, overwrite: true);
            return lines.Length - keep.Length;
        });
    }

    /// <summary>
    /// When the refs in the file at <paramref name="path"/> begin to be recorded, and every ref in it, also ones past
    /// <see cref="Retention"/>. Throws when the file can't be read or any line isn't a start or ref line.
    /// </summary>
    public static (DateTime Since, List<string> Refs) Read(string path)
        => LockedFile.Use(path, FileMode.Open, FileAccess.Read, stream =>
        {
            var lines = Text(stream).Split('\n');
            var entries = lines[..^1].Select((line, i) => Parse(line) is { } entry && entry.Ref is null == (i == 0)
                ? entry
                : throw new InvalidDataException($"Line {i + 1} of the deleted refs is not an entry.")).ToList();
            if (lines[^1].Length > 0) throw new InvalidDataException($"Line {lines.Length} of the deleted refs is cut off.");
            if (entries.Count == 0) throw new InvalidDataException("The deleted refs have no start line.");
            return (entries[0].At, entries.Skip(1).Select(e => e.Ref!).ToList());
        });

    /// <summary>Whether <paramref name="userRef"/> is a ref as Lookout takes it.</summary>
    public static bool IsRef(string userRef) => RefPattern().IsMatch(userRef);

    private void Write(string file, string line) => LockedFile.Use(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, stream =>
    {
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var end = Array.LastIndexOf(bytes, (byte)'\n') + 1;
        stream.SetLength(end);
        stream.Seek(end, SeekOrigin.Begin);
        stream.Write(Encoding.UTF8.GetBytes((end == 0 ? JsonSerializer.Serialize(new { at = Now(), kind = "start" }) + "\n" : "") + line));
        stream.Flush(flushToDisk: true);
        return 0;
    }, OwnerOnly);

    private string Now() => time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Temporary(string file) => file + ".tmp";

    private static string Text(FileStream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static Entry? Parse(string line)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(line, JsonSerializerOptions.Web);
            return entry is { At.Kind: DateTimeKind.Utc, Kind: "start", Ref: null } or { At.Kind: DateTimeKind.Utc, Kind: null, Ref: not null }
                && (entry.Ref is null || IsRef(entry.Ref)) ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record Entry(DateTime At, string? Kind = null, string? Ref = null);

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{8,64}\z")]
    private static partial Regex RefPattern();
}
