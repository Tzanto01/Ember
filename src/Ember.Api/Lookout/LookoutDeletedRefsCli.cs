using System.Globalization;
using Ember.Core;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Lookout;

/// <summary>
/// <c>--lookout-deleted-refs</c>: for restoring Lookout's store, prints every ref whose deletion Lookout must have, those confirmed
/// (<see cref="LookoutDeletedRefs"/>, from the file in <see cref="LookoutDeletedRefs.FileSetting"/>) and those still waiting in the
/// outbox of <c>ConnectionStrings:DefaultConnection</c>, one per line on stdout, each once, whatever its age. Then prints how many
/// and since when refs are recorded to stderr and exits 0. Prints nothing to stdout and exits 1 with the reason on stderr when the
/// setting is empty, the file can't be read or has a malformed line, or anything else fails.
/// </summary>
internal static class LookoutDeletedRefsCli
{
    /// <summary>Whether <paramref name="args"/> start with the verb.</summary>
    public static bool Handles(string[] args) => args is ["--lookout-deleted-refs", ..];

    /// <summary>Runs the verb in <paramref name="args"/> and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length != 1)
            {
                await Console.Error.WriteLineAsync("Usage: --lookout-deleted-refs");
                return 1;
            }
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (configuration[LookoutDeletedRefs.FileSetting] is not { } file || string.IsNullOrWhiteSpace(file))
            {
                await Console.Error.WriteLineAsync("The deleted refs are not configured, so no deletion is recorded.");
                return 1;
            }

            await using var db = new EmberDbContext(ServiceCollectionExtensions.EmberDbOptions(configuration));
            return await PrintAsync(db, file, Console.Out, Console.Error);
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Reads the outbox before the file: the sender records a ref in the file before it deletes the outbox row, so one moving
    /// meanwhile is in at least one of them.
    /// </summary>
    internal static async Task<int> PrintAsync(EmberDbContext db, string file, TextWriter output, TextWriter summary,
        CancellationToken cancellationToken = default)
    {
        var waiting = await db.LookoutOutbox.Where(o => o.Kind == LookoutOutbox.UserDelete).Select(o => o.UserRef).ToListAsync(cancellationToken);
        if (waiting.Any(r => r is null || !LookoutDeletedRefs.IsRef(r))) throw new InvalidDataException("A waiting deletion in the outbox has no valid ref.");
        var (since, recorded) = LookoutDeletedRefs.Read(file);

        var refs = recorded.Concat(waiting.Select(r => r!)).Distinct(StringComparer.Ordinal).ToList();
        await output.WriteAsync(string.Concat(refs.Select(r => r + "\n")));
        await output.FlushAsync(cancellationToken);
        await summary.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Total: {refs.Count}\nRecorded since {since:yyyy-MM-dd'T'HH:mm:ss'Z'}. Deletions before that are unknown."));
        return 0;
    }
}
