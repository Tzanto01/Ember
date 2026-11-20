using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>Whether the account shows streaks, and the tips it has seen.</summary>
public partial class HabitService
{
    /// <summary>The account's <see cref="User.ShowStreaks"/>; false when there is no account.</summary>
    public async Task<bool> GetShowStreaksAsync(Guid userId)
        => await _db.Users.Where(u => u.Id == userId).Select(u => u.ShowStreaks).FirstOrDefaultAsync();

    public async Task<bool> GetShowStreaksAsync(ulong discordUserId)
        => await GetShowStreaksAsync(await FindDiscordUserAsync(discordUserId));

    /// <summary>Sets the account's <see cref="User.ShowStreaks"/> and marks the <see cref="User.StreaksTip"/> seen.</summary>
    public async Task SetShowStreaksAsync(Guid userId, bool show)
    {
        await _db.Users
            .Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.ShowStreaks, show));
        await MarkTipSeenAsync(userId, User.StreaksTip);
    }

    public async Task SetShowStreaksAsync(ulong discordUserId, bool show, string? language = null)
        => await SetShowStreaksAsync(await ResolveOrCreateDiscordUserAsync(discordUserId, language), show);

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

    public async Task<bool> NeedsStreaksQuestionAsync(ulong discordUserId)
        => await NeedsStreaksQuestionAsync(await FindDiscordUserAsync(discordUserId));
}
