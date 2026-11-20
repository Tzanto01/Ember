using System.Globalization;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Reminder destinations: the Discord answer, browsers (push subscriptions) and phones.</summary>
public partial class ReminderService
{
    /// <summary>Most phones a single user keeps; registering another drops the least recently registered.</summary>
    public const int MaxReminderPhonesPerUser = 10;

    /// <summary>The user (untracked) with logins, push subscriptions and phones, or null.</summary>
    public Task<User?> GetUserWithDestinationsAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users
            .AsNoTracking()
            .Include(u => u.Logins)
            .Include(u => u.PushSubscriptions)
            .Include(u => u.ReminderPhones)
            .AsSplitQuery()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    /// <summary>
    /// Turns the user's destination <paramref name="id"/> on or off. For <see cref="ReminderDestinations.Discord"/> it records the
    /// account's answer (<see cref="User.DiscordReminders"/>) and needs a Discord login. False when the id is malformed or names no
    /// destination of this user.
    /// </summary>
    public async Task<bool> SetReminderDestinationAsync(Guid userId, string id, bool enabled)
    {
        if (id == ReminderDestinations.Discord)
            return await _db.Users
                .Where(u => u.Id == userId && _db.UserLogins.Any(l => l.UserId == userId && l.LoginProvider == DiscordLogin.Provider))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordReminders, enabled)) == 1;
        if (ParseId(id, ReminderDestinations.BrowserPrefix) is { } subscriptionId)
            return await _db.PushSubscriptions
                .Where(s => s.Id == subscriptionId && s.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, enabled)) == 1;
        if (ParseId(id, ReminderDestinations.PhonePrefix) is { } phoneId)
            return await _db.ReminderPhones
                .Where(p => p.Id == phoneId && p.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, enabled)) == 1;
        return false;
    }

    /// <summary>The number after <paramref name="prefix"/> when <paramref name="id"/> is exactly that prefix and a positive id in canonical form.</summary>
    private static int? ParseId(string id, string prefix)
        => id.StartsWith(prefix, StringComparison.Ordinal)
            && int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && value > 0 && id == prefix + value.ToString(CultureInfo.InvariantCulture)
                ? value
                : null;

    /// <summary>
    /// Registers the phone with <paramref name="installId"/> for the user and <paramref name="sessionId"/>. A phone already registered
    /// by this user keeps its id and <see cref="ReminderPhone.Enabled"/> and gets the new label and session; one registered by another
    /// account is removed there and registered anew (enabled) here. Afterwards the user keeps at most <see cref="MaxReminderPhonesPerUser"/>
    /// phones; the least recently registered beyond that are deleted.
    /// </summary>
    public async Task<ReminderPhone> SaveReminderPhoneAsync(Guid userId, string installId, string label, Guid sessionId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var phone = await _db.ReminderPhones.FirstOrDefaultAsync(p => p.InstallId == installId);
        if (phone is not null && phone.UserId != userId)
        {
            _db.ReminderPhones.Remove(phone);
            await _db.SaveChangesAsync();
            phone = null;
        }
        if (phone is null)
        {
            phone = new ReminderPhone { UserId = userId, InstallId = installId };
            _db.ReminderPhones.Add(phone);
        }

        phone.Label = label;
        phone.SessionId = sessionId;
        phone.CreatedAt = now;
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (_db.Entry(phone).State == EntityState.Added)
        {
            _db.Entry(phone).State = EntityState.Detached;
            await _db.ReminderPhones.Where(p => p.InstallId == installId && p.UserId != userId).ExecuteDeleteAsync();
            var updated = await _db.ReminderPhones
                .Where(p => p.InstallId == installId && p.UserId == userId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Label, label)
                    .SetProperty(p => p.SessionId, sessionId)
                    .SetProperty(p => p.CreatedAt, now));
            if (updated == 0)
            {
                phone = new ReminderPhone { UserId = userId, InstallId = installId, Label = label, SessionId = sessionId, CreatedAt = now };
                _db.ReminderPhones.Add(phone);
                await _db.SaveChangesAsync();
            }
            else
            {
                phone = await _db.ReminderPhones.AsNoTracking().FirstAsync(p => p.InstallId == installId && p.UserId == userId);
            }
        }

        await TrimReminderPhonesAsync(userId);
        return phone;
    }

    /// <summary>Deletes the user's least recently registered phones beyond <see cref="MaxReminderPhonesPerUser"/>.</summary>
    internal async Task TrimReminderPhonesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var excess = await _db.ReminderPhones
            .Where(p => p.UserId == userId)
            .OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
            .Skip(MaxReminderPhonesPerUser)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        if (excess.Count > 0)
            await _db.ReminderPhones.Where(p => excess.Contains(p.Id)).ExecuteDeleteAsync(cancellationToken);
    }
}
