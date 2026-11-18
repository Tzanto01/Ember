using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ember.Core.Data;
using Ember.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>A Fireside friend as the caller sees them; <see cref="Id"/> is the caller's own link id, never a user id.</summary>
public sealed record FiresideFriend(int Id, string Name, bool Asking, bool? CheckedInToday, bool Muted, bool ShareFlame,
    string? RenamedFrom, bool JoinedViaMyCode, bool CanSendWarmth);

/// <summary>Undismissed warmth to the caller; <see cref="FriendId"/> is the caller's link id for the sender.</summary>
public sealed record FiresideWarmth(int Id, int FriendId, string Name, string Message, DateTime SentAt);

/// <summary>The caller's Fireside page. <see cref="CodeProtected"/> and <see cref="CodeExpiresAt"/> are null without an active code.</summary>
public sealed record FiresideState(bool Enabled, string? Name, bool Notify, DateTime? AskingUntil, string? CodeProtected,
    DateTime? CodeExpiresAt, IReadOnlyList<FiresideFriend> Friends, IReadOnlyList<FiresideWarmth> Warmth);

/// <summary>A new friend code (8 characters, shown <c>XXXX XXXX</c>) and when (UTC) it stops working.</summary>
public sealed record FiresideNewCode(string Code, DateTime ExpiresAt);

/// <summary>Outcome of <see cref="FiresideService.JoinAsync"/>.</summary>
public enum FiresideJoinResult
{
    /// <summary>Joined, or already friends; the code was used up either way.</summary>
    Joined,

    /// <summary>The code is invalid, expired, used or the caller's own (one answer for all, no oracle). Counted as a failed try.</summary>
    InvalidCode,

    /// <summary>Fireside is off for the caller.</summary>
    Off,

    /// <summary>One of the two already has <see cref="FiresideService.MaxFriends"/> friends. The code was not used up.</summary>
    Full,

    /// <summary>Too many failed tries in the last hour.</summary>
    RateLimited,
}

/// <summary>Outcome of <see cref="FiresideService.SendWarmthAsync"/>.</summary>
public enum FiresideSendResult
{
    /// <summary>Stored (also when the receiver muted the sender; then it stays invisible).</summary>
    Sent,

    /// <summary>No such link of the caller.</summary>
    NotFound,

    /// <summary>Not one of <see cref="FiresideService.Messages"/>.</summary>
    InvalidMessage,

    /// <summary>Already sent today's warmth to that friend.</summary>
    Limit,
}

/// <summary>Outcome of <see cref="FiresideService.UpdateSettingsAsync"/>.</summary>
public enum FiresideSettingsResult
{
    Saved,

    /// <summary>Turning Fireside on needs a name, given or already stored.</summary>
    NameRequired,

    /// <summary>The user doesn't exist.</summary>
    NotFound,
}

/// <summary>A notification the bot should deliver: warmth (<see cref="Message"/> set) or "joined your fireside" (<see cref="Message"/> null).</summary>
public sealed record FiresideNotice(Guid ReceiverId, int LinkId, string Name, string? Message);

/// <summary>
/// Fireside: friends added by single-use codes who send each other one of five fixed messages ("warmth"). Every write that involves
/// two accounts locks both user rows (<c>SELECT ... FOR UPDATE</c>, ordered by id, PostgreSQL only) so join, turning off, removing,
/// sending, code replacement and account merge serialize. Every id the caller passes is checked against the caller first.
/// </summary>
public class FiresideService
{
    /// <summary>Most friends one account can have.</summary>
    public const int MaxFriends = 20;

    /// <summary>Longest Fireside name, after trimming.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Characters of a friend code: no 0, O, 1, I or L.</summary>
    public const string CodeAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    /// <summary>Characters in a friend code.</summary>
    public const int CodeLength = 8;

    /// <summary>Longest raw code input looked at; anything longer is a failed try.</summary>
    public const int MaxCodeInputLength = 64;

    /// <summary>Failed joins allowed per account in <see cref="JoinFailureWindow"/>.</summary>
    public const int MaxJoinFailures = 10;

    public static readonly TimeSpan JoinFailureWindow = TimeSpan.FromHours(1);

    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>How long warmth is kept.</summary>
    public static readonly TimeSpan WarmthLifetime = TimeSpan.FromDays(30);

    /// <summary>The five fixed messages.</summary>
    public static readonly IReadOnlyList<string> Messages = ["thinking", "proud", "easy", "notAlone", "blanket"];

    private readonly EmberDbContext _db;
    private readonly TimeProvider _time;

    public FiresideService(EmberDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>The trimmed name when it is 1 to <see cref="MaxNameLength"/> characters without control or format characters, else null.</summary>
    public static string? ValidName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxNameLength) return null;
        return trimmed.EnumerateRunes().Any(r => Rune.IsControl(r) || Rune.GetUnicodeCategory(r) == UnicodeCategory.Format) ? null : trimmed;
    }

    /// <summary>The code without whitespace, upper-cased, when it is <see cref="CodeLength"/> characters of <see cref="CodeAlphabet"/>; else null.</summary>
    public static string? NormalizeCode(string? code)
    {
        if (code is null || code.Length > MaxCodeInputLength) return null;
        var normalized = string.Concat(code.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();
        return normalized.Length == CodeLength && normalized.All(c => CodeAlphabet.Contains(c)) ? normalized : null;
    }

    /// <summary>The code as shown: <c>XXXX XXXX</c>.</summary>
    public static string DisplayCode(string code) => $"{code[..4]} {code[4..]}";

    private static string HashCode(string normalized) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private async Task LockUsersAsync(Guid first, Guid second, CancellationToken cancellationToken = default)
    {
        if (_db.Database.IsNpgsql())
            await _db.Database.ExecuteSqlAsync(
                $"""SELECT 1 FROM "AspNetUsers" WHERE "Id" IN ({first}, {second}) ORDER BY "Id" FOR UPDATE""", cancellationToken);
    }

    private static (DateTime Start, DateTime End) LocalDayUtc(string? timezone, TimeProvider time)
    {
        var tz = TimezoneHelper.Find(timezone ?? "UTC") ?? TimeZoneInfo.Utc;
        var today = TimezoneHelper.LocalToday(timezone, time);
        return (TimezoneHelper.LocalToUtc(today.ToDateTime(TimeOnly.MinValue), tz),
            TimezoneHelper.LocalToUtc(today.AddDays(1).ToDateTime(TimeOnly.MinValue), tz));
    }

    /// <summary>The user's Fireside page, or null when the user doesn't exist.</summary>
    public async Task<FiresideState?> GetStateAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        var user = await _db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.FiresideEnabled, u.FiresideName, u.FiresideNotify, u.WarmthAskUntilUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (user is null) return null;

        if (!user.FiresideEnabled)
            return new FiresideState(false, user.FiresideName, user.FiresideNotify, null, null, null, [], []);

        var code = await _db.FiresideCodes.AsNoTracking()
            .Where(c => c.UserId == userId && c.ExpiresAtUtc > now)
            .Select(c => new { c.CodeProtected, c.ExpiresAtUtc })
            .FirstOrDefaultAsync(cancellationToken);
        var warmth = await (
                from w in _db.Warmth
                where w.ToUserId == userId && w.DismissedAtUtc == null
                join l in _db.FiresideLinks on new { Owner = w.ToUserId, Friend = w.FromUserId } equals new { Owner = l.OwnerId, Friend = l.FriendId }
                where !l.Muted
                orderby w.SentAtUtc descending, w.Id descending
                select new FiresideWarmth(w.Id, l.Id, l.Friend.FiresideName ?? "", w.Message, w.SentAtUtc))
            .ToListAsync(cancellationToken);

        return new FiresideState(user.FiresideEnabled, user.FiresideName, user.FiresideNotify,
            user.WarmthAskUntilUtc > now ? user.WarmthAskUntilUtc : null,
            code?.CodeProtected, code?.ExpiresAtUtc, await GetFriendsAsync(userId, null, cancellationToken), warmth);
    }

    /// <summary>The caller's friend with link <paramref name="linkId"/>, or null when it isn't the caller's.</summary>
    public async Task<FiresideFriend?> GetFriendAsync(Guid userId, int linkId, CancellationToken cancellationToken = default)
        => (await GetFriendsAsync(userId, linkId, cancellationToken)).FirstOrDefault();

    private async Task<List<FiresideFriend>> GetFriendsAsync(Guid userId, int? linkId, CancellationToken cancellationToken)
    {
        var now = Now;
        var timezone = await _db.Users.Where(u => u.Id == userId).Select(u => u.Timezone).FirstOrDefaultAsync(cancellationToken);
        var (start, end) = LocalDayUtc(timezone, _time);
        var rows = await _db.FiresideLinks.AsNoTracking()
            .Where(l => l.OwnerId == userId && (linkId == null || l.Id == linkId))
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id,
                l.FriendId,
                l.Muted,
                l.ShareFlame,
                l.KnownName,
                l.JoinedViaMyCode,
                Name = l.Friend.FiresideName,
                l.Friend.Timezone,
                l.Friend.WarmthAskUntilUtc,
                FlameShared = _db.FiresideLinks.Any(r => r.OwnerId == l.FriendId && r.FriendId == userId && r.ShareFlame),
                SentToday = _db.Warmth.Count(w => w.FromUserId == userId && w.ToUserId == l.FriendId && w.SentAtUtc >= start && w.SentAtUtc < end),
            })
            .ToListAsync(cancellationToken);

        var todays = rows.Where(r => r.FlameShared).ToDictionary(r => r.FriendId, r => TimezoneHelper.LocalToday(r.Timezone, _time));
        var ids = todays.Keys.ToList();
        var dates = todays.Values.Distinct().ToList();
        HashSet<Guid> done = ids.Count == 0 ? [] : (await _db.HabitLogs.AsNoTracking()
                .Where(l => l.Completed && ids.Contains(l.Habit.UserId) && dates.Contains(l.Date))
                .Select(l => new { l.Habit.UserId, l.Date })
                .Distinct()
                .ToListAsync(cancellationToken))
            .Where(l => todays[l.UserId] == l.Date)
            .Select(l => l.UserId)
            .ToHashSet();

        return rows.Select(r =>
        {
            var name = r.Name ?? "";
            var asking = r.WarmthAskUntilUtc > now;
            return new FiresideFriend(r.Id, name, asking, r.FlameShared ? done.Contains(r.FriendId) : null, r.Muted, r.ShareFlame,
                r.KnownName != name ? r.KnownName : null, r.JoinedViaMyCode, r.SentToday == 0 || (r.SentToday == 1 && asking));
        }).ToList();
    }

    /// <summary>
    /// Turns Fireside on or off, with an optional new <paramref name="name"/> (already validated by <see cref="ValidName"/>) and
    /// <paramref name="notify"/> setting. Turning it off deletes the user's code, every link in both directions and all warmth from or
    /// to the user and stops asking; the name is kept so turning it on again can prefill it.
    /// </summary>
    public async Task<FiresideSettingsResult> UpdateSettingsAsync(Guid userId, bool enabled, string? name, bool? notify,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await LockUsersAsync(userId, userId, cancellationToken);
        var stored = await _db.Users.Where(u => u.Id == userId).Select(u => new { u.FiresideName }).FirstOrDefaultAsync(cancellationToken);
        if (stored is null) return FiresideSettingsResult.NotFound;
        name ??= stored.FiresideName;
        if (enabled && name is null) return FiresideSettingsResult.NameRequired;

        if (!enabled)
        {
            await _db.FiresideCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await _db.FiresideLinks.Where(l => l.OwnerId == userId || l.FriendId == userId).ExecuteDeleteAsync(cancellationToken);
            await _db.Warmth.Where(w => w.FromUserId == userId || w.ToUserId == userId).ExecuteDeleteAsync(cancellationToken);
        }

        await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.FiresideEnabled, enabled)
            .SetProperty(u => u.FiresideName, name)
            .SetProperty(u => u.FiresideNotify, u => notify ?? u.FiresideNotify)
            .SetProperty(u => u.WarmthAskUntilUtc, u => enabled ? u.WarmthAskUntilUtc : null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return FiresideSettingsResult.Saved;
    }

    /// <summary>
    /// Makes a new code for the user, replacing any active one, valid for <see cref="CodeLifetime"/>. <paramref name="protect"/>
    /// encrypts the code for <see cref="FiresideCode.CodeProtected"/>; without it the code can't be shown again. Null when Fireside is off.
    /// </summary>
    public async Task<FiresideNewCode?> CreateCodeAsync(Guid userId, Func<string, string>? protect = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await LockUsersAsync(userId, userId, cancellationToken);
        if (!await _db.Users.AnyAsync(u => u.Id == userId && u.FiresideEnabled, cancellationToken)) return null;

        string code, hash;
        do
        {
            code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, CodeLength));
            hash = HashCode(code);
        }
        while (await _db.FiresideCodes.AnyAsync(c => c.CodeHash == hash, cancellationToken));

        var expiresAt = Now + CodeLifetime;
        await _db.FiresideCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        _db.FiresideCodes.Add(new FiresideCode { UserId = userId, CodeHash = hash, CodeProtected = protect?.Invoke(code), ExpiresAtUtc = expiresAt });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return new FiresideNewCode(code, expiresAt);
    }

    /// <summary>Deletes the user's active code, if any.</summary>
    public Task DeleteCodeAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.FiresideCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Joins the fireside of the owner of <paramref name="code"/>, in one transaction with both user rows locked. A code that is
    /// malformed (over <see cref="MaxCodeInputLength"/> characters, outside the alphabet, not <see cref="CodeLength"/> long), unknown,
    /// expired, the caller's own or of an owner with Fireside off counts as a failed try (kept even though the join fails); after
    /// <see cref="MaxJoinFailures"/> in <see cref="JoinFailureWindow"/> every try is <see cref="FiresideJoinResult.RateLimited"/>, with
    /// the seconds until the window ends. Already friends uses up the code and returns the existing friend. A new friendship creates both
    /// link rows, marks the owner's row <see cref="FiresideLink.JoinedViaMyCode"/> (and pending notification when the owner's are on)
    /// and deletes the code.
    /// </summary>
    public async Task<(FiresideJoinResult Result, FiresideFriend? Friend, int RetryAfterSeconds)> JoinAsync(Guid userId, string? code,
        CancellationToken cancellationToken = default)
    {
        var now = Now;
        var normalized = NormalizeCode(code);
        var hash = normalized is null ? null : HashCode(normalized);
        var ownerId = hash is null
            ? userId
            : await _db.FiresideCodes.Where(c => c.CodeHash == hash).Select(c => (Guid?)c.UserId).FirstOrDefaultAsync(cancellationToken) ?? userId;

        int linkId;
        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockUsersAsync(userId, ownerId, cancellationToken);
            var me = await _db.Users.Where(u => u.Id == userId)
                .Select(u => new { u.FiresideEnabled, u.FiresideName, u.FiresideJoinFailures, u.FiresideJoinWindowUtc })
                .FirstOrDefaultAsync(cancellationToken);
            if (me is null || !me.FiresideEnabled) return (FiresideJoinResult.Off, null, 0);

            var windowOpen = me.FiresideJoinWindowUtc is { } window && window > now - JoinFailureWindow;
            if (windowOpen && me.FiresideJoinFailures >= MaxJoinFailures)
                return (FiresideJoinResult.RateLimited, null,
                    Math.Max(1, (int)Math.Ceiling((me.FiresideJoinWindowUtc!.Value + JoinFailureWindow - now).TotalSeconds)));

            var owner = ownerId == userId ? null : await _db.Users.Where(u => u.Id == ownerId)
                .Select(u => new { u.FiresideEnabled, u.FiresideName, u.FiresideNotify })
                .FirstOrDefaultAsync(cancellationToken);
            if (owner is not { FiresideEnabled: true }
                || !await _db.FiresideCodes.AnyAsync(c => c.UserId == ownerId && c.CodeHash == hash && c.ExpiresAtUtc > now, cancellationToken))
            {
                await _db.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.FiresideJoinFailures, windowOpen ? me.FiresideJoinFailures + 1 : 1)
                    .SetProperty(u => u.FiresideJoinWindowUtc, windowOpen ? me.FiresideJoinWindowUtc : now), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return (FiresideJoinResult.InvalidCode, null, 0);
            }

            var existing = await _db.FiresideLinks.Where(l => l.OwnerId == userId && l.FriendId == ownerId)
                .Select(l => (int?)l.Id).FirstOrDefaultAsync(cancellationToken);
            if (existing is null)
            {
                var counts = await _db.FiresideLinks.Where(l => l.OwnerId == userId || l.OwnerId == ownerId)
                    .GroupBy(l => l.OwnerId).Select(g => g.Count()).ToListAsync(cancellationToken);
                if (counts.Any(c => c >= MaxFriends)) return (FiresideJoinResult.Full, null, 0);
            }

            await _db.FiresideCodes.Where(c => c.UserId == ownerId).ExecuteDeleteAsync(cancellationToken);
            if (existing is { } id)
                linkId = id;
            else
            {
                var mine = new FiresideLink { OwnerId = userId, FriendId = ownerId, KnownName = owner.FiresideName ?? "", CreatedAt = now };
                _db.FiresideLinks.Add(mine);
                _db.FiresideLinks.Add(new FiresideLink
                {
                    OwnerId = ownerId,
                    FriendId = userId,
                    KnownName = me.FiresideName ?? "",
                    JoinedViaMyCode = true,
                    JoinNotifyPending = owner.FiresideNotify,
                    CreatedAt = now,
                });
                await _db.SaveChangesAsync(cancellationToken);
                linkId = mine.Id;
            }
            await transaction.CommitAsync(cancellationToken);
        }

        _db.ChangeTracker.Clear();
        return (FiresideJoinResult.Joined, await GetFriendAsync(userId, linkId, cancellationToken), 0);
    }

    /// <summary>
    /// Sends <paramref name="message"/> to the caller's friend with link <paramref name="linkId"/>: once per friend and sender-local day,
    /// plus one more that day while the friend is asking. To a friend who muted the sender it is stored already dismissed and never
    /// notified, so nothing tells the sender.
    /// </summary>
    public async Task<FiresideSendResult> SendWarmthAsync(Guid userId, int linkId, string? message, CancellationToken cancellationToken = default)
    {
        if (message is null || !Messages.Contains(message)) return FiresideSendResult.InvalidMessage;
        var friendId = await _db.FiresideLinks.Where(l => l.Id == linkId && l.OwnerId == userId)
            .Select(l => (Guid?)l.FriendId).FirstOrDefaultAsync(cancellationToken);
        if (friendId is not { } toId) return FiresideSendResult.NotFound;

        var now = Now;
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await LockUsersAsync(userId, toId, cancellationToken);
        var friend = await _db.FiresideLinks
            .Where(r => r.OwnerId == toId && r.FriendId == userId
                && _db.FiresideLinks.Any(l => l.Id == linkId && l.OwnerId == userId && l.FriendId == toId))
            .Join(_db.Users, r => r.OwnerId, u => u.Id, (r, u) => new { r.Muted, Notify = u.FiresideNotify, AskUntil = u.WarmthAskUntilUtc })
            .FirstOrDefaultAsync(cancellationToken);
        if (friend is null) return FiresideSendResult.NotFound;

        var timezone = await _db.Users.Where(u => u.Id == userId).Select(u => u.Timezone).FirstAsync(cancellationToken);
        var (start, end) = LocalDayUtc(timezone, _time);
        var sent = await _db.Warmth.CountAsync(w => w.FromUserId == userId && w.ToUserId == toId && w.SentAtUtc >= start && w.SentAtUtc < end,
            cancellationToken);
        if (sent > 1 || (sent == 1 && !(friend.AskUntil > now))) return FiresideSendResult.Limit;

        _db.Warmth.Add(new Warmth
        {
            FromUserId = userId,
            ToUserId = toId,
            Message = message,
            SentAtUtc = now,
            DismissedAtUtc = friend.Muted ? now : null,
            NotifyPending = !friend.Muted && friend.Notify,
        });
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
        return FiresideSendResult.Sent;
    }

    /// <summary>Sets <see cref="FiresideLink.Muted"/> and/or <see cref="FiresideLink.ShareFlame"/> on the caller's link; null when it isn't the caller's.</summary>
    public async Task<FiresideFriend?> UpdateFriendAsync(Guid userId, int linkId, bool? muted, bool? shareFlame,
        CancellationToken cancellationToken = default)
    {
        if (await _db.FiresideLinks.Where(l => l.Id == linkId && l.OwnerId == userId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Muted, l => muted ?? l.Muted)
                .SetProperty(l => l.ShareFlame, l => shareFlame ?? l.ShareFlame), cancellationToken) == 0)
            return null;
        return await GetFriendAsync(userId, linkId, cancellationToken);
    }

    /// <summary>
    /// Acknowledges the rename notice (<see cref="FiresideLink.KnownName"/> becomes the friend's current name) and/or the joined notice
    /// (clears <see cref="FiresideLink.JoinedViaMyCode"/>) on the caller's link; null when it isn't the caller's.
    /// </summary>
    public async Task<FiresideFriend?> AcknowledgeAsync(Guid userId, int linkId, bool rename, bool joined,
        CancellationToken cancellationToken = default)
    {
        var link = await _db.FiresideLinks.Where(l => l.Id == linkId && l.OwnerId == userId)
            .Select(l => new { l.Friend.FiresideName }).FirstOrDefaultAsync(cancellationToken);
        if (link is null) return null;
        var known = rename ? link.FiresideName : null;
        if (await _db.FiresideLinks.Where(l => l.Id == linkId && l.OwnerId == userId).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.KnownName, l => known ?? l.KnownName)
                .SetProperty(l => l.JoinedViaMyCode, l => !joined && l.JoinedViaMyCode)
                .SetProperty(l => l.JoinNotifyPending, l => !joined && l.JoinNotifyPending), cancellationToken) == 0)
            return null;
        return await GetFriendAsync(userId, linkId, cancellationToken);
    }

    /// <summary>
    /// Removes the friendship of the caller's link in both directions and the warmth between the two; with <paramref name="revokeCode"/>
    /// also the caller's active code. Nobody is notified. False when the link isn't the caller's.
    /// </summary>
    public async Task<bool> RemoveFriendAsync(Guid userId, int linkId, bool revokeCode, CancellationToken cancellationToken = default)
    {
        var friendId = await _db.FiresideLinks.Where(l => l.Id == linkId && l.OwnerId == userId)
            .Select(l => (Guid?)l.FriendId).FirstOrDefaultAsync(cancellationToken);
        if (friendId is not { } otherId) return false;

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await LockUsersAsync(userId, otherId, cancellationToken);
        if (await _db.FiresideLinks
                .Where(l => (l.OwnerId == userId && l.FriendId == otherId) || (l.OwnerId == otherId && l.FriendId == userId))
                .ExecuteDeleteAsync(cancellationToken) == 0)
            return false;
        await _db.Warmth
            .Where(w => (w.FromUserId == userId && w.ToUserId == otherId) || (w.FromUserId == otherId && w.ToUserId == userId))
            .ExecuteDeleteAsync(cancellationToken);
        if (revokeCode) await _db.FiresideCodes.Where(c => c.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>Dismisses warmth to the caller; false when there is no such warmth to the caller.</summary>
    public async Task<bool> DismissWarmthAsync(Guid userId, int warmthId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        return await _db.Warmth.Where(w => w.Id == warmthId && w.ToUserId == userId).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.DismissedAtUtc, w => w.DismissedAtUtc ?? now)
            .SetProperty(w => w.NotifyPending, false), cancellationToken) == 1;
    }

    /// <summary>Shows "Could use some warmth today" until the user's next local midnight and returns that instant (UTC); null when Fireside is off.</summary>
    public async Task<DateTime?> AskAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var timezone = await _db.Users.Where(u => u.Id == userId && u.FiresideEnabled).Select(u => u.Timezone).FirstOrDefaultAsync(cancellationToken);
        if (timezone is null) return null;
        var (_, until) = LocalDayUtc(timezone, _time);
        return await _db.Users.Where(u => u.Id == userId && u.FiresideEnabled)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.WarmthAskUntilUtc, until), cancellationToken) == 1 ? until : null;
    }

    /// <summary>Stops asking for warmth.</summary>
    public Task StopAskingAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.WarmthAskUntilUtc, (DateTime?)null), cancellationToken);

    /// <summary>Whether Fireside is on for the user.</summary>
    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken cancellationToken = default)
        => _db.Users.AnyAsync(u => u.Id == userId && u.FiresideEnabled, cancellationToken);

    /// <summary>Bot only: deletes warmth older than <see cref="WarmthLifetime"/> and expired codes.</summary>
    public async Task DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var now = Now;
        var oldest = now - WarmthLifetime;
        await _db.Warmth.Where(w => w.SentAtUtc < oldest).ExecuteDeleteAsync(cancellationToken);
        await _db.FiresideCodes.Where(c => c.ExpiresAtUtc <= now).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Bot only: up to <paramref name="max"/> ids of warmth and of links whose notification is pending.</summary>
    public async Task<(List<int> WarmthIds, List<int> LinkIds)> GetPendingNoticesAsync(int max, CancellationToken cancellationToken = default)
        => (await _db.Warmth.Where(w => w.NotifyPending).OrderBy(w => w.Id).Select(w => w.Id).Take(max).ToListAsync(cancellationToken),
            await _db.FiresideLinks.Where(l => l.JoinNotifyPending).OrderBy(l => l.Id).Select(l => l.Id).Take(max).ToListAsync(cancellationToken));

    /// <summary>
    /// Bot only: claims the pending notification of warmth <paramref name="warmthId"/> (at most once) and returns it while it is still
    /// visible: undismissed, the receiver's link to the sender exists and isn't muted, and the receiver's notifications are on. Else null.
    /// </summary>
    public async Task<FiresideNotice?> ClaimWarmthNoticeAsync(int warmthId, CancellationToken cancellationToken = default)
    {
        if (await _db.Warmth.Where(w => w.Id == warmthId && w.NotifyPending)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.NotifyPending, false), cancellationToken) != 1)
            return null;
        return await (
                from w in _db.Warmth
                where w.Id == warmthId && w.DismissedAtUtc == null
                join l in _db.FiresideLinks on new { Owner = w.ToUserId, Friend = w.FromUserId } equals new { Owner = l.OwnerId, Friend = l.FriendId }
                where !l.Muted && _db.Users.Any(u => u.Id == w.ToUserId && u.FiresideNotify)
                select new FiresideNotice(w.ToUserId, l.Id, l.Friend.FiresideName ?? "", w.Message))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Bot only: claims the pending "joined your fireside" notification of link <paramref name="linkId"/> (at most once) and returns it
    /// while the notice is still open and the owner's notifications are on. Else null.
    /// </summary>
    public async Task<FiresideNotice?> ClaimJoinNoticeAsync(int linkId, CancellationToken cancellationToken = default)
    {
        if (await _db.FiresideLinks.Where(l => l.Id == linkId && l.JoinNotifyPending)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.JoinNotifyPending, false), cancellationToken) != 1)
            return null;
        return await _db.FiresideLinks
            .Where(l => l.Id == linkId && l.JoinedViaMyCode && _db.Users.Any(u => u.Id == l.OwnerId && u.FiresideNotify))
            .Select(l => new FiresideNotice(l.OwnerId, l.Id, l.Friend.FiresideName ?? "", null))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
