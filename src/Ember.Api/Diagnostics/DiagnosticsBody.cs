using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Ember.Api.Lookout;

namespace Ember.Api.Diagnostics;

/// <summary>
/// Lookout's <c>DiagnosticsInput</c> for one upload: compact UTF-8 JSON in the contract's property order, with
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the bytes equal the web preview's <c>JSON.stringify</c> of the same
/// ASCII content. <c>userRef</c> is always null: uploads are never linked to an account.
/// </summary>
internal static class DiagnosticsBody
{
    /// <summary>Largest body Ember stores, under Lookout's 64 KB limit for this route.</summary>
    public const int MaxBytes = 64_000;

    /// <summary>The oldest entry kept: newer than <c>createdAt</c> minus this, plus one minute.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The newest entry kept: at most this after <c>createdAt</c> (device clock skew).</summary>
    public static readonly TimeSpan MaxAhead = TimeSpan.FromHours(1);

    private const string RefPrefix = "EMB-";
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>A fresh public reference: <c>EMB-</c> and 8 Crockford base32 characters from 40 random bits.</summary>
    public static string NewRef()
    {
        Span<byte> random = stackalloc byte[5];
        RandomNumberGenerator.Fill(random);
        var bits = 0L;
        foreach (var b in random) bits = (bits << 8) | b;
        var chars = new char[8];
        for (var i = chars.Length - 1; i >= 0; i--, bits >>= 5) chars[i] = Crockford[(int)(bits & 31)];
        return RefPrefix + new string(chars);
    }

    /// <summary>
    /// The body of a valid <paramref name="request"/> (see <see cref="DiagnosticsValidation.Errors"/>) received at
    /// <paramref name="createdAt"/>, keeping only the launches and exits from <c>createdAt - 7 days + 1 minute</c> to
    /// <c>createdAt + 1 hour</c>.
    /// </summary>
    public static byte[] Serialize(string reference, DateTime createdAt, DiagnosticsRequest request)
    {
        var oldest = createdAt - MaxAge + TimeSpan.FromMinutes(1);
        var newest = createdAt + MaxAhead;
        bool Kept(string? at) => DiagnosticsValidation.TryParseAt(at, out var time) && time >= oldest && time <= newest;

        return JsonSerializer.SerializeToUtf8Bytes(new DiagnosticsInput(reference, request.Platform!, request.Version, request.Os,
            request.Device, LookoutIngest.Utc(createdAt), null,
            [.. request.Launches!.Where(l => Kept(l!.At)).Select(l => new Launch(l!.At!, l.Event!))],
            [.. request.Exits!.Where(e => Kept(e!.At)).Select(e => new Exit(e!.At!, e.Reason!, e.Signal, e.Exception!, e.Frames!))]), Json);
    }

    private sealed record DiagnosticsInput(string Ref, string Platform, string? Version, string? Os, string? Device, string CreatedAt,
        string? UserRef, IReadOnlyList<Launch> Launches, IReadOnlyList<Exit> Exits);

    private sealed record Launch(string At, string Event);

    private sealed record Exit(string At, string Reason, string? Signal, string?[] Exception, string?[] Frames);
}
