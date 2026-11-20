using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>
/// Ember accounts: deleting one (<see cref="DeleteUserDataAsync"/>), the daily cleanup of stale signups and expired sessions, the export
/// loader, merging a bot-only account into a web account (<see cref="MergeBotOnlyAccountAsync"/>) and importing an export file into an
/// empty account (<see cref="ImportAsync"/>).
/// </summary>
public partial class AccountService
{
    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;
    private readonly HabitService _habits;
    private readonly ReminderService _reminders;
    private readonly FiresideService _fireside;
    private readonly DeletionLedger _ledger;

    /// <param name="db">The database; <paramref name="settings"/>, <paramref name="habits"/>, <paramref name="reminders"/> and <paramref name="fireside"/> must use the same one, so merge and import run in one transaction.</param>
    /// <param name="time">The clock.</param>
    /// <param name="consent">Answers whether the user agreed to store health data.</param>
    /// <param name="settings">Time zone and tips that merge and import use.</param>
    /// <param name="habits">The Discord user lookup that merge uses.</param>
    /// <param name="reminders">The per-user limits on push subscriptions and phones that merge applies; must use the same database.</param>
    /// <param name="fireside">The Fireside part of a merge.</param>
    /// <param name="ledger">Where committed account deletions are recorded.</param>
    public AccountService(EmberDbContext db, TimeProvider time, ConsentService consent, UserSettingsService settings, HabitService habits,
        ReminderService reminders, FiresideService fireside, DeletionLedger ledger)
    {
        _db = db;
        _time = time;
        _consent = consent;
        _settings = settings;
        _habits = habits;
        _reminders = reminders;
        _fireside = fireside;
        _ledger = ledger;
    }

    /// <summary>
    /// Permanently deletes the user; the database cascades to all their habits, logs, day marks, routines, journal entries, own moods, journal key, "What helps me" list
    /// (reminders live on the habits and routines), feedback with its Lookout rows, and Lookout refs. In the same transaction, with the user row locked (PostgreSQL),
    /// first queues one <see cref="LookoutOutbox.UserDelete"/> row per <see cref="LookoutUserRef"/> of the user, so Lookout deletes after every ticket already queued.
    /// Records a deleted account in the <see cref="DeletionLedger"/>.
    /// </summary>
    public Task DeleteUserDataAsync(Guid userId) => DeleteUserAsync(userId, onlyUnprotected: false);

    /// <summary>
    /// Deletes the user like <see cref="DeleteUserDataAsync(Guid)"/>, unless the account has a password or two-step sign-in
    /// (<see cref="IsProtectedAccountAsync"/>): such an account is deleted only in the web app, which asks for them. The check and the
    /// delete run in one transaction with the user row locked (PostgreSQL). Returns false, deleting nothing, for a protected account.
    /// </summary>
    public Task<bool> DeleteUnprotectedUserDataAsync(Guid userId) => DeleteUserAsync(userId, onlyUnprotected: true);

    /// <summary>Whether the account has a password or two-step sign-in, so deleting it needs the web app's step-up.</summary>
    public Task<bool> IsProtectedAccountAsync(Guid userId)
        => _db.Users.AnyAsync(u => u.Id == userId && (u.PasswordHash != null || u.TwoFactorEnabled));

    private async Task<bool> DeleteUserAsync(Guid userId, bool onlyUnprotected)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""");
        if (onlyUnprotected && await IsProtectedAccountAsync(userId)) return false;

        var refs = await _db.LookoutUserRefs.Where(r => r.UserId == userId).OrderBy(r => r.CreatedAt).ThenBy(r => r.Ref)
            .Select(r => r.Ref).ToListAsync();
        if (refs.Count > 0)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            _db.LookoutOutbox.AddRange(refs.Select(r => new LookoutOutbox { Kind = LookoutOutbox.UserDelete, UserRef = r, CreatedAt = now }));
            await _db.SaveChangesAsync();
        }

        var deleted = await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteDeleteAsync();
        await transaction.CommitAsync();
        if (deleted > 0) _ledger.Record(DeletionLedger.Account, userId);
        return true;
    }

    /// <summary>How long an email signup may stay unconfirmed before <see cref="DeleteStaleUnconfirmedAccountsAsync"/> removes it.</summary>
    public static readonly TimeSpan UnconfirmedAccountLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Permanently deletes email signups older than <see cref="UnconfirmedAccountLifetime"/> whose email was never confirmed and that
    /// hold nothing a user made: no Discord or other external login, habits, routines, day marks, notes, moods, push subscriptions
    /// or journal key. The creation time is read from the account's UUIDv7 ID; an account with any other ID version is kept.
    /// Returns how many accounts were deleted.
    /// </summary>
    public async Task<int> DeleteStaleUnconfirmedAccountsAsync(CancellationToken cancellationToken = default)
    {
        var deletable = _db.Users.Where(u => !u.EmailConfirmed && u.Email != null && u.PasswordHash != null
            && !u.Logins.Any() && !u.Habits.Any() && !u.Routines.Any() && !u.DayMarks.Any() && !u.DayNotes.Any()
            && !u.Moods.Any() && !u.PushSubscriptions.Any() && u.JournalKey == null);

        var cutoff = _time.GetUtcNow() - UnconfirmedAccountLifetime;
        var stale = (await deletable.Select(u => u.Id).ToListAsync(cancellationToken))
            .Where(id => id.Version == 7 && UuidV7Time(id) < cutoff)
            .ToList();
        if (stale.Count == 0) return 0;

        return await deletable.Where(u => stale.Contains(u.Id)).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes every expired sign-in session (<see cref="AuthSession"/>) and every phone whose session no longer exists. Returns how
    /// many sessions were deleted.
    /// </summary>
    public async Task<int> DeleteExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var deleted = await _db.AuthSessions.Where(s => s.ExpiresUtc <= now).ExecuteDeleteAsync(cancellationToken);
        await _db.ReminderPhones.Where(p => !_db.AuthSessions.Any(s => s.Id == p.SessionId)).ExecuteDeleteAsync(cancellationToken);
        return deleted;
    }

    private static DateTimeOffset UuidV7Time(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return DateTimeOffset.FromUnixTimeMilliseconds(System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(bytes) >>> 16);
    }

    /// <summary>
    /// The user with their logins, push subscriptions, phones, every habit, log, slip, day mark, routine, journal entry and own mood, and the
    /// journal key and "What helps me" list (untracked), or null when Ember stores nothing about them. Habit notes only with
    /// <paramref name="withHabitNotes"/> (the full export).
    /// </summary>
    public Task<User?> GetUserForExportAsync(Guid userId, bool withHabitNotes = false)
        => (withHabitNotes ? _db.Users.Include(u => u.Habits).ThenInclude(h => h.Notes) : (IQueryable<User>)_db.Users)
            .AsNoTracking()
            .Include(u => u.Logins)
            .Include(u => u.Habits).ThenInclude(h => h.Logs)
            .Include(u => u.Habits).ThenInclude(h => h.Slips)
            .Include(u => u.Habits).ThenInclude(h => h.MadeItDays)
            .Include(u => u.DayMarks)
            .Include(u => u.PushSubscriptions)
            .Include(u => u.ReminderPhones)
            .Include(u => u.Routines)
            .Include(u => u.DayNotes)
            .Include(u => u.Moods)
            .Include(u => u.JournalKey)
            .Include(u => u.Helps)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId);

    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.AnyAsync(u => u.Id == userId, cancellationToken);
}
