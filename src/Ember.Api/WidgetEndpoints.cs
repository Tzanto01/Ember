using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Ember.Core.Data;
using Ember.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Ember.Api;

/// <summary>
/// The home screen widget's check-in, authorized by the <c>Widget</c> key of a live session alone (issued by
/// <c>POST /api/me/widget-key</c>). Nothing else accepts the key, and using it doesn't extend the session.
/// </summary>
internal static class WidgetEndpoints
{
    private const string Scheme = "Widget";

    /// <summary>Longer than any key the API issues (43 characters); longer input is unknown without hashing it.</summary>
    private const int MaxKeyLength = 128;

    public sealed record CheckInRequest(int? HabitId, string? Date);

    /// <summary>The habit's day after the check-in: <c>done</c>, <c>rest</c> (a low day), <c>partial</c> or <c>open</c>.</summary>
    public sealed record CheckInDto(string State, int Count, int TimesPerDay);

    /// <summary>What the session stores for <paramref name="key"/>: its SHA-256 of the UTF-8 bytes.</summary>
    public static byte[] KeyHash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    public static RouteGroupBuilder MapWidget(this RouteGroupBuilder api)
    {
        api.MapPost("/widget/check-in", async (CheckInRequest request, HttpContext http, EmberDbContext db, HabitService habits, TimeProvider time) =>
        {
            if (!AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var header)
                || !string.Equals(header.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
                || header.Parameter is not { Length: > 0 and <= MaxKeyLength } key)
                return Problems.Unauthenticated();

            var hash = KeyHash(key);
            var now = time.GetUtcNow();
            var session = await db.AuthSessions.AsNoTracking()
                .Where(s => s.WidgetKeyHash == hash && s.ExpiresUtc > now)
                .Select(s => new { s.UserId, s.WidgetKeyHash })
                .FirstOrDefaultAsync();
            if (session?.WidgetKeyHash is not { } stored || !CryptographicOperations.FixedTimeEquals(stored, hash))
                return Problems.Unauthenticated();

            var userId = session.UserId;
            if (request.HabitId is not { } habitId) return Problems.Validation("habitId", "Required.");
            if (!Formats.TryParseDate(request.Date, out var day)) return Problems.Validation("date", Formats.DateMessage);
            if (day != await habits.GetLocalTodayAsync(userId)) return Problems.Validation("date", "Must be today.");

            if (Problems.DayEdit(await habits.AddCheckInAsync(userId, habitId, day)) is { } problem) return problem;

            if (await habits.GetHabitAsync(userId, habitId) is not { } habit) return Problems.NotFound();
            var count = habit.Logs.FirstOrDefault(l => l.Date == day)?.Count ?? 0;
            var state = count >= habit.TimesPerDay ? "done"
                : (await habits.GetLowDaysAsync(userId)).Contains(day) ? "rest"
                : count > 0 ? "partial"
                : "open";
            return Results.Ok(new CheckInDto(state, count, habit.TimesPerDay));
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        return api;
    }
}
