using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>What one local day means for a <see cref="HabitKind.LetGo"/> habit (see <see cref="HabitService.LetGoDayOn"/>).</summary>
public enum LetGoDayState
{
    /// <summary>Before the habit's local start day.</summary>
    BeforeStart,

    /// <summary>No slip that day and not paused.</summary>
    Free,

    /// <summary>At least one slip that day, paused or not.</summary>
    Slipped,

    /// <summary>Inside the habit's paused range without a slip: neither free nor slipped.</summary>
    Paused,
}

/// <summary>One local day of a let-go habit: its <see cref="LetGoDayState"/> and that day's slip count (0 without slips).</summary>
public sealed record LetGoDay(DateOnly Date, LetGoDayState State, int Slips);

/// <summary>A let-go habit's numbers as the clients show them (see <see cref="HabitService.LetGoStatsOn"/>).</summary>
public sealed record LetGoStats(int DaysFree, int LongestStretch, int FreeDaysThisMonth, int SlipDaysLast30);

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
}

/// <summary>Let-go habits: slips instead of check-ins, days free instead of streaks.</summary>
public partial class HabitService
{
    /// <summary>
    /// The let-go habit's state on the local <paramref name="date"/>. Precedence: before start (<see cref="StartDate"/> in
    /// <paramref name="tz"/>), slipped (a slip that day, even on a paused day), paused (<see cref="IsPausedOn"/>), free.
    /// Needs the habit's slip of that date loaded, if there is one.
    /// </summary>
    public static LetGoDay LetGoDayOn(Habit habit, DateOnly date, TimeZoneInfo tz)
    {
        var slips = habit.Slips.FirstOrDefault(s => s.Date == date)?.Count ?? 0;
        var state = date < StartDate(habit, tz) ? LetGoDayState.BeforeStart
            : slips > 0 ? LetGoDayState.Slipped
            : IsPausedOn(habit, date) ? LetGoDayState.Paused
            : LetGoDayState.Free;
        return new LetGoDay(date, state, slips);
    }

    /// <summary>
    /// The let-go habit's stats on the user's local <paramref name="today"/>, by the day states of <see cref="LetGoDayOn"/> from its start
    /// through today: <c>DaysFree</c> counts the free days after the last slip (or from the start), today included while it is free;
    /// paused days are skipped (neither counted nor breaking the run). <c>LongestStretch</c> is the longest such run ever, the current one
    /// included. <c>FreeDaysThisMonth</c> counts the free days of today's calendar month, <c>SlipDaysLast30</c> the slipped days in the 30
    /// days ending today. Needs all of the habit's slips loaded.
    /// </summary>
    public static LetGoStats LetGoStatsOn(Habit habit, DateOnly today, TimeZoneInfo tz)
    {
        var slipped = habit.Slips.Where(s => s.Count > 0).Select(s => s.Date).ToHashSet();
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var windowStart = today.AddDays(-29);
        int run = 0, longest = 0, month = 0, last30 = 0;
        for (var date = StartDate(habit, tz); date <= today; date = date.AddDays(1))
        {
            if (slipped.Contains(date))
            {
                run = 0;
                if (date >= windowStart) last30++;
            }
            else if (!IsPausedOn(habit, date))
            {
                longest = Math.Max(longest, ++run);
                if (date >= monthStart) month++;
            }
        }
        return new LetGoStats(run, longest, month, last30);
    }

    /// <summary>Kind of each of the user's habits by id, without loading anything else.</summary>
    public Task<Dictionary<int, HabitKind>> GetHabitKindsAsync(Guid userId)
        => _db.Habits.Where(h => h.UserId == userId).ToDictionaryAsync(h => h.Id, h => h.Kind);

    /// <summary>
    /// Adds one slip to the let-go habit on the user's local <paramref name="date"/>: the day's count goes up by one and
    /// <paramref name="reason"/>, when given and not yet listed, is appended to its reasons. Any day from the habit's start
    /// (<see cref="StartDate"/>) to the user's local today. Two first slips of a day at once both count.
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
        if (date < StartDate(habit, TimezoneHelper.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return SlipResult.BeforeHabitStart;

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

    /// <summary>Throws when a let-go habit wouldn't be daily and once a day, without weekly target or days, with at most one reminder time.</summary>
    private static void GuardLetGo(HabitKind kind, FrequencyType frequency, int timesPerDay, int? weeklyTarget, ScheduleDays? days,
        int reminderTimes)
    {
        if (kind == HabitKind.LetGo && (frequency != FrequencyType.Daily || timesPerDay != 1 || weeklyTarget is not null
            || days is not null || reminderTimes > 1))
            throw new ArgumentException("A let-go habit is daily, once a day, with at most one reminder time.");
    }
}
