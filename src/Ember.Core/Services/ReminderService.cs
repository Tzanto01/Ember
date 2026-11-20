using Ember.Core.Data;
using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>
/// Reminder scheduling (snooze, reschedule, DM pause), the bot's reminder dispatch with the only loaders that ignore the owner,
/// open reminder messages and delivered web push reminders, second reminders, destinations (Discord answer, browsers, phones),
/// the Discord ask and the one-time Done tokens.
/// </summary>
public partial class ReminderService
{
    /// <summary>How long the Snooze button delays a reminder.</summary>
    public static readonly TimeSpan SnoozeDelay = TimeSpan.FromMinutes(60);

    /// <summary>How long a reminder's snooze button stays usable after the reminder was sent.</summary>
    public static readonly TimeSpan SnoozeWindow = TimeSpan.FromHours(24);

    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;
    private readonly ConsentService _consent;
    private readonly HabitService _habits;

    /// <param name="consent">Answers whether the user agreed to store health data.</param>
    /// <param name="habits">
    /// Gives the time zone, the check-in a Done token writes and the Discord user lookup; must use the same database, so a scope's
    /// reminder and habit calls share one context.
    /// </param>
    public ReminderService(EmberDbContext db, TimeProvider time, ConsentService consent, HabitService habits)
    {
        _db = db;
        _time = time;
        _consent = consent;
        _habits = habits;
    }

    /// <summary>
    /// Delays the habit's next reminder by <see cref="SnoozeDelay"/> and drops its pending second reminder. Returns false when the habit isn't the user's, is a let-go habit or has no reminder,
    /// when the reminder was sent (<paramref name="sentAtUtc"/>) more than <see cref="SnoozeWindow"/> ago, so an old DM can't displace the schedule,
    /// or when the snoozed reminder would land on a later local day than the one the reminder was sent on.
    /// </summary>
    public async Task<bool> SnoozeReminderAsync(Guid userId, int habitId, DateTimeOffset sentAtUtc)
    {
        if (_time.GetUtcNow() - sentAtUtc > SnoozeWindow) return false;

        var tz = await _habits.GetUserTzAsync(userId);
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
        var tz = await _habits.GetUserTzAsync(userId);
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

    /// <summary>Sets the account's <see cref="User.SecondReminderMinutes"/> (one of <see cref="User.SecondReminderChoices"/>).</summary>
    public async Task SetSecondReminderMinutesAsync(Guid userId, int minutes)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecondReminderMinutes, minutes));
    }
}
