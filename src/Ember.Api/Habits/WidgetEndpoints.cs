using System.Net.Http.Headers;
using Ember.Api.Auth;
using Ember.Api.Hosting;
using Ember.Core.Services;

namespace Ember.Api.Habits;

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

    public static RouteGroupBuilder MapWidget(this RouteGroupBuilder api)
    {
        api.MapPost("/widget/check-in", async (CheckInRequest request, HttpContext http, AuthSessions sessions, HabitService habits,
            UserSettingsService settings) =>
        {
            if (!AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var header)
                || !string.Equals(header.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
                || header.Parameter is not { Length: > 0 and <= MaxKeyLength } key)
                return Problems.Unauthenticated();

            if (await sessions.FindWidgetUserAsync(key) is not { } userId) return Problems.Unauthenticated();

            if (request.HabitId is not { } habitId) return Problems.Validation("habitId", "Required.");
            if (!Formats.TryParseDate(request.Date, out var day)) return Problems.Validation("date", Formats.DateMessage);
            if (day != await settings.GetLocalTodayAsync(userId)) return Problems.Validation("date", "Must be today.");

            if (Problems.DayEdit(await habits.AddCheckInAsync(userId, habitId, day)) is { } problem) return problem;

            if (await habits.GetHabitAsync(userId, habitId) is not { } habit) return Problems.NotFound();
            var count = habit.Logs.FirstOrDefault(l => l.Date == day)?.Count ?? 0;
            var state = count >= habit.TimesPerDay ? "done"
                : (await habits.GetLowDaysAsync(userId)).Contains(day) ? "rest"
                : count > 0 ? "partial"
                : "open";
            return Results.Ok(new CheckInDto(state, count, habit.TimesPerDay));
        }).RequireRateLimiting(RateLimits.WidgetPolicy);

        return api;
    }
}
