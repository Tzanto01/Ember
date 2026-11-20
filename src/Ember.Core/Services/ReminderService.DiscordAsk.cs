using System.Globalization;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>The one-time Discord question "remind me in Discord DMs?" and its answer.</summary>
public partial class ReminderService
{
    /// <summary>Most users <see cref="GetDiscordAskUserIdsAsync"/> returns per call.</summary>
    public const int MaxDiscordAsksPerRun = 20;

    /// <summary>
    /// Bot only: up to <see cref="MaxDiscordAsksPerRun"/> users with a Discord login who never answered or were asked about Discord
    /// reminders, haven't withdrawn their health-data consent and have a habit with reminder times or a routine with a reminder time, with
    /// their Discord ids.
    /// </summary>
    public async Task<List<(Guid UserId, ulong DiscordUserId)>> GetDiscordAskUserIdsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _db.Users
            .Where(u => u.DiscordReminders == null && u.DiscordRemindersAskedAt == null && u.ConsentWithdrawnAt == null
                && (u.Habits.Any(h => h.ReminderTimes.Count > 0) || u.Routines.Any(r => r.ReminderTime != null)))
            .SelectMany(u => u.Logins.Where(l => l.LoginProvider == DiscordLogin.Provider), (u, l) => new { u.Id, l.ProviderKey })
            .OrderBy(r => r.Id)
            .Take(MaxDiscordAsksPerRun)
            .ToListAsync(cancellationToken);
        return rows.Select(r => (r.Id, ulong.Parse(r.ProviderKey, CultureInfo.InvariantCulture))).ToList();
    }

    /// <summary>Bot only: records that the user was asked about Discord reminders, so they are never asked again.</summary>
    public Task MarkDiscordRemindersAskedAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordRemindersAskedAt, now), cancellationToken);
    }

    /// <summary>Bot only: forgets that the user was asked about Discord reminders, so the next ask run asks again.</summary>
    public Task ClearDiscordRemindersAskedAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordRemindersAskedAt, (DateTime?)null), cancellationToken);

    /// <summary>
    /// Bot only, for a reminder time set through the bot: records yes (and the ask, so it isn't sent) when the account
    /// never answered about Discord reminders. Returns whether Discord reminders are on for the account afterwards.
    /// </summary>
    public async Task<bool> DefaultDiscordRemindersOnAsync(Guid userId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _db.Users.Where(u => u.Id == userId && u.DiscordReminders == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DiscordReminders, true)
                .SetProperty(u => u.DiscordRemindersAskedAt, u => u.DiscordRemindersAskedAt ?? now));
        return await _db.Users.Where(u => u.Id == userId).Select(u => u.DiscordReminders).FirstOrDefaultAsync() == true;
    }

    /// <summary>
    /// Records the account's answer to "remind me in Discord DMs" (latest answer wins) and that it was asked,
    /// so the one-time question is never sent afterwards.
    /// </summary>
    public async Task SetDiscordRemindersAsync(Guid userId, bool enabled)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DiscordReminders, enabled)
                .SetProperty(u => u.DiscordRemindersAskedAt, u => u.DiscordRemindersAskedAt ?? now));
    }
}
