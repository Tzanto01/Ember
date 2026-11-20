using Ember.Core.Localization;
using Ember.Core.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Ember.Core.Services;

/// <summary>
/// The Discord identity lookup of <see cref="HabitService"/>: the Ember user ID linked to a Discord user, finding it
/// (<see cref="FindDiscordUserAsync"/>) or creating a bot-only user (<see cref="ResolveOrCreateDiscordUserAsync"/>), and the
/// language to speak to a Discord user in. The bot resolves the user once per interaction and calls the owning service with
/// the <see cref="Guid"/>. The lookup is never cached: the API can merge or delete accounts while the bot runs.
/// </summary>
public partial class HabitService
{
    /// <summary>
    /// The Ember user ID linked to this Discord user, or <see cref="Guid.Empty"/> when Ember stores nothing about them.
    /// Every user-scoped query with <see cref="Guid.Empty"/> matches nothing, so callers need no special case.
    /// </summary>
    public async Task<Guid> FindDiscordUserAsync(ulong discordUserId, CancellationToken cancellationToken = default)
    {
        var key = DiscordLogin.Key(discordUserId);
        return await _db.UserLogins
            .Where(l => l.LoginProvider == DiscordLogin.Provider && l.ProviderKey == key)
            .Select(l => l.UserId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// The Ember user ID linked to this Discord user, creating a bot-only user (no email, no password) when there is none.
    /// Safe against concurrent calls: losing the insert race to another request is not an error.
    /// </summary>
    /// <param name="discordUserId">The Discord user.</param>
    /// <param name="language">The new user's <see cref="User.Language"/> (one of <see cref="Languages.All"/>); ignored for an existing user.</param>
    /// <param name="cancellationToken">Cancels the lookup and the insert.</param>
    public async Task<Guid> ResolveOrCreateDiscordUserAsync(ulong discordUserId, string? language = null, CancellationToken cancellationToken = default)
    {
        var existing = await FindDiscordUserAsync(discordUserId, cancellationToken);
        if (existing != Guid.Empty) return existing;

        var userName = DiscordLogin.UserName(discordUserId);
        var user = new User
        {
            Id = Guid.CreateVersion7(),
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            Language = language is not null && Languages.All.Contains(language) ? language : Languages.English,
            CreatedUtc = _time.GetUtcNow().UtcDateTime,
        };
        user.Logins.Add(new IdentityUserLogin<Guid>
        {
            LoginProvider = DiscordLogin.Provider,
            ProviderKey = DiscordLogin.Key(discordUserId),
            ProviderDisplayName = DiscordLogin.Provider,
            UserId = user.Id,
        });
        _db.Users.Add(user);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
            return user.Id;
        }
        catch (DbUpdateException)
        {
            _db.Entry(user).State = EntityState.Detached;
            foreach (var login in user.Logins) _db.Entry(login).State = EntityState.Detached;
            var winner = await FindDiscordUserAsync(discordUserId, cancellationToken);
            if (winner == Guid.Empty) throw;
            return winner;
        }
    }

    /// <summary>
    /// The language to speak to this Discord user in: the linked account's <see cref="User.Language"/>, or for someone Ember stores
    /// nothing about, their Discord locale (<see cref="Languages.FromLocale"/>).
    /// </summary>
    public async Task<string> GetDiscordLanguageAsync(ulong discordUserId, string? discordLocale)
    {
        var key = DiscordLogin.Key(discordUserId);
        return await _db.UserLogins
            .Where(l => l.LoginProvider == DiscordLogin.Provider && l.ProviderKey == key)
            .Join(_db.Users, l => l.UserId, u => u.Id, (_, u) => u.Language)
            .FirstOrDefaultAsync() ?? Languages.FromLocale(discordLocale);
    }
}
