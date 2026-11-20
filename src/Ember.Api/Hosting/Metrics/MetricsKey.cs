using System.Security.Cryptography;
using System.Text;

namespace Ember.Api;

/// <summary>The key in a file (at least 32 bytes), kept in memory and read again only when a bearer does not match it.</summary>
public sealed class MetricsKey(string? keyFile)
{
    private volatile byte[]? _hash;

    /// <summary>True when the bearer of <paramref name="authorization"/> equals the key, compared in constant time.</summary>
    public bool Matches(string? authorization)
    {
        const string scheme = "Bearer ";
        if (authorization is null || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var sent = SHA256.HashData(Encoding.UTF8.GetBytes(authorization[scheme.Length..]));
        if (_hash is { } cached && CryptographicOperations.FixedTimeEquals(sent, cached)) return true;
        _hash = ApiMetrics.ReadKey(keyFile);
        return _hash is { } fresh && CryptographicOperations.FixedTimeEquals(sent, fresh);
    }
}
