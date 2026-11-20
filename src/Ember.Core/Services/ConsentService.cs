using System.Linq.Expressions;
using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Ember.Core.Services;

/// <summary>
/// Consent to health data, its withdrawal, and the deletion that follows. Holds the one definition of health data: habits (with their
/// logs, slips and notes), day marks, day notes, own moods and the "What helps me" list, the entities the data-lifecycle registry
/// (<see cref="Privacy.DataLifecycle"/>) marks <see cref="Privacy.WithdrawalRule.Deleted"/>. <see cref="HasHealthDataAsync"/> asks
/// whether a user holds any of it, withdrawing needs it (or a current consent), and <see cref="DeleteHealthDataAsync"/> and
/// <see cref="RepeatHealthDataDeletionAsync"/> remove exactly that.
/// </summary>
public class ConsentService
{
    /// <summary>
    /// Version of the privacy policy the health-data consent refers to. Consent given for an
    /// older version no longer counts, so changing this asks every user again.
    /// </summary>
    public const string CurrentConsentPolicyVersion = "2026-11-v2";

    /// <summary>Days after a "No thanks" before Ember asks for consent again on its own.</summary>
    public const int ConsentDeclineBackoffDays = 30;

    /// <summary>How long health data is kept after the consent was withdrawn or declined (<see cref="User.ConsentWithdrawnAt"/>).</summary>
    public static readonly TimeSpan WithdrawnDataLifetime = TimeSpan.FromDays(30);

    /// <summary>How long before the deletion the user is reminded, and the least time between that reminder and the deletion.</summary>
    public static readonly TimeSpan DeletionReminderLead = TimeSpan.FromDays(3);

    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;
    private readonly DeletionLedger _ledger;

    /// <param name="ledger">Where committed withdrawals and health-data deletions are recorded.</param>
    public ConsentService(EmberDbContext db, TimeProvider time, DeletionLedger ledger)
    {
        _db = db;
        _time = time;
        _ledger = ledger;
    }

    /// <summary>Whether the user agreed to the current health-data consent (<see cref="CurrentConsentPolicyVersion"/>) and hasn't withdrawn it.</summary>
    public async Task<bool> HasHealthDataConsentAsync(Guid userId)
        => await _db.Users.AnyAsync(u => u.Id == userId && u.ConsentWithdrawnAt == null
            && u.HealthDataConsentAt != null && u.ConsentPolicyVersion == CurrentConsentPolicyVersion);

    /// <summary>Whether the user has any health data: a habit, day mark, day note, mood or "What helps me" list.</summary>
    public async Task<bool> HasHealthDataAsync(Guid userId)
        => await _db.Users.Where(u => u.Id == userId).AnyAsync(HoldsHealthData);

    private static readonly Expression<Func<User, bool>> HoldsHealthData
        = u => u.Habits.Any() || u.DayMarks.Any() || u.DayNotes.Any() || u.Moods.Any() || u.Helps != null;

    private static readonly Expression<Func<User, bool>> HasCurrentConsent
        = u => u.HealthDataConsentAt != null && u.ConsentPolicyVersion == CurrentConsentPolicyVersion;

    private static Expression<Func<User, bool>> Or(Expression<Func<User, bool>> left, Expression<Func<User, bool>> right)
        => Expression.Lambda<Func<User, bool>>(
            Expression.OrElse(left.Body, ReplacingExpressionVisitor.Replace(right.Parameters[0], left.Parameters[0], right.Body)),
            left.Parameters);

    /// <summary>
    /// Records the user's explicit consent (now, for <see cref="CurrentConsentPolicyVersion"/>) and cancels a pending deletion by clearing
    /// <see cref="User.ConsentWithdrawnAt"/> and <see cref="User.DeletionReminderSentAt"/>.
    /// </summary>
    public async Task GiveHealthDataConsentAsync(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.HealthDataConsentAt, now)
                .SetProperty(u => u.ConsentPolicyVersion, CurrentConsentPolicyVersion)
                .SetProperty(u => u.ConsentWithdrawnAt, (DateTime?)null)
                .SetProperty(u => u.DeletionReminderSentAt, (DateTime?)null));
    }

    /// <summary>
    /// Withdraws the health-data consent: for a user with current consent or health data who hasn't withdrawn yet, sets
    /// <see cref="User.ConsentWithdrawnAt"/> to now (deletion follows after <see cref="WithdrawnDataLifetime"/>) and holds back the
    /// consent prompt like a decline. The past consent stays recorded. Records the withdrawal in the <see cref="DeletionLedger"/>. Returns
    /// false when there was nothing to withdraw.
    /// </summary>
    public async Task<bool> WithdrawHealthDataConsentAsync(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var lastQuietDay = (await GetLocalTodayAsync(userId)).AddDays(ConsentDeclineBackoffDays - 1);
        var withdrawn = await _db.Users
            .Where(u => u.Id == userId && u.ConsentWithdrawnAt == null)
            .Where(Or(HasCurrentConsent, HoldsHealthData))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.ConsentWithdrawnAt, now)
                .SetProperty(u => u.DeletionReminderSentAt, (DateTime?)null)
                .SetProperty(u => u.ConsentPromptedOn, lastQuietDay)) == 1;
        if (withdrawn) _ledger.Record(DeletionLedger.Withdraw, userId);
        return withdrawn;
    }

    /// <summary>
    /// Sets the consent back to withdrawn at <paramref name="withdrawnAt"/> unless it is withdrawn already, to repeat a recorded withdrawal
    /// after a backup restore; the deletion and its reminder then run from that time. Returns whether it changed the account.
    /// </summary>
    public async Task<bool> RepeatConsentWithdrawalAsync(Guid userId, DateTime withdrawnAt, CancellationToken cancellationToken = default)
        => await _db.Users
            .Where(u => u.Id == userId && u.ConsentWithdrawnAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.ConsentWithdrawnAt, withdrawnAt)
                .SetProperty(u => u.DeletionReminderSentAt, (DateTime?)null), cancellationToken) == 1;

    /// <summary>
    /// For a user who already has habits but no current consent: marks today's consent prompt as shown and
    /// returns true, at most once per local day and not within <see cref="ConsentDeclineBackoffDays"/> of a decline.
    /// Returns false for everyone else.
    /// </summary>
    public async Task<bool> ClaimConsentPromptAsync(Guid userId)
    {
        var today = await GetLocalTodayAsync(userId);
        return await _db.Users
            .Where(u => u.Id == userId && u.Habits.Any()
                && (u.HealthDataConsentAt == null || u.ConsentPolicyVersion != CurrentConsentPolicyVersion)
                && (u.ConsentPromptedOn == null || u.ConsentPromptedOn < today))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ConsentPromptedOn, today)) == 1;
    }

    /// <summary>Undoes <see cref="ClaimConsentPromptAsync"/> when the prompt couldn't be shown, so the next interaction asks again.</summary>
    public async Task ReleaseConsentPromptAsync(Guid userId)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ConsentPromptedOn, (DateOnly?)null));
    }

    /// <summary>
    /// Notes a "No thanks" to the consent prompt, so <see cref="ClaimConsentPromptAsync"/> waits <see cref="ConsentDeclineBackoffDays"/>
    /// before asking again, and withdraws like <see cref="WithdrawHealthDataConsentAsync"/> when there is anything to withdraw. Changes nothing
    /// while the consent is already withdrawn. Never creates a user.
    /// </summary>
    public async Task DeclineConsentPromptAsync(Guid userId)
    {
        if (await _db.Users.AnyAsync(u => u.Id == userId && u.ConsentWithdrawnAt != null)) return;
        if (await WithdrawHealthDataConsentAsync(userId)) return;
        var lastQuietDay = (await GetLocalTodayAsync(userId)).AddDays(ConsentDeclineBackoffDays - 1);
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ConsentPromptedOn, lastQuietDay));
    }

    /// <summary>Notes that the consent prompt was shown today, keeping a later decline back-off. Never creates a user.</summary>
    public async Task MarkConsentPromptedAsync(Guid userId)
    {
        var today = await GetLocalTodayAsync(userId);
        await _db.Users
            .Where(u => u.Id == userId && (u.ConsentPromptedOn == null || u.ConsentPromptedOn < today))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ConsentPromptedOn, today));
    }

    /// <summary>
    /// Deletes the user's health data (habits with their logs, slips and reminders, day marks, day notes, moods and "What helps me" list)
    /// when their consent is still withdrawn since <paramref name="cutoff"/> or earlier, checked under a row lock so agreeing again at the
    /// same time wins. Keeps the account, settings, routines, push subscriptions, journal key and Fireside. Records the deletion in the
    /// <see cref="DeletionLedger"/>. Returns whether it deleted.
    /// </summary>
    public async Task<bool> DeleteHealthDataAsync(Guid userId, DateTime cutoff, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""", cancellationToken);
        if (!await _db.Users.AnyAsync(u => u.Id == userId && u.ConsentWithdrawnAt != null && u.ConsentWithdrawnAt <= cutoff, cancellationToken))
            return false;

        await DeleteHealthRowsAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _ledger.Record(DeletionLedger.Health, userId);
        return true;
    }

    /// <summary>
    /// Deletes the same health data as <see cref="DeleteHealthDataAsync"/> whatever the consent, to repeat a recorded deletion after a
    /// backup restore. Returns whether the user had any.
    /// </summary>
    public async Task<bool> RepeatHealthDataDeletionAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlAsync($"""SELECT 1 FROM "AspNetUsers" WHERE "Id" = {userId} FOR UPDATE""", cancellationToken);
        if (!await _db.Users.Where(u => u.Id == userId).AnyAsync(HoldsHealthData, cancellationToken)) return false;

        await DeleteHealthRowsAsync(userId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task DeleteHealthRowsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await _db.Habits.Where(h => h.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await _db.DayMarks.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await _db.DayNotes.Where(n => n.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await _db.Moods.Where(m => m.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await _db.UserHelps.Where(h => h.UserId == userId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Ids of users with health data whose consent was withdrawn at or before <paramref name="withdrawnBefore"/> and who haven't had the
    /// deletion reminder yet.
    /// </summary>
    public Task<List<Guid>> GetDeletionReminderDueAsync(DateTime withdrawnBefore, CancellationToken cancellationToken = default)
        => _db.Users
            .Where(u => u.ConsentWithdrawnAt != null && u.ConsentWithdrawnAt <= withdrawnBefore && u.DeletionReminderSentAt == null)
            .Where(HoldsHealthData)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Claims the deletion reminder for the user's current withdrawal, at most once per withdrawal. Returns the withdrawal time for the one
    /// call that claimed it, or null when the consent isn't withdrawn or the reminder was already claimed.
    /// </summary>
    public async Task<DateTime?> ClaimDeletionReminderAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var withdrawnAt = await _db.Users.Where(u => u.Id == userId).Select(u => u.ConsentWithdrawnAt).FirstOrDefaultAsync(cancellationToken);
        if (withdrawnAt is null) return null;
        return await _db.Users
            .Where(u => u.Id == userId && u.ConsentWithdrawnAt == withdrawnAt && u.DeletionReminderSentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletionReminderSentAt, now), cancellationToken) == 1 ? withdrawnAt : null;
    }

    /// <summary>Gives back a claimed deletion reminder (<see cref="ClaimDeletionReminderAsync"/>) that reached nobody, so the next run tries again.</summary>
    public Task ReleaseDeletionReminderAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DeletionReminderSentAt, (DateTime?)null), cancellationToken);

    /// <summary>
    /// When (UTC) the health data of a user who withdrew at <paramref name="withdrawnAt"/> is deleted: <see cref="WithdrawnDataLifetime"/>
    /// later, but never sooner than <see cref="DeletionReminderLead"/> after the reminder claimed at <paramref name="reminderSentAt"/>.
    /// </summary>
    public static DateTime DeletionDueUtc(DateTime withdrawnAt, DateTime? reminderSentAt)
    {
        var due = withdrawnAt + WithdrawnDataLifetime;
        return reminderSentAt is { } sent && sent + DeletionReminderLead > due ? sent + DeletionReminderLead : due;
    }

    /// <summary>When the user withdrew and when their deletion reminder was claimed, or null while the consent isn't withdrawn.</summary>
    public async Task<(DateTime WithdrawnAt, DateTime? ReminderSentAt)?> GetConsentWithdrawalAsync(Guid userId)
    {
        var row = await _db.Users.Where(u => u.Id == userId && u.ConsentWithdrawnAt != null)
            .Select(u => new { u.ConsentWithdrawnAt, u.DeletionReminderSentAt }).FirstOrDefaultAsync();
        return row is null ? null : (row.ConsentWithdrawnAt!.Value, row.DeletionReminderSentAt);
    }

    /// <summary>
    /// Ids of users with health data whose consent was withdrawn at or before <paramref name="withdrawnBefore"/> and whose deletion reminder
    /// was claimed at or before <paramref name="remindedBefore"/>.
    /// </summary>
    public Task<List<Guid>> GetHealthDataDeletionDueAsync(DateTime withdrawnBefore, DateTime remindedBefore, CancellationToken cancellationToken = default)
        => _db.Users
            .Where(u => u.ConsentWithdrawnAt != null && u.ConsentWithdrawnAt <= withdrawnBefore
                && u.DeletionReminderSentAt != null && u.DeletionReminderSentAt <= remindedBefore)
            .Where(HoldsHealthData)
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);

    private async Task<DateOnly> GetLocalTodayAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return TimeZones.LocalToday(user?.Timezone, _time);
    }
}
