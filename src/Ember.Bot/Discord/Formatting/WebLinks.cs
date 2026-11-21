using Microsoft.Extensions.Configuration;

namespace Ember.Bot.Discord.Formatting;

/// <summary>Links into the web app, from the configured <see cref="OriginKey"/>.</summary>
internal static class WebLinks
{
    internal const string OriginKey = "Ember:WebOrigin";

    /// <summary>The web app's origin without a trailing slash, or null when none is configured.</summary>
    internal static string? Origin(IConfiguration config)
        => config[OriginKey] is { Length: > 0 } origin ? origin.TrimEnd('/') : null;
}
