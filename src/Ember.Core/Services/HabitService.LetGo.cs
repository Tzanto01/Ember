using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Outcome of adding or removing slips.</summary>
public enum SlipResult
{
    Saved,

    /// <summary>The habit doesn't exist or belongs to someone else.</summary>
    HabitNotFound,

    /// <summary>The habit is a <see cref="HabitKind.Build"/> habit. Nothing was written.</summary>
    NotLetGo,

    /// <summary>The day lies before the habit's local start day. Nothing was written.</summary>
    BeforeHabitStart,

    /// <summary>The day lies after the user's local today. Nothing was written.</summary>
    InFuture,

    /// <summary>The user hasn't agreed to the current health-data consent (or withdrew it). Nothing was written.</summary>
    ConsentRequired,
}

/// <summary>Let-go habits: slips instead of check-ins, days free instead of streaks.</summary>
public partial class HabitService
{
    /// <summary>Kind of each of the user's habits by id, without loading anything else.</summary>
    public Task<Dictionary<int, HabitKind>> GetHabitKindsAsync(Guid userId)
        => _db.Habits.Where(h => h.UserId == userId).ToDictionaryAsync(h => h.Id, h => h.Kind);

    /// <summary>
    /// Adds one slip to the let-go habit on the user's local <paramref name="date"/>: the day's count goes up by one and
    /// <paramref name="reason"/>, when given and not yet listed, is appended to its reasons. Any day from the habit's start
    /// (<see cref="HabitRules.StartDate"/>) to the user's local today. Two first slips of a day at once both count.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="reason"/> isn't one of <see cref="HabitSlip.ReasonIds"/>; nothing is written.</exception>
    public async Task<SlipResult> AddSlipAsync(Guid userId, int habitId, DateOnly date, string? reason = null)
    {
        if (reason is not null && !HabitSlip.ReasonIds.Contains(reason))
            throw new ArgumentException($"Unknown slip reason {reason}.", nameof(reason));

        var habit = await _db.Habits
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);
        if (habit is null) return SlipResult.HabitNotFound;
        if (habit.Kind != HabitKind.LetGo) return SlipResult.NotLetGo;
        if (date > TimeZones.LocalToday(habit.User.Timezone, _time)) return SlipResult.InFuture;
        if (date < HabitRules.StartDate(habit, TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return SlipResult.BeforeHabitStart;
        if (!await _consent.HasHealthDataConsentAsync(userId)) return SlipResult.ConsentRequired;

        var now = _time.GetUtcNow().UtcDateTime;
        var existing = await _db.HabitSlips.FirstOrDefaultAsync(s => s.HabitId == habitId && s.Date == date);
        if (existing is null)
        {
            var slip = new HabitSlip { HabitId = habitId, Date = date, Count = 1, Reasons = reason is null ? [] : [reason], UpdatedAt = now };
            _db.HabitSlips.Add(slip);
            try
            {
                await _db.SaveChangesAsync();
                return SlipResult.Saved;
            }
            catch (DbUpdateException)
            {
                _db.Entry(slip).State = EntityState.Detached;
                existing = await _db.HabitSlips.FirstOrDefaultAsync(s => s.HabitId == habitId && s.Date == date);
                if (existing is null) throw;
            }
        }

        existing.Count++;
        if (reason is not null && !existing.Reasons.Contains(reason)) existing.Reasons = [.. existing.Reasons, reason];
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync();
        return SlipResult.Saved;
    }

    /// <summary>Removes the let-go habit's slips on the local <paramref name="date"/> (undo); a day without slips is a no-op that still returns <see cref="SlipResult.Saved"/>.</summary>
    public async Task<SlipResult> RemoveSlipsAsync(Guid userId, int habitId, DateOnly date)
    {
        var kind = await _db.Habits.Where(h => h.Id == habitId && h.UserId == userId).Select(h => (HabitKind?)h.Kind).FirstOrDefaultAsync();
        if (kind is null) return SlipResult.HabitNotFound;
        if (kind != HabitKind.LetGo) return SlipResult.NotLetGo;

        await _db.HabitSlips.Where(s => s.HabitId == habitId && s.Date == date).ExecuteDeleteAsync();
        return SlipResult.Saved;
    }

    /// <summary>
    /// The habit's slip days from <paramref name="from"/> through <paramref name="to"/>, oldest first (empty for a build habit).
    /// Null when the habit doesn't exist or isn't the user's.
    /// </summary>
    public async Task<List<HabitSlip>?> GetSlipsAsync(Guid userId, int habitId, DateOnly from, DateOnly to)
    {
        if (!await _db.Habits.AnyAsync(h => h.Id == habitId && h.UserId == userId)) return null;
        return await _db.HabitSlips
            .AsNoTracking()
            .Where(s => s.HabitId == habitId && s.Date >= from && s.Date <= to)
            .OrderBy(s => s.Date)
            .ToListAsync();
    }
}
