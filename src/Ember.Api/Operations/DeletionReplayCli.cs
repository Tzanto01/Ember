using System.Globalization;
using System.Text.RegularExpressions;
using Ember.Core;
using Ember.Core.Data;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api.Operations;

/// <summary>
/// <c>--replay-deletions &lt;since&gt;</c>: after restoring a backup taken at <c>since</c> (UTC ISO 8601 with <c>Z</c> or an offset),
/// repeats every <see cref="DeletionLedger"/> entry at or after it that still applies, against
/// <c>ConnectionStrings:DefaultConnection</c> and the ledger file in <see cref="DeletionLedger.FileSetting"/> from the environment.
/// Prints counts only; exit 0. Refuses with exit 1, before touching the database, when <c>since</c> is not such a time, the ledger
/// setting is empty, the file is missing or has a line that isn't an entry, or <see cref="Refusal"/> refuses <c>since</c>. Any failure prints the exception
/// type and message to stderr and exits 1.
/// </summary>
internal static partial class DeletionReplayCli
{
    /// <summary>Whether <paramref name="args"/> start with the verb.</summary>
    public static bool Handles(string[] args) => args is ["--replay-deletions", ..];

    /// <summary>Runs the replay in <paramref name="args"/> and returns the exit code.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args is not ["--replay-deletions", var text] || !IsoTime().IsMatch(text)
                || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var since))
            {
                await Console.Error.WriteLineAsync("Usage: --replay-deletions <backup time, UTC ISO 8601, e.g. 2026-11-19T03:00:00Z>");
                return 1;
            }
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (configuration[DeletionLedger.FileSetting] is not { Length: > 0 } ledger)
            {
                await Console.Error.WriteLineAsync("The deletion ledger is not configured.");
                return 1;
            }
            var entries = DeletionLedger.Read(ledger);
            if (Refusal(entries, since.UtcDateTime, TimeProvider.System.GetUtcNow().UtcDateTime) is { } refusal)
            {
                await Console.Error.WriteLineAsync(refusal);
                return 1;
            }

            var options = ServiceCollectionExtensions.EmberDbOptions(configuration);
            await using var db = new EmberDbContext(options);
            return await ReplayAsync(db, entries, since.UtcDateTime, Console.Out, TimeProvider.System);
        }
        catch (Exception e)
        {
            await Console.Error.WriteLineAsync($"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Why <paramref name="since"/> can't be replayed in full, or null: it lies in the future, before the ledger's
    /// <see cref="DeletionLedger.Start"/> line, or more than <see cref="DeletionLedger.Retention"/> ago, where pruning has removed entries.
    /// </summary>
    internal static string? Refusal(IReadOnlyList<DeletionLedger.Entry> entries, DateTime since, DateTime now)
    {
        if (since > now) return "The backup time lies in the future.";
        if (since < now - DeletionLedger.Retention)
            return string.Create(CultureInfo.InvariantCulture, $"The backup is more than {DeletionLedger.Retention.TotalDays} days old; the ledger no longer holds every deletion since then.");
        var start = entries.Where(e => e.Kind == DeletionLedger.Start).Select(e => (DateTime?)e.At).Min();
        return start is null || since < start ? "The ledger started after the backup time, so it doesn't hold every deletion since then." : null;
    }

    /// <summary>
    /// Repeats the <paramref name="entries"/> at or after <paramref name="since"/> in time order, for accounts that still exist: an
    /// account is deleted the normal way (<see cref="HabitService.DeleteUserDataAsync(Guid)"/>, which queues its Lookout deletion); a
    /// withdrawal or a health-data deletion sets a consent that isn't withdrawn back to withdrawn at the entry's time
    /// (<see cref="HabitService.RepeatConsentWithdrawalAsync"/>), and a health-data deletion then deletes any health data again
    /// (<see cref="HabitService.RepeatHealthDataDeletionAsync"/>). Records nothing in the ledger. Prints the counts and returns 0.
    /// </summary>
    internal static async Task<int> ReplayAsync(EmberDbContext db, IReadOnlyList<DeletionLedger.Entry> entries, DateTime since, TextWriter output,
        TimeProvider time, CancellationToken cancellationToken = default)
    {
        var habits = new HabitService(db, time);
        int accounts = 0, health = 0, withdrawals = 0, done = 0;
        foreach (var entry in entries.Where(e => e.At >= since && e.Kind != DeletionLedger.Start).OrderBy(e => e.At))
        {
            if (!await db.Users.AnyAsync(u => u.Id == entry.UserId, cancellationToken))
            {
                done++;
                continue;
            }
            if (entry.Kind == DeletionLedger.Account)
            {
                await habits.DeleteUserDataAsync(entry.UserId);
                accounts++;
                continue;
            }
            var changed = false;
            if (await habits.RepeatConsentWithdrawalAsync(entry.UserId, entry.At, cancellationToken))
            {
                withdrawals++;
                changed = true;
            }
            if (entry.Kind == DeletionLedger.Health && await habits.RepeatHealthDataDeletionAsync(entry.UserId, cancellationToken))
            {
                health++;
                changed = true;
            }
            if (!changed) done++;
        }
        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Deleted again: {accounts} accounts, health data of {health} users. Withdrawn again: {withdrawals} consents. Already done: {done}."));
        return 0;
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex IsoTime();
}
