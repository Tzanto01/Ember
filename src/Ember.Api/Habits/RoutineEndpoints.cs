using System.Security.Claims;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;

namespace Ember.Api.Habits;

/// <summary>Routines: named, ordered groups of habits with an optional shared reminder.</summary>
internal static class RoutineEndpoints
{
    public sealed record RoutineDto(int Id, string Name, string? ReminderTime, List<string> ReminderChannels, List<int> HabitIds,
        List<string> ReminderExclusions, bool ServerReminders);

    public sealed record CreateRoutineRequest(string? Name, string? ReminderTime, string?[]? ReminderExclusions, int[]? HabitIds);
    public sealed record UpdateRoutineRequest(string? Name, string? ReminderTime, bool? ClearReminder, string?[]? ReminderExclusions,
        int[]? HabitIds);
    public sealed record RoutineOrderRequest(int[]? Ids);

    public static RouteGroupBuilder MapRoutines(this RouteGroupBuilder api)
    {
        api.MapGet("/routines", async (ClaimsPrincipal principal, HabitService habits, ReminderService reminders) =>
        {
            var userId = principal.UserId();
            var destinations = await LoadDestinations(reminders, userId);
            return (await habits.GetRoutinesAsync(userId)).Select(r => ToDto(r, destinations)).ToList();
        });

        api.MapPost("/routines", async (CreateRoutineRequest request, ClaimsPrincipal principal, HabitService habits, ReminderService reminders) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = HabitEndpoints.ValidName(request.Name, errors, Limits.RoutineNameLength);
            var reminder = HabitEndpoints.ParseReminder(request.ReminderTime, errors);
            var userId = principal.UserId();
            var exclusions = await HabitEndpoints.ParseReminderExclusionsAsync(request.ReminderExclusions, reminders, userId, errors);
            await ValidateHabitIdsAsync(request.HabitIds, habits, userId, errors);
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, routine) = await habits.AddRoutineAsync(userId, name!, reminder, exclusions, request.HabitIds);
            return result switch
            {
                RoutineWriteResult.Saved => Results.Created($"/api/routines/{routine!.Id}", ToDto(routine, await LoadDestinations(reminders, userId))),
                RoutineWriteResult.LimitReached => Problems.Conflict(
                    $"You already have {Limits.RoutinesPerUser} routines, the most Ember can hold. Delete one to add another."),
                _ => Problems.NotFound(),
            };
        });

        api.MapPatch("/routines/{id:int}", async (int id, UpdateRoutineRequest request, ClaimsPrincipal principal, HabitService habits,
            ReminderService reminders) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = request.Name is null ? null : HabitEndpoints.ValidName(request.Name, errors, Limits.RoutineNameLength);
            var reminder = HabitEndpoints.ParseReminder(request.ReminderTime, errors);
            if (request.ReminderTime is not null && request.ClearReminder == true)
                errors["reminderTime"] = ["Can't be combined with clearReminder."];
            var userId = principal.UserId();
            var exclusions = await HabitEndpoints.ParseReminderExclusionsAsync(request.ReminderExclusions, reminders, userId, errors);
            await ValidateHabitIdsAsync(request.HabitIds, habits, userId, errors);
            if (errors.Count > 0) return Problems.Validation(errors);

            if (await habits.EditRoutineAsync(userId, id, name, reminder, request.ClearReminder == true, exclusions, request.HabitIds)
                != RoutineWriteResult.Saved)
                return Problems.NotFound();

            return (await habits.GetRoutinesAsync(userId)).FirstOrDefault(r => r.Id == id) is { } routine
                ? Results.Ok(ToDto(routine, await LoadDestinations(reminders, userId)))
                : Problems.NotFound();
        });

        api.MapPut("/routines/order", async (RoutineOrderRequest request, ClaimsPrincipal principal, HabitService habits) =>
        {
            if (request.Ids is null) return Problems.Validation("ids", "List every routine.");
            if (request.Ids.Distinct().Count() != request.Ids.Length) return Problems.Validation("ids", "Each routine can be listed once.");

            return await habits.SetRoutineOrderAsync(principal.UserId(), request.Ids) == RoutineOrderResult.Saved
                ? Results.NoContent()
                : Problems.Conflict("Your routines changed in the meantime. Reload and try again.");
        });

        api.MapDelete("/routines/{id:int}", async (int id, ClaimsPrincipal principal, HabitService habits) =>
            await habits.DeleteRoutineAsync(principal.UserId(), id) ? Results.NoContent() : Problems.NotFound());

        return api;
    }

    private static async Task ValidateHabitIdsAsync(int[]? habitIds, HabitService habits, Guid userId, Dictionary<string, string[]> errors)
    {
        if (habitIds is not { Length: > 0 }) return;
        if (habitIds.Distinct().Count() != habitIds.Length)
        {
            errors["habitIds"] = ["Each habit can be listed once."];
            return;
        }
        var kinds = await habits.GetHabitKindsAsync(userId);
        if (habitIds.Any(id => kinds.GetValueOrDefault(id) == HabitKind.LetGo))
            errors["habitIds"] = ["Let-go habits can't be in a routine."];
    }

    private static async Task<(User User, HashSet<string> Ids)> LoadDestinations(ReminderService reminders, Guid userId)
    {
        var user = await reminders.GetUserWithDestinationsAsync(userId) ?? new User { Id = userId };
        return (user, ReminderDestinations.Ids(user));
    }

    private static RoutineDto ToDto(Routine routine, (User User, HashSet<string> Ids) destinations)
    {
        var targets = ReminderDestinations.Resolve(destinations.User, routine.ReminderExclusions);
        return new(
            routine.Id, routine.Name,
            routine.ReminderTime is { } time ? Formats.Time(time) : null,
            ReminderDestinations.LegacyChannels(targets),
            routine.Habits.OrderBy(h => h.RoutinePosition).Select(h => h.Id).ToList(),
            routine.ReminderExclusions.Where(destinations.Ids.Contains).ToList(), targets.Any);
    }
}
