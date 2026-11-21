using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Bot only: the reminder dispatch. These loaders and writes take ids regardless of owner; API handlers never call them.</summary>
public partial class ReminderService
{
    /// <summary>Id and due time of each habit whose next reminder is due at or before <paramref name="nowUtc"/>, skipping users who withdrew consent.</summary>
    public async Task<List<(int Id, DateTime DueAt)>> GetDueReminderHabitsAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        => (await _db.Habits
            .Where(h => h.NextReminderUtc != null && h.NextReminderUtc <= nowUtc && h.User.ConsentWithdrawnAt == null)
            .Select(h => new { h.Id, DueAt = h.NextReminderUtc!.Value })
            .ToListAsync(cancellationToken))
            .Select(h => (h.Id, h.DueAt)).ToList();

    /// <summary>
    /// The habit (tracked) with its routine, its user and the user's logins and push subscriptions, regardless of owner.
    /// Persist edits with <see cref="SaveChangesAsync"/>.
    /// </summary>
    public Task<Habit?> GetHabitWithUserAsync(int habitId, CancellationToken cancellationToken = default)
        => _db.Habits
            .Include(h => h.Routine)
            .Include(h => h.User).ThenInclude(u => u.Logins)
            .Include(h => h.User).ThenInclude(u => u.PushSubscriptions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == habitId, cancellationToken);

    public Task<bool> IsCompletedOnAsync(int habitId, DateOnly date, CancellationToken cancellationToken = default)
        => _db.HabitLogs.AnyAsync(l => l.HabitId == habitId && l.Date == date && l.Completed, cancellationToken);

    /// <summary>Completed logs of the habit dated strictly between <paramref name="after"/> and <paramref name="before"/>.</summary>
    public Task<int> CountCompletedBetweenAsync(int habitId, DateOnly after, DateOnly before, CancellationToken cancellationToken = default)
        => _db.HabitLogs.CountAsync(l => l.HabitId == habitId && l.Date > after && l.Date < before && l.Completed, cancellationToken);

    /// <summary>Completed logs of the habit in the Monday–Sunday week (<see cref="HabitRules.WeekStart"/>) of the user's local <paramref name="today"/>.</summary>
    public Task<int> CountCompletedInWeekAsync(int habitId, DateOnly today, CancellationToken cancellationToken = default)
    {
        var monday = HabitRules.WeekStart(today);
        return CountCompletedBetweenAsync(habitId, monday.AddDays(-1), monday.AddDays(7), cancellationToken);
    }

    /// <summary>Saves pending changes to entities loaded through this service.</summary>
    public Task SaveChangesAsync(CancellationToken cancellationToken = default) => _db.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Moves the habit's next reminder directly in the database to the first occurrence of any of its reminder times after
    /// <paramref name="nowUtc"/> in the user's timezone, or clears it when the habit has no reminder time.
    /// </summary>
    public async Task DeferReminderAsync(int habitId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var row = await _db.Habits.Where(h => h.Id == habitId)
            .Select(h => new { h.ReminderTimes, h.User.Timezone })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return;

        var next = TimeZones.NextOccurrenceUtc(row.ReminderTimes, TimeZones.Find(row.Timezone) ?? TimeZoneInfo.Utc, nowUtc);
        await _db.Habits
            .Where(h => h.Id == habitId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.NextReminderUtc, next), cancellationToken);
    }

    /// <summary>
    /// Saves pending changes and, in the same transaction, moves the habit's next reminder to <paramref name="nextUtc"/> and its
    /// second reminder to <paramref name="secondUtc"/> directly in the database, but only while the next reminder is still
    /// <paramref name="dueAtUtc"/>. False when an edit or snooze moved it meanwhile; that change is kept, second reminder included.
    /// </summary>
    public async Task<bool> SaveAndAdvanceReminderAsync(int habitId, DateTime dueAtUtc, DateTime? nextUtc, DateTime? secondUtc,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var advanced = await _db.Habits
            .Where(h => h.Id == habitId && h.NextReminderUtc == dueAtUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.NextReminderUtc, nextUtc).SetProperty(h => h.SecondReminderUtc, secondUtc),
                cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return advanced;
    }

    /// <summary>Id and due time of each routine whose next reminder is due at or before <paramref name="nowUtc"/>, skipping users who withdrew consent.</summary>
    public async Task<List<(int Id, DateTime DueAt)>> GetDueReminderRoutinesAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        => (await _db.Routines
            .Where(r => r.NextReminderUtc != null && r.NextReminderUtc <= nowUtc && r.User.ConsentWithdrawnAt == null)
            .Select(r => new { r.Id, DueAt = r.NextReminderUtc!.Value })
            .ToListAsync(cancellationToken))
            .Select(r => (r.Id, r.DueAt)).ToList();

    /// <summary>
    /// The routine (tracked) with its member habits and their logs of the last two UTC days onward (enough for any local today),
    /// and its user with logins and push subscriptions, regardless of owner. Persist edits with <see cref="SaveChangesAsync"/>.
    /// </summary>
    public Task<Routine?> GetRoutineWithUserAsync(int routineId, CancellationToken cancellationToken = default)
    {
        var since = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(-2);
        return _db.Routines
            .Include(r => r.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= since))
            .Include(r => r.User).ThenInclude(u => u.Logins)
            .Include(r => r.User).ThenInclude(u => u.PushSubscriptions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(r => r.Id == routineId, cancellationToken);
    }

    /// <summary>
    /// Moves the routine's next reminder directly in the database to the first occurrence of its reminder time after
    /// <paramref name="nowUtc"/> in the user's timezone, or clears it when the routine has no reminder time.
    /// </summary>
    public async Task DeferRoutineReminderAsync(int routineId, DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var row = await _db.Routines.Where(r => r.Id == routineId)
            .Select(r => new { r.ReminderTime, r.User.Timezone })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null) return;

        DateTime? next = row.ReminderTime is { } time
            ? TimeZones.NextOccurrenceUtc(time, TimeZones.Find(row.Timezone) ?? TimeZoneInfo.Utc, nowUtc)
            : null;
        await _db.Routines
            .Where(r => r.Id == routineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextReminderUtc, next), cancellationToken);
    }

    /// <summary>
    /// Saves pending changes and, in the same transaction, moves the routine's next reminder to <paramref name="nextUtc"/> and its
    /// second reminder to <paramref name="secondUtc"/> directly in the database, but only while the next reminder is still
    /// <paramref name="dueAtUtc"/>. False when an edit moved it meanwhile; that change is kept.
    /// </summary>
    public async Task<bool> SaveAndAdvanceRoutineReminderAsync(int routineId, DateTime dueAtUtc, DateTime? nextUtc, DateTime? secondUtc,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        var advanced = await _db.Routines
            .Where(r => r.Id == routineId && r.NextReminderUtc == dueAtUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextReminderUtc, nextUtc).SetProperty(r => r.SecondReminderUtc, secondUtc),
                cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return advanced;
    }

    /// <summary>Ids of habits whose pending second reminder is due at or before <paramref name="nowUtc"/>, skipping users who withdrew consent.</summary>
    public Task<List<int>> GetDueSecondReminderHabitIdsAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        => _db.Habits
            .Where(h => h.SecondReminderUtc != null && h.SecondReminderUtc <= nowUtc && h.User.ConsentWithdrawnAt == null)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Ids of routines whose pending second reminder is due at or before <paramref name="nowUtc"/>, skipping users who withdrew consent.</summary>
    public Task<List<int>> GetDueSecondReminderRoutineIdsAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
        => _db.Routines
            .Where(r => r.SecondReminderUtc != null && r.SecondReminderUtc <= nowUtc && r.User.ConsentWithdrawnAt == null)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);

    /// <summary>Drops the habit's pending second reminder directly in the database.</summary>
    public Task ClearSecondReminderAsync(int habitId, CancellationToken cancellationToken = default)
        => _db.Habits
            .Where(h => h.Id == habitId)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.SecondReminderUtc, (DateTime?)null), cancellationToken);

    /// <summary>Drops the routine's pending second reminder directly in the database.</summary>
    public Task ClearRoutineSecondReminderAsync(int routineId, CancellationToken cancellationToken = default)
        => _db.Routines
            .Where(r => r.Id == routineId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SecondReminderUtc, (DateTime?)null), cancellationToken);
}
