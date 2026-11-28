using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

public partial class HabitService
{
    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;
    private readonly ConsentService _consent;
    private readonly UserSettingsService _settings;

    /// <param name="consent">Answers whether the user agreed to store health data.</param>
    /// <param name="settings">Gives the user's time zone and local day; must use the same database.</param>
    public HabitService(EmberDbContext db, TimeProvider time, ConsentService consent, UserSettingsService settings)
    {
        _db = db;
        _time = time;
        _consent = consent;
        _settings = settings;
    }

    /// <summary>
    /// Creates a habit for the user. Needs the health-data consent (<see cref="HabitWriteResult.ConsentRequired"/>) and room under
    /// <see cref="Limits.HabitsPerUser"/> (<see cref="HabitWriteResult.LimitReached"/>); the habit comes back with
    /// <see cref="HabitWriteResult.Saved"/> only.
    /// </summary>
    /// <remarks>
    /// <paramref name="days"/> is stored for <see cref="FrequencyType.Weekdays"/> habits only; weekly habits always get <paramref name="timesPerDay"/> 1.
    /// <paramref name="reminderTimes"/>, when given, replaces <paramref name="reminderTime"/> and is kept as <see cref="HabitRules.NormalizeReminderTimes"/> does.
    /// <paramref name="picture"/> without a portion gets its <see cref="PictureRules.DefaultPortion"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">A <see cref="HabitKind.LetGo"/> habit that isn't daily, once a day, without weekly target, days or timer and with at most one reminder time;
    /// or a picture and portion that fail <see cref="PictureRules.Validate"/>. Nothing is written.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timerMinutes"/> is outside 1 to <see cref="Limits.TimerMinutes"/>.</exception>
    public async Task<(HabitWriteResult Result, Habit? Habit)> AddHabitAsync(Guid userId, string name, TimeOnly? reminderTime,
        FrequencyType frequency = FrequencyType.Daily, int? weeklyTarget = null, IReadOnlyCollection<string>? reminderExclusions = null,
        ScheduleDays? days = null, int timesPerDay = 1, IReadOnlyCollection<TimeOnly>? reminderTimes = null, HabitKind kind = HabitKind.Build,
        int? timerMinutes = null, HabitPicture? picture = null, int? portionAmount = null, PortionUnit? portionUnit = null,
        bool pictureOfferAnswered = false)
    {
        ThrowIfTimerOutOfRange(timerMinutes);
        LetGoRules.ThrowIfInvalid(kind, frequency, timesPerDay, weeklyTarget, days, reminderTimes?.Distinct().Count() ?? 0, timerMinutes);
        var pictureState = PictureRules.Apply(default, picture, portionAmount, portionUnit);
        PictureRules.ThrowIfInvalid(kind, pictureState);
        if (!await _consent.HasHealthDataConsentAsync(userId))
            return (HabitWriteResult.ConsentRequired, null);
        if (await _db.Habits.CountAsync(h => h.UserId == userId) >= Limits.HabitsPerUser)
            return (HabitWriteResult.LimitReached, null);

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
            TimerMinutes = timerMinutes,
            Picture = pictureState.Picture,
            PortionAmount = pictureState.PortionAmount,
            PortionUnit = pictureState.PortionUnit,
            PictureOfferAnswered = pictureOfferAnswered,
            ReminderExclusions = reminderExclusions?.Distinct().ToList() ?? [],
            CreatedAt = _time.GetUtcNow().UtcDateTime
        };
        if (reminderTimes is not null) habit.ReminderTimes = HabitRules.NormalizeReminderTimes(reminderTimes, habit.TimesPerDay);
        habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await _settings.GetTimeZoneAsync(userId), _time.GetUtcNow().UtcDateTime);
        _db.Habits.Add(habit);
        await _db.SaveChangesAsync();

        return (HabitWriteResult.Saved, habit);
    }

    public async Task<Habit?> GetHabitAsync(Guid userId, int habitId)
    {
        return await _db.Habits
            .Include(h => h.Logs)
            .Include(h => h.Slips)
            .Include(h => h.MadeItDays)
            .AsSplitQuery()
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);
    }

    public async Task<List<Habit>> GetHabitsAsync(Guid userId)
    {
        return await _db.Habits
            .Where(h => h.UserId == userId)
            .Include(h => h.Logs)
            .Include(h => h.Slips)
            .Include(h => h.MadeItDays)
            .AsSplitQuery()
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// The user's habits in creation order with their slips but no logs or made-it days: enough to list them by name and schedule,
    /// with <see cref="LetGoRules.StatsOn"/> for let-go habits.
    /// </summary>
    public Task<List<Habit>> GetHabitsWithSlipsAsync(Guid userId)
        => _db.Habits
            .Where(h => h.UserId == userId)
            .Include(h => h.Slips)
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();

    /// <summary>The user's habits in creation order with only their logs of the local <paramref name="date"/> loaded.</summary>
    public Task<List<Habit>> GetHabitsWithLogsOnAsync(Guid userId, DateOnly date)
        => _db.Habits
            .Where(h => h.UserId == userId)
            .Include(h => h.Logs.Where(l => l.Date == date))
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();

    /// <summary>
    /// The user's habits in creation order with only their logs from the local <paramref name="from"/> on, without slips or
    /// made-it days: for <see cref="WidgetDays.On"/> with <paramref name="from"/> the Monday of today's week.
    /// </summary>
    public Task<List<Habit>> GetHabitsWithLogsFromAsync(Guid userId, DateOnly from)
        => _db.Habits
            .Where(h => h.UserId == userId)
            .Include(h => h.Logs.Where(l => l.Date >= from))
            .OrderBy(h => h.CreatedAt)
            .ToListAsync();

    /// <summary>How many habits the user has.</summary>
    public Task<int> CountHabitsAsync(Guid userId) => _db.Habits.CountAsync(h => h.UserId == userId);

    /// <summary>
    /// Logs the habit as done (<see cref="HabitLog.Count"/> = <see cref="Habit.TimesPerDay"/>) or explicitly not done (count 0,
    /// <see cref="HabitLog.Skipped"/>, also on a partly done day) on the user's local <paramref name="date"/>, replacing any earlier log
    /// of that day; <see cref="SkipDayAsync"/> skips and keeps the count.
    /// Any day from the habit's local start day (<see cref="HabitRules.StartDate"/>) to the user's local today can be logged,
    /// so past days can be backfilled or corrected; future days are refused. Needs the health-data consent, like every check-in write
    /// (<see cref="DayEditResult.ConsentRequired"/>); <see cref="ClearDayAsync"/> doesn't. A skip (not done) on the local today ends that
    /// day's reminders whichever channel it came from (<see cref="SkipTodayRemindersAsync"/>); any other write of today takes such a skip back.
    /// </summary>
    public Task<DayEditResult> CheckInAsync(Guid userId, int habitId, DateOnly date, bool completed, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, _) => completed ? habit.TimesPerDay : 0, skip: !completed);

    /// <summary>
    /// Skips the user's local <paramref name="date"/> by the same day rules as <see cref="CheckInAsync"/>: keeps the day's check-in count
    /// (0 without a log) and marks it <see cref="HabitLog.Skipped"/>, so it stays not done. A day already done stays done and isn't marked.
    /// On the local today it ends that day's reminders either way.
    /// </summary>
    public Task<DayEditResult> SkipDayAsync(Guid userId, int habitId, DateOnly date, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (_, current) => current, skip: true);

    /// <summary>
    /// Sets the day's check-in count to the absolute <paramref name="count"/> (0 to <see cref="Habit.TimesPerDay"/>),
    /// by the same day rules as <see cref="CheckInAsync"/>. The day is done once the count reaches the habit's target.
    /// This is never a skip, not even for a count of 0: it clears <see cref="HabitLog.Skipped"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is outside 0 to the habit's <see cref="Habit.TimesPerDay"/>; nothing is written.</exception>
    public Task<DayEditResult> SetCountAsync(Guid userId, int habitId, DateOnly date, int count, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, _) => count >= 0 && count <= habit.TimesPerDay
            ? count
            : throw new ArgumentOutOfRangeException(nameof(count), count, $"Must be between 0 and {habit.TimesPerDay}."));

    /// <summary>
    /// Adds one check-in to the day, capped at the habit's <see cref="Habit.TimesPerDay"/>, by the same day rules as <see cref="CheckInAsync"/>.
    /// Clears <see cref="HabitLog.Skipped"/>.
    /// </summary>
    public Task<DayEditResult> AddCheckInAsync(Guid userId, int habitId, DateOnly date, ulong? guildId = null)
        => WriteCountAsync(userId, habitId, date, guildId, (habit, current) => Math.Min(current + 1, habit.TimesPerDay));

    private async Task<DayEditResult> WriteCountAsync(Guid userId, int habitId, DateOnly date, ulong? guildId, Func<Habit, int, int> nextCount,
        bool skip = false)
    {
        var (habit, result) = await FindEditableHabitAsync(userId, habitId, date);
        if (habit is null) return result;
        if (!await _consent.HasHealthDataConsentAsync(userId)) return DayEditResult.ConsentRequired;
        if (skip) SkipToday(habit, date);
        else UnskipToday(habit, date);

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
            ApplyCount(log, nextCount(habit, 0), habit, skip);
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

        ApplyCount(existing, nextCount(habit, existing.Count), habit, skip);
        if (guildId.HasValue) existing.GuildId = (long)guildId.Value;
        await _db.SaveChangesAsync();
        return DayEditResult.Saved;
    }

    private static void ApplyCount(HabitLog log, int count, Habit habit, bool skip)
    {
        log.Completed = count >= habit.TimesPerDay || (log.Completed && count > 0 && count >= log.Count);
        log.Count = count;
        log.Skipped = skip && !log.Completed;
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
        UnskipToday(habit, date);
        await _db.SaveChangesAsync();
        return DayEditResult.Saved;
    }

    /// <summary>
    /// Ends the habit's reminders for the user's local today, as a skip does: drops its pending second reminder and moves its next
    /// reminder to the first one after today (<see cref="HabitRules.NextReminderAfterDayUtc"/>). For a skip that keeps a day already done.
    /// </summary>
    public async Task SkipTodayRemindersAsync(Guid userId, int habitId)
    {
        var habit = await _db.Habits
            .Include(h => h.User)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);
        if (habit is null) return;

        SkipToday(habit, TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow()));
        await _db.SaveChangesAsync();
    }

    private void SkipToday(Habit habit, DateOnly date)
    {
        if (date != TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow())) return;
        habit.SecondReminderUtc = null;
        habit.NextReminderUtc = HabitRules.NextReminderAfterDayUtc(habit, TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc, date,
            _time.GetUtcNow().UtcDateTime);
        var entry = _db.Entry(habit);
        entry.Property(h => h.SecondReminderUtc).IsModified = true;
        entry.Property(h => h.NextReminderUtc).IsModified = true;
    }

    /// <summary>Brings back the rest of today's reminders that a skip of the local today moved past it.</summary>
    private void UnskipToday(Habit habit, DateOnly date)
    {
        if (date != TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow())) return;
        var tz = TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc;
        if (habit.NextReminderUtc is { } next && TimeZones.LocalDay(next, tz) > date)
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, tz, _time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Marks the user's local <paramref name="date"/> as a low day for all their habits (see <see cref="HabitRules.ConsecutiveStreak"/>
    /// and <see cref="HabitRules.FlexibleStreak"/>). Any day up to the user's local today can be marked; marking it again is a no-op.
    /// Needs the health-data consent, since a low day can say something about the user's health.
    /// </summary>
    public async Task<DayEditResult> SetLowDayAsync(Guid userId, DateOnly date)
    {
        if (!await _consent.HasHealthDataConsentAsync(userId)) return DayEditResult.ConsentRequired;
        if (date > await _settings.GetLocalTodayAsync(userId)) return DayEditResult.InFuture;

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
        return oldest is null ? (DateOnly?)null : HabitRules.StartDate(oldest, await _settings.GetTimeZoneAsync(userId));
    }

    /// <summary>
    /// Returns the habit's last <paramref name="days"/> local days, oldest first and ending at the user's local today,
    /// with each day's <see cref="DayState"/> and raw log. Null when the habit doesn't exist or isn't the user's.
    /// State precedence: before start, paused, low day, done, partial, offday, pending (open), not done.
    /// </summary>
    public async Task<IReadOnlyList<HabitDay>?> GetHabitDaysAsync(Guid userId, int habitId, int days)
    {
        var today = await _settings.GetLocalTodayAsync(userId);
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

        var tz = TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc;
        var today = TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow());
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
        var tz = await _settings.GetTimeZoneAsync(userId);
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
        if (date > TimeZones.LocalToday(habit.User.Timezone, _time.GetUtcNow())) return (null, DayEditResult.InFuture);
        if (date < HabitRules.StartDate(habit, TimeZones.Find(habit.User.Timezone) ?? TimeZoneInfo.Utc)) return (null, DayEditResult.BeforeHabitStart);
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
        LetGoRules.ThrowIfInvalid(habit.Kind, type, timesPerDay, null, null, 0, null);

        var target = type == FrequencyType.Weekly ? 1 : timesPerDay;
        var recompute = target != habit.TimesPerDay;
        habit.FrequencyType = type;
        habit.WeeklyTarget = type == FrequencyType.Weekly ? (weeklyTarget ?? HabitRules.DefaultWeeklyTarget) : null;
        habit.Days = type == FrequencyType.Weekdays ? days : null;
        habit.TimesPerDay = target;
        if (habit.ReminderTimes.Count > target)
        {
            habit.ReminderTimes = habit.ReminderTimes.Take(target).ToList();
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await _settings.GetTimeZoneAsync(userId), _time.GetUtcNow().UtcDateTime);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        await _db.SaveChangesAsync();
        if (recompute)
        {
            var today = await _settings.GetLocalTodayAsync(userId);
            await _db.HabitLogs
                .Where(l => l.HabitId == habitId && (l.Date == today || l.Count >= target))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.Completed, l => l.Count >= target)
                    .SetProperty(l => l.Skipped, l => l.Skipped && l.Count < target)
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
    /// <paramref name="timerMinutes"/> sets <see cref="Habit.TimerMinutes"/>, <paramref name="clearTimer"/> removes it.
    /// <see cref="HabitWriteResult.NotFound"/> when the habit isn't the user's; <see cref="HabitWriteResult.ConsentRequired"/>, writing
    /// nothing, for a new name without the current health-data consent.
    /// </summary>
    /// <exception cref="ArgumentException">More than one reminder time or a timer for a let-go habit; nothing is written.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timerMinutes"/> is outside 1 to <see cref="Limits.TimerMinutes"/>; nothing is written.</exception>
    /// <remarks>
    /// The picture changes as <see cref="PictureRules.Apply"/> says: <paramref name="picture"/>, <paramref name="portionAmount"/> and
    /// <paramref name="portionUnit"/> set it, <paramref name="clearPicture"/> removes it. <paramref name="pictureOfferAnswered"/> true
    /// sets <see cref="Habit.PictureOfferAnswered"/>; false changes nothing. A result that fails <see cref="PictureRules.Validate"/>, or
    /// <paramref name="clearPicture"/> with a picture or portion, throws <see cref="ArgumentException"/> and nothing is written.
    /// </remarks>
    public async Task<(HabitWriteResult Result, Habit? Habit)> EditHabitAsync(Guid userId, int habitId, string? newName,
        TimeOnly? newReminderTime, bool clearReminder, IReadOnlyCollection<string>? reminderExclusions = null,
        IReadOnlyCollection<TimeOnly>? newReminderTimes = null, int? timerMinutes = null, bool clearTimer = false,
        HabitPicture? picture = null, int? portionAmount = null, PortionUnit? portionUnit = null, bool clearPicture = false,
        bool pictureOfferAnswered = false)
    {
        ThrowIfTimerOutOfRange(timerMinutes);
        if (clearPicture && (picture is not null || portionAmount is not null || portionUnit is not null))
            throw new ArgumentException("Can't be combined with clearPicture.", nameof(picture));
        var habit = await _db.Habits
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

        if (habit is null) return (HabitWriteResult.NotFound, null);
        LetGoRules.ThrowIfInvalid(habit.Kind, FrequencyType.Daily, 1, null, null,
            clearReminder ? 0 : newReminderTimes?.Distinct().Count() ?? 0, clearTimer ? null : timerMinutes);
        var pictureState = PictureRules.Apply(HabitPictureState.Of(habit), picture, portionAmount, portionUnit, clearPicture);
        PictureRules.ThrowIfInvalid(habit.Kind, pictureState);
        if (newName is not null && newName != habit.Name && !await _consent.HasHealthDataConsentAsync(userId))
            return (HabitWriteResult.ConsentRequired, null);

        if (newName is not null) habit.Name = newName;
        if (reminderExclusions is not null) habit.ReminderExclusions = reminderExclusions.Distinct().ToList();
        if (clearTimer) habit.TimerMinutes = null;
        else if (timerMinutes is not null) habit.TimerMinutes = timerMinutes;
        (habit.Picture, habit.PortionAmount, habit.PortionUnit) = pictureState;
        if (pictureOfferAnswered) habit.PictureOfferAnswered = true;

        if (clearReminder)
        {
            habit.ReminderTime = null;
            habit.NextReminderUtc = null;
        }
        else if (newReminderTimes is not null)
        {
            habit.ReminderTimes = HabitRules.NormalizeReminderTimes(newReminderTimes, habit.TimesPerDay);
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await _settings.GetTimeZoneAsync(userId), _time.GetUtcNow().UtcDateTime);
        }
        else if (newReminderTime.HasValue)
        {
            habit.ReminderTime = newReminderTime;
            habit.NextReminderUtc = HabitRules.NextReminderUtc(habit, await _settings.GetTimeZoneAsync(userId), _time.GetUtcNow().UtcDateTime);
        }

        await _db.SaveChangesAsync();
        return (HabitWriteResult.Saved, habit);
    }

    private static void ThrowIfTimerOutOfRange(int? timerMinutes)
    {
        if (timerMinutes is < 1 or > Limits.TimerMinutes)
            throw new ArgumentOutOfRangeException(nameof(timerMinutes), timerMinutes, $"Must be between 1 and {Limits.TimerMinutes}.");
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

        var today = await _settings.GetLocalTodayAsync(userId);
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

    /// <summary>Total number of habits across all users.</summary>
    public Task<int> CountHabitsAsync() => _db.Habits.CountAsync();

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
}
