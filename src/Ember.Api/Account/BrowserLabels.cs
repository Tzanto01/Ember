namespace Ember.Api;

/// <summary>Readable browser names ("Firefox on Linux") from a User-Agent header.</summary>
internal static class BrowserLabels
{
    public const string Fallback = "Browser";

    private static readonly (string Name, string[] Tokens)[] Browsers =
    [
        ("Samsung Internet", ["SamsungBrowser/"]),
        ("Edge", ["Edg/", "EdgA/", "EdgiOS/"]),
        ("Opera", ["OPR/"]),
        ("Firefox", ["Firefox/", "FxiOS/"]),
        ("Chrome", ["Chrome/", "CriOS/"]),
        ("Safari", ["Safari/"]),
    ];

    private static readonly (string Name, string[] Tokens)[] Systems =
    [
        ("Windows", ["Windows"]),
        ("iOS", ["iPhone", "iPad", "iPod"]),
        ("macOS", ["Macintosh", "Mac OS X"]),
        ("Android", ["Android"]),
        ("ChromeOS", ["CrOS"]),
        ("Linux", ["Linux"]),
    ];

    /// <summary>"&lt;Browser&gt; on &lt;OS&gt;" for <paramref name="userAgent"/>; an unknown part is left out, "Browser" when both are unknown.</summary>
    public static string From(string? userAgent)
    {
        var browser = Find(Browsers, userAgent);
        var system = Find(Systems, userAgent);
        return system is null ? browser ?? Fallback : $"{browser ?? Fallback} on {system}";
    }

    private static string? Find((string Name, string[] Tokens)[] candidates, string? userAgent)
        => userAgent is null
            ? null
            : candidates.FirstOrDefault(c => c.Tokens.Any(t => userAgent.Contains(t, StringComparison.Ordinal))).Name;
}
