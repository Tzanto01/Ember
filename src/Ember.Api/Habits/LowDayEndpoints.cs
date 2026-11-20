using System.Security.Claims;
using Ember.Api.Hosting;
using Ember.Core.Services;

namespace Ember.Api.Habits;

/// <summary>The user's low days, which protect all their habits.</summary>
internal static class LowDayEndpoints
{
    public static RouteGroupBuilder MapLowDays(this RouteGroupBuilder api)
    {
        api.MapGet("/low-days", async (string? from, string? to, ClaimsPrincipal principal, HabitService habits, UserSettingsService settings) =>
        {
            var userId = principal.UserId();
            var today = await settings.GetLocalTodayAsync(userId);
            var errors = new Dictionary<string, string[]>();
            var end = Formats.DateOr(to, today, "to", errors);
            var start = Formats.DateOr(from, Formats.DaysBefore(end, 365), "from", errors);
            if (errors.Count == 0 && start > end) errors["from"] = ["Can't be after to."];
            if (errors.Count > 0) return Problems.Validation(errors);

            var lowDays = await habits.GetLowDaysAsync(userId);
            return Results.Ok(lowDays.Where(d => d >= start && d <= end).Order().ToList());
        });

        api.MapPut("/low-days/{date}", async (string date, ClaimsPrincipal principal, HabitService habits, UserSettingsService settings,
            ConsentService consent) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();
            if (day > await settings.GetLocalTodayAsync(userId)) return Problems.DayEdit(DayEditResult.InFuture)!;

            if (await habits.GetEarliestHabitStartAsync(userId) is not { } start || day < start) return Problems.DayEdit(DayEditResult.BeforeHabitStart)!;

            return Problems.DayEdit(await habits.SetLowDayAsync(userId, day)) ?? Results.NoContent();
        });

        api.MapDelete("/low-days/{date}", async (string date, ClaimsPrincipal principal, HabitService habits) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            await habits.ClearLowDayAsync(principal.UserId(), day);
            return Results.NoContent();
        });

        return api;
    }
}
