using System.Globalization;
using System.Linq.Expressions;
using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

public partial class HabitService
{
    /// <summary>How long the Snooze button delays a reminder.</summary>
    public static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(60);

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
    private readonly DeletionLedger? _ledger;

    /// <param name="ledger">Where committed account and health-data deletions are recorded; none when null.</param>
    public HabitService(EmberDbContext db, TimeProvider time, DeletionLedger? ledger = null)
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
            .Where(u => u.Id == userId && u.ConsentWithdrawnAt == null
                && ((u.HealthDataConsentAt != null && u.ConsentPolicyVersion == CurrentConsentPolicyVersion)
                    || u.Habits.Any() || u.DayMarks.Any() || u.DayNotes.Any() || u.Moods.Any() || u.Helps != null))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.ConsentWithdrawnAt, now)
                .SetProperty(u => u.DeletionReminderSentAt, (DateTime?)null)
                .SetProperty(u => u.ConsentPromptedOn, lastQuietDay)) == 1;
        if (withdrawn) _ledger?.Record(DeletionLedger.Withdraw, userId);
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

    /// <exception cref="ConsentRequiredException">The user hasn't agreed to the current health-data consent.</exception>
    /// <exception cref="HabitLimitReachedException">The user already tracks <see cref="Limits.HabitsPerUser"/> habits.</exception>
    /// <remarks>
    /// <paramref name="days"/> is stored for <see cref="FrequencyType.Weekdays"/> habits only; weekly habits always get <paramref name="timesPerDay"/> 1.
    /// <paramref name="reminderTimes"/>, when given, replaces <paramref name="reminderTime"/> and is kept as <see cref="HabitRules.NormalizeReminderTimes"/> does.
    /// </remarks>
    /// <exception cref="ArgumentException">A <see cref="HabitKind.LetGo"/> habit that isn't daily, once a day, without weekly target or days and with at most one reminder time.</exception>
    public async Task<Habit> AddHabitAsync(Guid userId, string name, TimeOnly? reminderTime,
        FrequencyType frequency = FrequencyType.Daily, int? weeklyTarget = null, IReadOnlyCollection<string>? reminderExclusions = null,
        ScheduleDays? days = null, int timesPerDay = 1, IReadOnlyCollection<TimeOnly>? reminderTimes = null, HabitKind kind = HabitKind.Build)
    {
        LetGoRules.GuardLetGo(kind, frequency, timesPerDay, weeklyTarget, days, reminderTimes?.Distinct().Count() ?? 0);
        if (!await HasHealthDataConsentAsync(userId))
            throw new ConsentRequiredException();
        if (await _db.Habits.CountAsync(h => h.UserId == userId) >= Limits.HabitsPerUser)
            throw new HabitLimitReachedException();

        var habit = new Habit
        {
            UserId = userId,
            Name = name,
            Kind = kind,
            ReminderTime = reminderTime,
            FrequencyType = frequency,
            WeeklyTarget = frequency == FrequencyType.Weekly ? (weeklyTarget ?? HabitRules.DefaultWeeklyTarget) : null,
            Days = frequency == FrequencyType.Weekdays ? days : null,
            TimesPerDay = frequency == FrequencyType.Weekly ? 1 : timesPerDay,
            ReminderExclusions = reminderExclusions?.Distinct().ToList() ?? [],
            CreatedAt = _time.GetUtcNow().UtcDateTime
        };
        if (reminderTimes is not null) habit.ReminderTimes = HabitRules.NormalizeReminderTimes(reminderTimes, habit.TimesPerDay);
        habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        _db.Habits.Add(habit);
        await _db.SaveChangesAsync();

        return habit;
    }

    public async Task<Habit?> GetHabitAsync(Guid userId, int habitId)
    {
        return await _db.Habits
            .Include(h => h.Logs)
            .Include(h => h.Slips)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);
    }

    public async Task<List<Habit>> GetHabitsAsync(Guid userId)
    {
        return await _db.Habits
            .Where(h => h.UserId == userId)
            .Include(h => h.Logs)
            .Include(h => h.Slips)
            .AsSplitQuery()
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Logs the habit as done (<see cref="HabitLog.Count"/> = <see cref="Habit.TimesPerDay"/>) or explicitly not done (count 0)
    /// on the user's local <paramref name="date"/>, replacing any earlier log of that day.
    /// Any day from the habit's local start day (<see cref="HabitRules.StartDate"/>) to the user's local today can be logged,
    /// so past days can be backfilled or corrected; future days are refused. Needs the health-data consent, like every check-in write
    /// (<see cref="DayEditResult.ConsentRequired"/>); <see cref="ClearDayAsync"/> doesn't.
    /// </summary>
    public Task<DayEditResult> CheckInAsync(Guid userId, int habitId, DateOnly date, bool completed, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, _) => completed ? habit.TimesPerDay : 0);

    /// <summary>
    /// Sets the day's check-in count to the absolute <paramref name="count"/> (0 to <see cref="Habit.TimesPerDay"/>),
    /// by the same day rules as <see cref="CheckInAsync"/>. The day is done once the count reaches the habit's target.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 0 to the habit's <see cref="Habit.TimesPerDay"/>; nothing is written.</exception>
    public Task<DayEditResult> SetCountAsync(Guid userId, int habitId, DateOnly date, int count, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, _) => count >= 0 && count <= habit.TimesPerDay
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, $"Must be between 0 and {habit.TimesPerDay}."));

    /// <summary>Adds one check-in to the day, capped at the habit's <see cref="Habit.TimesPerDay"/>, by the same day rules as <see cref="CheckInAsync"/>.</summary>
    public Task<DayEditResult> AddCheckInAsync(Guid userId, int habitId, DateOnly date, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, current) => Math.Min(current + 1, habit.TimesPerDay));

    private async Task<DayEditResult> WriteCountAsync(Guid userId, int habitId, DateOnly date, ulong? guildId, Func<Habit, int, int> nextCount)
    {
        var (habit, result) = await FindEditableHabitAsync(userId, habitId, date);
        if (habit is null) return result;
        if (!await HasHealthDataConsentAsync(userId)) return DayEditResult.ConsentRequired;

        var existing = await _db.HabitLogs
            .FirstOrDefaultAsync(l => l.HabitId == habitId && l.Date == date);

        if (existing is null)
        {
            var log = new HabitLog
            {
                HabitId = habitId,
                Date = date,
                GuildId = guildId.HasValue ? (long)guildId.Value : null
            };
            ApplyCount(log, nextCount(habit, 0), habit);
            _db.HabitLogs.Add(log);
            try
            {
                await _db.SaveChangesAsync();
                return DayEditResult.Saved;
            }
            catch (DbUpdateException)
            {
                _db.Entry(log).State = EntityState.Detached;
                existing = await _db.HabitLogs
                    .FirstOrDefaultAsync(l => l.HabitId == habitId && l.Date == date);
                if (existing is null) throw;
            }
        }

        ApplyCount(existing, nextCount(habit, existing.Count), habit);
        if (guildId.HasValue) existing.GuildId = (long)guildId.Value;
        await _db.SaveChangesAsync();
        return DayEditResult.Saved;
    }

    private static void ApplyCount(HabitLog log, int count, Habit habit)
    {
        log.Completed = count >= habit.TimesPerDay || (log.Completed && count > 0 && count >= log.Count);
        log.Count = count;
    }

    /// <summary>
    /// Removes the habit's log for the user's local <paramref name="date"/>, so the day is unlogged again.
    /// Same day range as <see cref="CheckInAsync"/>; clearing a day without a log is a no-op that still returns <see cref="DayEditResult.Saved"/>.
    /// </summary>
    public async Task<DayEditResult> ClearDayAsync(Guid userId, int habitId, DateOnly date)
    {
        var (habit, result) = await FindEditableHabitAsync(userId, habitId, date);
        if (habit is null) return result;

        await _db.HabitLogs.Where(l => l.HabitId == habitId && l.Date == date).ExecuteDeleteAsync();
        return DayEditResult.Saved;
    }

    /// <summary>
    /// Marks the user's local <paramref name="date"/> as a low day for all their habits (see <see cref="HabitRules.ConsecutiveStreak"/>
    /// and <see cref="HabitRules.FlexibleStreak"/>). Any day up to the user's local today can be marked; marking it again is a no-op.
    /// Needs the health-data consent, since a low day can say something about the user's health.
    /// </summary>
    public async Task<DayEditResult> SetLowDayAsync(Guid userId, DateOnly date)
    {
        if (!await HasHealthDataConsentAsync(userId)) return DayEditResult.ConsentRequired;
        if (date > await GetLocalTodayAsync(userId)) return DayEditResult.InFuture;

        if (await IsLowDayStoredAsync(userId, date)) return DayEditResult.Saved;

        var mark = new DayMark { UserId = userId, Date = date, Kind = DayMarkKind.LowDay };
        _db.DayMarks.Add(mark);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            _db.Entry(mark).State = EntityState.Detached;
            if (!await IsLowDayStoredAsync(userId, date)) throw;
        }
        return DayEditResult.Saved;
    }

    /// <summary>Removes the low-day mark from the user's local <paramref name="date"/>, if there is one.</summary>
    public async Task ClearLowDayAsync(Guid userId, DateOnly date)
    {
        await _db.DayMarks
            .Where(m => m.UserId == userId && m.Date == date && m.Kind == DayMarkKind.LowDay)
            .ExecuteDeleteAsync();
    }

    /// <summary>All of the user's low days (local dates), for <see cref="HabitRules.ConsecutiveStreak"/>, <see cref="HabitRules.BestStreak"/> and <see cref="HabitRules.FlexibleStreak"/>.</summary>
    public async Task<HashSet<DateOnly>> GetLowDaysAsync(Guid userId)
        => (await _db.DayMarks
            .Where(m => m.UserId == userId && m.Kind == DayMarkKind.LowDay)
            .Select(m => m.Date)
            .ToListAsync()).ToHashSet();

    /// <summary>The local start day (<see cref="HabitRules.StartDate"/>) of the user's oldest habit, or null when they have none. Loads no logs.</summary>
    public async Task<DateOnly?> GetEarliestHabitStartAsync(Guid userId)
    {
        var oldest = await _db.Habits
            .Where(h => h.UserId == userId)
            .OrderBy(h => h.CreatedAt)
            .FirstOrDefaultAsync();
        return oldest is null ? (DateOnly?)null : HabitRules.StartDate(oldest, await GetUserTzAsync(userId));
    }

    /// <summary>
    /// Returns the habit's last <paramref name="days"/> local days, oldest first and ending at the user's local today,
    /// with each day's <see cref="DayState"/> and raw log. Null when the habit doesn't exist or isn't the user's.
    /// State precedence: before start, paused, low day, done, partial, offday, pending (open), not done.
    /// </summary>
    public async Task<IReadOnlyList<HabitDay>?> GetHabitDaysAsync(Guid userId, int habitId, int days)
    {
        var today = await GetLocalTodayAsync(userId);
        return await GetHabitDaysAsync(userId, habitId, today.AddDays(1 - days), today);
    }

    /// <summary>
    /// Returns the habit's local days from <paramref name="from"/> through <paramref name="to"/>, oldest first (empty when
    /// <paramref name="from"/> is after <paramref name="to"/>), by the rules of <see cref="HabitRules.DayOn"/>.
    /// Null when the habit doesn't exist or isn't the user's.
    /// </summary>
    public async Task<IReadOnlyList<HabitDay>?> GetHabitDaysAsync(Guid userId, int habitId, DateOnly from, DateOnly to)
    {
        var habit = await _db.Habits
            .Include(h => h.User)
            .Include(h => h.Logs.Where(l => l.Date >= from && l.Date <= to))
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return null;

        var tz = TimezoneHelper.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimezoneHelper.LocalToday(habit.User.Timezone, _time);
        var lowDays = await GetLowDaysAsync(userId);

        var result = new List<HabitDay>();
        for (var date = from; date <= to; date = date.AddDays(1))
            result.Add(HabitRules.DayOn(habit, date, today, tz, lowDays));
        return result;
    }

    /// <summary>
    /// Per local day from <paramref name="from"/> through <paramref name="to"/> (oldest first), across all the user's habits,
    /// by the rules of <see cref="HabitRules.DayStatsOn"/>. Empty when <paramref name="from"/> is after <paramref name="to"/>.
    /// </summary>
    public async Task<IReadOnlyList<DayStats>> GetDayStatsAsync(Guid userId, DateOnly from, DateOnly to)
    {
        var tz = await GetUserTzAsync(userId);
        var habits = await _db.Habits
            .AsNoTracking()
            .Where(h => h.UserId == userId)
            .Include(h => h.Logs.Where(l => l.Date >= from && l.Date <= to))
            .ToListAsync();

        var result = new List<DayStats>();
        for (var date = from; date <= to; date = date.AddDays(1))
            result.Add(HabitRules.DayStatsOn(habits, date, tz));
        return result;
    }

    /// <summary>Whether <paramref name="date"/> is a stored low day for the user.</summary>
    public Task<bool> IsLowDayStoredAsync(Guid userId, DateOnly date)
        => _db.DayMarks.AnyAsync(m => m.UserId == userId && m.Date == date && m.Kind == DayMarkKind.LowDay);

    private async Task<(Habit? Habit, DayEditResult Result)> FindEditableHabitAsync(Guid userId, int habitId, DateOnly date)
    {
        var habit = await _db.Habits
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return (null, DayEditResult.HabitNotFound);
        if (habit.Kind == HabitKind.LetGo) return (null, DayEditResult.LetGoHabit);
        if (date > TimezoneHelper.LocalToday(habit.User.Timezone, _time)) return (null, DayEditResult.InFuture);
        if (date < HabitRules.StartDate(habit, TimezoneHelper.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return (null, DayEditResult.BeforeHabitStart);
        return (habit, DayEditResult.Saved);
    }

    /// <summary>
    /// Sets the habit's schedule: <paramref name="weeklyTarget"/> (default 3) is kept for weekly habits only, <paramref name="days"/>
    /// for <see cref="FrequencyType.Weekdays"/> habits only, and weekly habits always get <paramref name="timesPerDay"/> 1.
    /// A day that was done stays done: when the target per day changes, days whose count reaches the new target become done, counts
    /// above a lowered target are clamped to it, and only the user's local today is recomputed against it. Reminder times beyond the
    /// new target are dropped, keeping the earliest. Null when the habit isn't the user's.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timesPerDay"/> is outside 1 to <see cref="Limits.TimesPerDay"/>.</exception>
    /// <exception cref="ArgumentException">A weekdays habit without any <paramref name="days"/>, or a let-go habit that wouldn't stay daily once a day.</exception>
    public async Task<Habit?> SetScheduleAsync(Guid userId, int habitId, FrequencyType type, int? weeklyTarget, ScheduleDays? days,
        int timesPerDay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(timesPerDay, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(timesPerDay, Limits.TimesPerDay);
        if (type == FrequencyType.Weekdays && days.GetValueOrDefault() == ScheduleDays.None)
            throw new ArgumentException("A weekdays habit needs at least one day.", nameof(days));

        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return null;
        LetGoRules.GuardLetGo(habit.Kind, type, timesPerDay, null, null, 0);

        var target = type == FrequencyType.Weekly ? 1 : timesPerDay;
        var recompute = target != habit.TimesPerDay;
        habit.FrequencyType = type;
        habit.WeeklyTarget = type == FrequencyType.Weekly ? (weeklyTarget ?? HabitRules.DefaultWeeklyTarget) : null;
        habit.Days = type == FrequencyType.Weekdays ? days : null;
        habit.TimesPerDay = target;
        if (habit.ReminderTimes.Count > target)
        {
            habit.ReminderTimes = habit.ReminderTimes.Take(target).ToList();
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        if (recompute)
        {
            var today = await GetLocalTodayAsync(userId);
            await _db.HabitLogs
                .Where(l => l.HabitId == habitId && (l.Date == today || l.Count >= target))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.Completed, l => l.Count >= target)
                    .SetProperty(l => l.Count, l => l.Count > target ? target : l.Count));
        }
        await transaction.CommitAsync();
        return habit;
    }

    public async Task<bool> DeleteHabitAsync(Guid userId, int habitId)
    {
        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return false;

        _db.Habits.Remove(habit);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Edits the user's habit; null arguments keep the current value. <paramref name="newReminderTime"/> replaces all reminder times
    /// with that one; <paramref name="newReminderTimes"/> replaces them with those (empty clears the reminder), kept as
    /// <see cref="HabitRules.NormalizeReminderTimes"/> does. <paramref name="reminderExclusions"/> replaces <see cref="Habit.ReminderExclusions"/>.
    /// Null when the habit isn't the user's.
    /// </summary>
    /// <exception cref="ArgumentException">More than one reminder time for a let-go habit; nothing is written.</exception>
    /// <exception cref="ConsentRequiredException">A new name without the current health-data consent; nothing is written.</exception>
    public async Task<Habit?> EditHabitAsync(Guid userId, int habitId, string? newName, TimeOnly? newReminderTime, bool clearReminder,
        IReadOnlyCollection<string>? reminderExclusions = null, IReadOnlyCollection<TimeOnly>? newReminderTimes = null)
    {
        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return null;
        if (!clearReminder && newReminderTimes is not null)
            LetGoRules.GuardLetGo(habit.Kind, FrequencyType.Daily, 1, null, null, newReminderTimes.Distinct().Count());
        if (newName is not null && newName != habit.Name && !await HasHealthDataConsentAsync(userId))
            throw new ConsentRequiredException();

        if (newName is not null) habit.Name = newName;
        if (reminderExclusions is not null) habit.ReminderExclusions = reminderExclusions.Distinct().ToList();

        if (clearReminder)
        {
            habit.ReminderTime = null;
            habit.NextReminderUtc = null;
        }
        else if (newReminderTimes is not null)
        {
            habit.ReminderTimes = HabitRules.NormalizeReminderTimes(newReminderTimes, habit.TimesPerDay);
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        }
        else if (newReminderTime.HasValue)
        {
            habit.ReminderTime = newReminderTime;
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await GetUserTzAsync(userId), _time.GetUtcNow().UtcDateTime);
        }

        await _db.SaveChangesAsync();
        return habit;
    }

    /// <summary>How long a reminder's snooze button stays usable after the reminder was sent.</summary>
    public static readonly TimeSpan SnoozeWindow = TimeSpan.FromHours(24);

    /// <summary>
    /// Delays the habit's next reminder by <see cref="SnoozeDelay"/> and drops its pending second reminder. Returns false when the habit isn't the user's, is a let-go habit or has no reminder,
    /// when the reminder was sent (<paramref name="sentAtUtc"/>) more than <see cref="SnoozeWindow"/> ago, so an old DM can't displace the schedule,
    /// or when the snoozed reminder would land on a later local day than the one the reminder was sent on.
    /// </summary>
    public async Task<bool> SnoozeReminderAsync(Guid userId, int habitId, DateTimeOffset sentAtUtc)
    {
        if (_time.GetUtcNow() - sentAtUtc > SnoozeWindow) return false;

        var tz = await GetUserTzAsync(userId);
        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_time.GetUtcNow() + SnoozeDelay, tz).DateTime)
            != DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(sentAtUtc, tz).DateTime))
            return false;

        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId && h.Kind == HabitKind.Build && h.ReminderTimes.Count > 0);

        if (habit is null) return false;

        habit.NextReminderUtc = _time.GetUtcNow().UtcDateTime + SnoozeDelay;
        habit.SecondReminderUtc = null;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Recomputes every habit and routine reminder of the user from their current timezone. Returns how many habits have a reminder.</summary>
    public async Task<int> RescheduleRemindersAsync(Guid userId)
    {
        var tz = await GetUserTzAsync(userId);
        var habits = await _db.Habits
            .Where(h => h.UserId == userId && h.ReminderTimes.Count > 0)
            .ToListAsync();

        foreach (var habit in habits)
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, tz, _time.GetUtcNow().UtcDateTime);
        foreach (var routine in await _db.Routines.Where(r => r.UserId == userId && r.ReminderTime != null).ToListAsync())
            routine.NextReminderUtc = HabitRules.NextReminderUtc(routine, tz, _time.GetUtcNow().UtcDateTime);

        await _db.SaveChangesAsync();
        return habits.Count;
    }

    /// <summary>
    /// Marks the "your DM reminders are paused" notice as shown. Returns true only for the one
    /// call that should show it: the user's DM reminders are paused and they haven't been told yet.
    /// </summary>
    public async Task<bool> ClaimDmPauseNoticeAsync(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return await _db.Users
            .Where(u => u.Id == userId && u.DmRemindersPausedAt != null && u.DmPauseNoticeSentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DmPauseNoticeSentAt, now)) == 1;
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
        _ledger?.Record(DeletionLedger.Health, userId);
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
        if (deleted > 0) _ledger?.Record(DeletionLedger.Account, userId);
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

    /// <summary>Undoes <see cref="ClaimDmPauseNoticeAsync"/> when the notice couldn't be shown, so the next interaction shows it.</summary>
    public async Task ReleaseDmPauseNoticeAsync(Guid userId)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DmPauseNoticeSentAt, (DateTime?)null));
    }

    /// <summary>Turns DM reminders back on after Ember could reach the user again.</summary>
    public async Task ResumeDmRemindersAsync(Guid userId)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DmFailureCount, 0)
                .SetProperty(u => u.DmRemindersPausedAt, (DateTime?)null)
                .SetProperty(u => u.DmPauseNoticeSentAt, (DateTime?)null));
    }

    /// <summary>
    /// Pauses the habit through the local day <paramref name="pausedUntil"/>, or resumes it when null.
    /// A new pause starts today; extending a running pause keeps its start. Resuming ends the pause yesterday,
    /// so the days already paused stay protected; a pause that started today is removed entirely.
    /// </summary>
    public async Task<Habit?> PauseHabitAsync(Guid userId, int habitId, DateOnly? pausedUntil)
    {
        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return null;

        var today = await GetLocalTodayAsync(userId);
        if (pausedUntil is not null)
        {
            if (!HabitRules.IsPausedOn(habit, today)) habit.PausedFrom = today;
            habit.PausedUntil = pausedUntil;
        }
        else if (habit.PausedUntil is { } until && (habit.PausedFrom is null || habit.PausedFrom < today))
        {
            habit.PausedUntil = until < today ? until : today.AddDays(-1);
        }
        else
        {
            habit.PausedFrom = null;
            habit.PausedUntil = null;
        }

        await _db.SaveChangesAsync();
        return habit;
    }

    public async Task<int> GetGraceDaysAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return user?.GraceDaysPerWeek ?? 1;
    }

    public async Task SetGraceDaysAsync(Guid userId, int graceDays)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.GraceDaysPerWeek, graceDays));
    }

    /// <summary>The user's <see cref="User.Language"/>; <see cref="Languages.English"/> when the user doesn't exist.</summary>
    public async Task<string> GetLanguageAsync(Guid userId)
        => await _db.Users.Where(u => u.Id == userId).Select(u => u.Language).FirstOrDefaultAsync() ?? Languages.English;

    /// <summary>Sets <see cref="User.Language"/>; the caller passes one of <see cref="Languages.All"/>.</summary>
    public async Task SetLanguageAsync(Guid userId, string language)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Language, language));
    }

    /// <summary>Sets <see cref="User.OfferHabitNote"/>.</summary>
    public async Task SetOfferHabitNoteAsync(Guid userId, bool offer)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.OfferHabitNote, offer));
    }

    /// <summary>
    /// Stores <paramref name="order"/> as the user's <see cref="User.TodayOrder"/>, keeping only the first occurrence of each
    /// <c>h:&lt;id&gt;</c> of the user's habits outside a routine and <c>r:&lt;id&gt;</c> of the user's routines, in canonical form.
    /// Null resets to automatic. Returns what was stored. Callers enforce <see cref="User.MaxTodayOrderEntries"/>.
    /// </summary>
    public async Task<List<string>?> SetTodayOrderAsync(Guid userId, IReadOnlyList<string?>? order)
    {
        List<string>? kept = null;
        if (order is not null)
        {
            var valid = (await _db.Habits.Where(h => h.UserId == userId && h.RoutineId == null).Select(h => h.Id).ToListAsync())
                .Select(id => $"h:{id}")
                .Concat((await _db.Routines.Where(r => r.UserId == userId).Select(r => r.Id).ToListAsync()).Select(id => $"r:{id}"))
                .ToHashSet();
            var seen = new HashSet<string>();
            kept = order.OfType<string>().Where(k => valid.Contains(k) && seen.Add(k)).ToList();
        }
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TodayOrder, kept));
        return kept;
    }

    /// <summary>Sets <see cref="User.CalmMode"/>.</summary>
    public async Task SetCalmModeAsync(Guid userId, bool calmMode)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CalmMode, calmMode));
    }

    public async Task SetThemeAsync(Guid userId, string theme)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Theme, theme));
    }


    public async Task<TimeZoneInfo> GetUserTzAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return TimezoneHelper.Find(user?.Timezone ?? "UTC") ?? TimeZoneInfo.Utc;
    }

    /// <summary>The user's current local day (UTC when the user has no known timezone).</summary>
    public async Task<DateOnly> GetLocalTodayAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return TimezoneHelper.LocalToday(user?.Timezone, _time);
    }

    public async Task<bool> HasTimezoneSetAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return user?.Timezone is not null && user.Timezone != "UTC";
    }

    /// <summary>Stores the user's timezone id. Does not reschedule reminders.</summary>
    public async Task SetTimezoneAsync(Guid userId, string timezoneId)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Timezone, timezoneId));
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

    /// <summary>Total number of habits across all users.</summary>
    public Task<int> CountHabitsAsync() => _db.Habits.CountAsync();

    public Task<bool> UserExistsAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.AnyAsync(u => u.Id == userId, cancellationToken);

    /// <summary>Id and timezone of every user with at least one habit whose consent isn't withdrawn.</summary>
    public async Task<List<(Guid UserId, string Timezone)>> GetUserTimezonesWithHabitsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Users
            .Where(u => u.Habits.Any() && u.ConsentWithdrawnAt == null)
            .Select(u => new { u.Id, u.Timezone })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, r.Timezone)).ToList();
    }

    /// <summary>
    /// The given users (tracked) with their logins, habits, logs, slips and day marks; with <paramref name="logsSince"/> only the logs on or
    /// after that day (slips are always all loaded, since days free can reach back to a habit's start).
    /// </summary>
    public Task<List<User>> GetUsersWithHabitsAsync(List<Guid> userIds, CancellationToken cancellationToken = default,
        DateOnly? logsSince = null)
    {
        IQueryable<User> users = logsSince is { } since
            ? _db.Users.Include(u => u.Logins).Include(u => u.Habits).ThenInclude(h => h.Logs.Where(l => l.Date >= since))
            : _db.Users.Include(u => u.Logins).Include(u => u.Habits).ThenInclude(h => h.Logs);
        return users
            .Include(u => u.Habits).ThenInclude(h => h.Slips)
            .Include(u => u.DayMarks)
            .AsSplitQuery()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(cancellationToken);
    }

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

        var next = TimezoneHelper.NextOccurrenceUtc(row.ReminderTimes, TimezoneHelper.Find(row.Timezone) ?? TimeZoneInfo.Utc, nowUtc);
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
}
