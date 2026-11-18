using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Primitives;

namespace Ember.Ops;

/// <summary>
/// Decides who a request comes from. Identity headers count only from a trusted proxy address; then either an allow-listed
/// <c>Tailscale-User-Login</c> or the configured app capability in <c>Tailscale-App-Capabilities</c> lets it in.
/// </summary>
public static class OpsAccess
{
    public const string LoginHeader = "Tailscale-User-Login";
    public const string CapabilitiesHeader = "Tailscale-App-Capabilities";
    public const int MaxAuthorLength = 100;

    /// <summary>The author name for an allowed request, or null when it must be refused.</summary>
    public static string? Resolve(IPAddress? remote, IHeaderDictionary headers, AccessOptions options)
    {
        if (remote is null || !IsTrusted(remote, options.TrustedProxies)) return null;

        var login = Single(headers[LoginHeader]);
        if (login is { Length: > 0 }
            && options.AllowedLogins.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), login.Trim(), StringComparison.OrdinalIgnoreCase)) is { } allowed)
            return Cap(allowed.Trim());

        return string.IsNullOrWhiteSpace(options.AppCapability) ? null : FromCapability(Single(headers[CapabilitiesHeader]), options.AppCapability);
    }

    private static bool IsTrusted(IPAddress remote, string[] trusted)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        foreach (var entry in trusted)
            if (IPAddress.TryParse(entry.Trim(), out var ip) && (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).Equals(remote))
                return true;
        return false;
    }

    private static string? Single(StringValues values) => values.Count == 1 ? values[0] : null;

    private static string? FromCapability(string? header, string capability)
    {
        if (string.IsNullOrEmpty(header)) return null;
        try
        {
            using var doc = JsonDocument.Parse(header);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty(capability, out var grants)
                || grants.ValueKind != JsonValueKind.Array)
                return null;

            if (grants.GetArrayLength() > 0 && grants[0] is { ValueKind: JsonValueKind.Object } first
                && first.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.String
                && author.GetString() is { } name && !string.IsNullOrWhiteSpace(name))
                return Cap(name.Trim());
            return "tailnet";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Cap(string author) => author.Length <= MaxAuthorLength ? author : author[..MaxAuthorLength];
}
