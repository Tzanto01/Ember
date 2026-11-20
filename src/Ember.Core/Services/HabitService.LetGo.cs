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

/// <summary>Outcome of marking or unmarking a let-go habit's day as made it.</summary>
public enum MadeItResult
{
    Saved,

    /// <summary>The habit doesn't exist or belongs to someone else.</summary>
    HabitNotFound,

    /// <summary>The habit is a <see cref="HabitKind.Build"/> habit. Nothing was written.</summary>
    NotLetGo,

    /// <summary>The day lies after the user's local today. Nothing was written.</summary>
    InFuture,

    /// <summary>The day lies before the habit's local start day. Nothing was written.</summary>
    BeforeHabitStart,

    /// <summary>The day lies inside the habit's paused range. Nothing was written.</summary>
    Paused,

    /// <summary>The day has a slip noted. Nothing was written.</summary>
    Slipped,

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
    /// (<see cref="HabitRules.StartDate"/>) to the user's local today. Two first slips of a day at once both count. Once saved, the
    /// day's <see cref="MadeItDay"/> is removed.
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
        if (date > TimezoneHelper.LocalToday(habit.User.Timezone, _time)) return SlipResult.InFuture;
        if (date < HabitRules.StartDate(habit, TimezoneHelper.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return SlipResult.BeforeHabitStart;
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
                await RemoveMadeItDayAsync(habitId, date);
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
        await RemoveMadeItDayAsync(habitId, date);
        return SlipResult.Saved;
    }

    /// <summary>
    /// Removes the let-go habit's slips on the local <paramref name="date"/> (undo), and any made-it mark of that day; a day without
    /// slips is a no-op that still returns <see cref="SlipResult.Saved"/>.
    /// </summary>
    public async Task<SlipResult> RemoveSlipsAsync(Guid userId, int habitId, DateOnly date)
    {
        var kind = await _db.Habits.Where(h => h.Id == habitId && h.UserId == userId).Select(h => (HabitKind?)h.Kind).FirstOrDefaultAsync();
        if (kind is null) return SlipResult.HabitNotFound;
        if (kind != HabitKind.LetGo) return SlipResult.NotLetGo;

        await _db.HabitSlips.Where(s => s.HabitId == habitId && s.Date == date).ExecuteDeleteAsync();
        await RemoveMadeItDayAsync(habitId, date);
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

    /// <summary>
    /// Marks the let-go habit's local <paramref name="date"/> as made it: any free day from the habit's start to the user's local
    /// today, never a paused day or one with a slip. Marking a marked day again is a no-op that returns <see cref="MadeItResult.Saved"/>.
    /// Once saved, the day is checked for a slip again, so a slip noted at the same time removes the mark and the result is
    /// <see cref="MadeItResult.Slipped"/>.
    /// </summary>
    public async Task<MadeItResult> MarkMadeItAsync(Guid userId, int habitId, DateOnly date)
    {
        var habit = await _db.Habits
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);
        if (habit is null) return MadeItResult.HabitNotFound;
        if (habit.Kind != HabitKind.LetGo) return MadeItResult.NotLetGo;
        if (date > TimezoneHelper.LocalToday(habit.User.Timezone, _time)) return MadeItResult.InFuture;
        if (date < HabitRules.StartDate(habit, TimezoneHelper.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return MadeItResult.BeforeHabitStart;
        if (HabitRules.IsPausedOn(habit, date)) return MadeItResult.Paused;
        if (await HasSlipOnAsync(habitId, date)) return MadeItResult.Slipped;
        if (!await _consent.HasHealthDataConsentAsync(userId)) return MadeItResult.ConsentRequired;

        if (!await _db.MadeItDays.AnyAsync(m => m.HabitId == habitId && m.Date == date))
        {
            var mark = new MadeItDay { HabitId = habitId, Date = date, CreatedAt = _time.GetUtcNow().UtcDateTime };
            _db.MadeItDays.Add(mark);
            try
            {
                await _db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                _db.Entry(mark).State = EntityState.Detached;
                if (!await _db.MadeItDays.AnyAsync(m => m.HabitId == habitId && m.Date == date)) throw;
            }
        }

        if (await HasSlipOnAsync(habitId, date))
        {
            await RemoveMadeItDayAsync(habitId, date);
            return MadeItResult.Slipped;
        }
        return MadeItResult.Saved;
    }

    /// <summary>Removes the let-go habit's made-it mark on the local <paramref name="date"/> (undo); a day without one is a no-op that still returns <see cref="MadeItResult.Saved"/>.</summary>
    public async Task<MadeItResult> RemoveMadeItAsync(Guid userId, int habitId, DateOnly date)
    {
        var kind = await _db.Habits.Where(h => h.Id == habitId && h.UserId == userId).Select(h => (HabitKind?)h.Kind).FirstOrDefaultAsync();
        if (kind is null) return MadeItResult.HabitNotFound;
        if (kind != HabitKind.LetGo) return MadeItResult.NotLetGo;

        await RemoveMadeItDayAsync(habitId, date);
        return MadeItResult.Saved;
    }

    /// <summary>
    /// The habit's made-it days from <paramref name="from"/> through <paramref name="to"/>, oldest first (empty for a build habit).
    /// Null when the habit doesn't exist or isn't the user's.
    /// </summary>
    public async Task<List<MadeItDay>?> GetMadeItDaysAsync(Guid userId, int habitId, DateOnly from, DateOnly to)
    {
        if (!await _db.Habits.AnyAsync(h => h.Id == habitId && h.UserId == userId)) return null;
        return await _db.MadeItDays
            .AsNoTracking()
            .Where(m => m.HabitId == habitId && m.Date >= from && m.Date <= to)
            .OrderBy(m => m.Date)
            .ToListAsync();
    }

    private Task<bool> HasSlipOnAsync(int habitId, DateOnly date)
        => _db.HabitSlips.AnyAsync(s => s.HabitId == habitId && s.Date == date && s.Count > 0);

    private Task<int> RemoveMadeItDayAsync(int habitId, DateOnly date)
        => _db.MadeItDays.Where(m => m.HabitId == habitId && m.Date == date).ExecuteDeleteAsync();
}
