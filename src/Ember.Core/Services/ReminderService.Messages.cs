using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Sent reminders whose buttons or notifications are still open: Discord reminder messages and delivered web push reminders.</summary>
public partial class ReminderService
{
    /// <summary>Remembers a sent Discord reminder message until it is closed.</summary>
    public async Task AddReminderMessageAsync(ReminderMessage message, CancellationToken cancellationToken = default)
    {
        _db.ReminderMessages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The open reminder messages (untracked) of the habit or routine for the local day <paramref name="date"/>.</summary>
    public Task<List<ReminderMessage>> GetReminderMessagesAsync(int? habitId, int? routineId, DateOnly date,
        CancellationToken cancellationToken = default)
        => _db.ReminderMessages
            .AsNoTracking()
            .Where(m => m.HabitId == habitId && m.RoutineId == routineId && m.Date == date)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Every open reminder message (untracked) with its user's timezone id, whether that habit is done on the message's day for a
    /// habit's message (always false for a routine's message) and the user's language, in one query.
    /// </summary>
    public async Task<List<(ReminderMessage Message, string Timezone, bool HabitDone, string Language)>> GetOpenReminderMessagesAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.ReminderMessages
            .AsNoTracking()
            .Join(_db.Users, m => m.UserId, u => u.Id, (m, u) => new
            {
                Message = m,
                u.Timezone,
                HabitDone = m.HabitId != null && _db.HabitLogs.Any(l => l.HabitId == m.HabitId && l.Date == m.Date && l.Completed),
                u.Language,
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Message, r.Timezone, r.HabitDone, r.Language)).ToList();
    }

    /// <summary>Forgets the reminder message with the database id <paramref name="id"/>.</summary>
    public Task DeleteReminderMessageAsync(int id, CancellationToken cancellationToken = default)
        => _db.ReminderMessages.Where(m => m.Id == id).ExecuteDeleteAsync(cancellationToken);

    /// <summary>Forgets the reminder message with the Discord message id <paramref name="messageId"/>, if it is remembered.</summary>
    public Task DeleteReminderMessageByDiscordIdAsync(ulong messageId, CancellationToken cancellationToken = default)
        => _db.ReminderMessages.Where(m => m.MessageId == messageId).ExecuteDeleteAsync(cancellationToken);

    /// <summary>Bot only: remembers that the habit's or routine's reminder for <paramref name="date"/> was delivered by web push (at most once).</summary>
    public async Task AddPushReminderAsync(Guid userId, int? habitId, int? routineId, DateOnly date, CancellationToken cancellationToken = default)
    {
        if (await _db.PushReminders.AnyAsync(r => r.UserId == userId && r.HabitId == habitId && r.RoutineId == routineId && r.Date == date,
                cancellationToken))
            return;
        _db.PushReminders.Add(new PushReminder { UserId = userId, HabitId = habitId, RoutineId = routineId, Date = date });
        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Bot only: every remembered web push reminder (untracked) with its user's timezone id and, for a habit's, whether that habit
    /// is done on the reminder's day (always false for a routine's), in one query.
    /// </summary>
    public async Task<List<(PushReminder Reminder, string Timezone, bool HabitDone)>> GetPushRemindersAsync(
        CancellationToken cancellationToken = default)
    {
        var rows = await _db.PushReminders
            .AsNoTracking()
            .Join(_db.Users, r => r.UserId, u => u.Id, (r, u) => new
            {
                Reminder = r,
                u.Timezone,
                HabitDone = r.HabitId != null && _db.HabitLogs.Any(l => l.HabitId == r.HabitId && l.Date == r.Date && l.Completed),
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Reminder, r.Timezone, r.HabitDone)).ToList();
    }

    /// <summary>Bot only: forgets the remembered web push reminder with the database id <paramref name="id"/>.</summary>
    public Task DeletePushReminderAsync(int id, CancellationToken cancellationToken = default)
        => _db.PushReminders.Where(r => r.Id == id).ExecuteDeleteAsync(cancellationToken);
}
