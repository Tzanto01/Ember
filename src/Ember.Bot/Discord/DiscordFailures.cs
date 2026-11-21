using Discord.Net;

namespace Ember.Bot.Discord;

/// <summary>Recognizes how a Discord REST call fails when Discord refuses it or cannot be reached.</summary>
internal static class DiscordFailures
{
    /// <summary>
    /// True for a refused request (<see cref="HttpException"/>, such as a message that is already gone or closed DMs), a request that
    /// timed out in Discord.Net's queue or hit its rate limit (<see cref="TimeoutException"/>), and a network failure or HTTP timeout on
    /// the way (<see cref="HttpRequestException"/>, <see cref="TaskCanceledException"/>).
    /// </summary>
    public static bool IsRequestFailure(Exception ex)
        => ex is HttpException or TimeoutException or HttpRequestException or TaskCanceledException;
}
