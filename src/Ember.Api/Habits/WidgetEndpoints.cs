using System.Net.Http.Headers;
using Ember.Api.Auth;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.AspNetCore.Identity;

namespace Ember.Api.Habits;

/// <summary>
/// The home screen widget's check-in and its rows for today and tomorrow, authorized by the <c>Widget</c> key of a live session
/// alone (issued by <c>POST /api/me/widget-key</c>). Nothing else accepts the key, and using it doesn't extend the session.
/// </summary>
internal static class WidgetEndpoints
{
    private const string Scheme = "Widget";

    /// <summary>Longer than any key the API issues (43 characters); longer input is unknown without hashing it.</summary>
    private const int MaxKeyLength = 128;

    public sealed record CheckInRequest(int? HabitId, string? Date);

    /// <summary>The habit's day after the check-in: <c>done</c>, <c>rest</c> (a low day), <c>partial</c> or <c>open</c>.</summary>
    public sealed record CheckInDto(string State, int Count, int TimesPerDay);

    /// <summary>
    /// The widget's snapshot for the key's user: <see cref="WidgetDays.On"/> on their local today, with <c>Next</c> for the day
    /// after. <c>UserId</c>, <c>TimeZone</c> and the order are those of <c>GET /api/me</c>.
    /// </summary>
    public sealed record TodayDto(Guid UserId, string TimeZone, DateOnly Date, IReadOnlyList<WidgetRow> Habits, int More, int Done,
        int Total, WidgetDay Next);

    public static RouteGroupBuilder MapWidget(this RouteGroupBuilder api)
    {
        api.MapGet("/widget/today", async (HttpContext http, AuthSessions sessions, ConsentService consent, UserManager<User> users,
            HabitService habits, TimeProvider time) =>
        {
            if (await WidgetUserAsync(http, sessions) is not { } userId
                || await users.FindByIdAsync(userId.ToString()) is not { } user)
                return Problems.Unauthenticated();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();

            var tz = TimeZones.Find(user.Timezone) ?? TimeZoneInfo.Utc;
            var today = TimeZones.LocalToday(user.Timezone, time.GetUtcNow());
            var list = await habits.GetHabitsAsync(userId);
            var routines = await habits.GetRoutinesAsync(userId);
            var lowDays = await habits.GetLowDaysAsync(userId);
            WidgetDay On(DateOnly date) => WidgetDays.On(list, routines, user.TodayOrder, date, today, tz, lowDays);
            var day = On(today);
            return Results.Ok(new TodayDto(user.Id, user.Timezone, day.Date, day.Habits, day.More, day.Done, day.Total, On(today.AddDays(1))));
        }).RequireRateLimiting(RateLimits.WidgetPolicy);

        api.MapPost("/widget/check-in", async (CheckInRequest request, HttpContext http, AuthSessions sessions, HabitService habits,
            UserSettingsService settings) =>
        {
            if (await WidgetUserAsync(http, sessions) is not { } userId) return Problems.Unauthenticated();

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

    /// <summary>The user of the live session whose key the <c>Authorization: Widget &lt;key&gt;</c> header carries, or null.</summary>
    private static async Task<Guid?> WidgetUserAsync(HttpContext http, AuthSessions sessions)
        => AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var header)
            && string.Equals(header.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            && header.Parameter is { Length: > 0 and <= MaxKeyLength } key
                ? await sessions.FindWidgetUserAsync(key)
                : null;
}
