using Ember.Core.Data;
using Ember.Core.Localization;
using Ember.Core.Models;
using Ember.Core.Rules;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>
/// The account's settings: time zone and local day, language, Today order, grace days, theme, calm mode, the habit note offer,
/// whether streaks show, and the tips the account has seen.
/// </summary>
public class UserSettingsService
{
    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;

    public UserSettingsService(EmberDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>The settings <see cref="UpdateAsync"/> changes; a null one stays as it is. The caller validates the values.</summary>
    public sealed record SettingsUpdate(int? GraceDaysPerWeek = null, string? Theme = null, bool? ShowStreaks = null,
        string? Language = null, bool? CalmMode = null, bool? OfferHabitNote = null, string? Timezone = null);

    /// <summary>
    /// Stores the non-null settings of <paramref name="update"/> with the setters below (a time zone without rescheduling reminders),
    /// then stops tracking every entity this database context holds. The writes bypass the context, so reads later in the same scope,
    /// such as the signed-in user the sign-in loaded, see the stored values, including earlier set-based writes in that scope.
    /// </summary>
    public async Task UpdateAsync(Guid userId, SettingsUpdate update)
    {
        if (update.GraceDaysPerWeek is { } grace) await SetGraceDaysAsync(userId, grace);
        if (update.Theme is { } theme) await SetThemeAsync(userId, theme);
        if (update.ShowStreaks is { } showStreaks) await SetShowStreaksAsync(userId, showStreaks);
        if (update.Language is { } language) await SetLanguageAsync(userId, language);
        if (update.CalmMode is { } calmMode) await SetCalmModeAsync(userId, calmMode);
        if (update.OfferHabitNote is { } offer) await SetOfferHabitNoteAsync(userId, offer);
        if (update.Timezone is { } timezone) await SetTimezoneAsync(userId, timezone);
        _db.ChangeTracker.Clear();
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

    /// <summary>The user's time zone; UTC when the user doesn't exist or has no known time zone.</summary>
    public async Task<TimeZoneInfo> GetTimeZoneAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return TimeZones.Find(user?.Timezone ?? "UTC") ?? TimeZoneInfo.Utc;
    }

    /// <summary>The user's current local day (UTC when the user has no known timezone).</summary>
    public async Task<DateOnly> GetLocalTodayAsync(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        return TimeZones.LocalToday(user?.Timezone, _time.GetUtcNow());
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

    /// <summary>The account's <see cref="User.ShowStreaks"/>; false when there is no account.</summary>
    public async Task<bool> GetShowStreaksAsync(Guid userId)
        => await _db.Users.Where(u => u.Id == userId).Select(u => u.ShowStreaks).FirstOrDefaultAsync();

    /// <summary>Sets the account's <see cref="User.ShowStreaks"/> and marks the <see cref="User.StreaksTip"/> seen.</summary>
    public async Task SetShowStreaksAsync(Guid userId, bool show)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ShowStreaks, show));
        await MarkTipSeenAsync(userId, User.StreaksTip);
    }

    /// <summary>Adds <paramref name="tipId"/> to the account's <see cref="User.TipsSeen"/> unless it is already there.</summary>
    public async Task MarkTipSeenAsync(Guid userId, string tipId)
    {
        if (_db.Database.IsNpgsql())
        {
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "AspNetUsers" SET "TipsSeen" = array_append("TipsSeen", {tipId}) WHERE "Id" = {userId} AND NOT ({tipId} = ANY("TipsSeen"))""");
            return;
        }

        var seen = await _db.Users.AsNoTracking()
            .Where(u => u.Id == userId && !u.TipsSeen.Contains(tipId))
            .Select(u => u.TipsSeen)
            .FirstOrDefaultAsync();
        if (seen is null) return;

        List<string> updated = [.. seen, tipId];
        await _db.Users
            .Where(u => u.Id == userId && !u.TipsSeen.Contains(tipId))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TipsSeen, updated));
    }

    /// <summary>
    /// Whether the bot should ask the streaks question: the account exists, shows no streaks and has not seen the
    /// <see cref="User.StreaksTip"/>.
    /// </summary>
    public async Task<bool> NeedsStreaksQuestionAsync(Guid userId)
        => await _db.Users.AnyAsync(u => u.Id == userId && !u.ShowStreaks && !u.TipsSeen.Contains(User.StreaksTip));
}
