namespace Ember.Core.Services;

/// <summary>
/// The Discord side of <see cref="ReminderService"/>: its user-scoped methods keyed on a Discord user ID instead of the Ember user ID.
/// Each one resolves the Discord ID through <see cref="HabitService.FindDiscordUserAsync"/> or
/// <see cref="HabitService.ResolveOrCreateDiscordUserAsync"/>, then calls the <see cref="Guid"/> overload.
/// </summary>
public partial class ReminderService
{
    public async Task<bool> SnoozeReminderAsync(ulong discordUserId, int habitId, DateTimeOffset sentAtUtc)
        => await SnoozeReminderAsync(await _habits.FindDiscordUserAsync(discordUserId), habitId, sentAtUtc);

    public async Task<int> RescheduleRemindersAsync(ulong discordUserId)
        => await RescheduleRemindersAsync(await _habits.FindDiscordUserAsync(discordUserId));

    public async Task<bool> ClaimDmPauseNoticeAsync(ulong discordUserId)
        => await ClaimDmPauseNoticeAsync(await _habits.FindDiscordUserAsync(discordUserId));

    public async Task ReleaseDmPauseNoticeAsync(ulong discordUserId)
        => await ReleaseDmPauseNoticeAsync(await _habits.FindDiscordUserAsync(discordUserId));

    public async Task ResumeDmRemindersAsync(ulong discordUserId)
        => await ResumeDmRemindersAsync(await _habits.FindDiscordUserAsync(discordUserId));

    public async Task SetSecondReminderMinutesAsync(ulong discordUserId, int minutes, string? language = null)
        => await SetSecondReminderMinutesAsync(await _habits.ResolveOrCreateDiscordUserAsync(discordUserId, language), minutes);
}
