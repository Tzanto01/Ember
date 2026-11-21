using System.Security.Claims;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;

namespace Ember.Api.Habits;

/// <summary>Habits, their days and the habit templates.</summary>
internal static class HabitEndpoints
{
    public sealed record DayDto(DateOnly Date, string State, bool? Completed, int Count);
    public sealed record WeekDto(int Done, int Target);
    public sealed record StatsDto(int? Streak, int? GraceUsed, int? BestStreak, int Last7, WeekDto? Week);
    public sealed record LetGoDayDto(DateOnly Date, string State, int Slips, bool MadeIt);
    public sealed record LetGoStatsDto(int DaysFree, int LongestStretch, int FreeDaysThisMonth, int SlipDaysLast30);
    public sealed record SlipDto(DateOnly Date, int Count, List<string> Reasons);
    public sealed record MadeItDto(DateOnly Date);

    /// <summary>A habit. <c>Today</c> is a <see cref="DayDto"/> and <c>Stats</c> a <see cref="StatsDto"/> for a build habit, a <see cref="LetGoDayDto"/> and a <see cref="LetGoStatsDto"/> for a let-go habit.</summary>
    public sealed record HabitDto(
        int Id, string Name, string Frequency, int? WeeklyTarget, string? ReminderTime,
        DateOnly? PausedFrom, DateOnly? PausedUntil, bool PausedToday, DateTime CreatedAt, DateOnly StartDate,
        object Today, object Stats, List<string> ReminderChannels, List<string>? Days, int TimesPerDay, int? RoutineId,
        List<string> ReminderTimes, List<string> ReminderExclusions, bool ServerReminders, string Kind);

    public sealed record TemplateDto(string Key, string Name, string Description, string SuggestedReminderTime, string Frequency, int? WeeklyTarget);

    public sealed record CreateHabitRequest(string? Name, string? ReminderTime, string? Frequency, int? WeeklyTarget,
        string?[]? Days, int? TimesPerDay, string?[]? ReminderTimes, string?[]? ReminderExclusions, string? Kind);
    public sealed record UpdateHabitRequest(string? Name, string? ReminderTime, bool? ClearReminder, string? Frequency, int? WeeklyTarget,
        string?[]? ReminderExclusions, string?[]? Days, int? TimesPerDay, string?[]? ReminderTimes, string? Kind);
    public sealed record SlipRequest(string? Date, string? Reason);
    public sealed record PauseRequest(string? Until);
    public sealed record DayRequest(bool? Completed, int? Count);

    private sealed record UserDays(TimeZoneInfo Tz, DateOnly Today, int GraceDays, IReadOnlySet<DateOnly> LowDays, User Destinations,
        HashSet<string> DestinationIds);

    public static RouteGroupBuilder MapHabits(this RouteGroupBuilder api)
    {
        api.MapGet("/habits", async (ClaimsPrincipal principal, HabitService habits, UserSettingsService settings, ReminderService reminders) =>
        {
            var userId = principal.UserId();
            var list = await habits.GetHabitsAsync(userId);
            var days = await LoadUserDays(habits, settings, reminders, userId);
            return list.Select(h => ToDto(h, days)).ToList();
        });

        api.MapGet("/habits/{id:int}", async (int id, ClaimsPrincipal principal, HabitService habits, UserSettingsService settings,
            ReminderService reminders) =>
            await HabitResult(habits, settings, reminders, principal.UserId(), id));

        api.MapPost("/habits", async (CreateHabitRequest request, ClaimsPrincipal principal, HabitService habits, UserSettingsService settings,
            ReminderService reminders) =>
        {
            var errors = new Dictionary<string, string[]>();
            var name = HabitValidation.ValidName(request.Name, errors);
            var reminder = request.ReminderTimes is null ? HabitValidation.ParseReminder(request.ReminderTime, errors) : null;
            var frequency = HabitValidation.ParseFrequency(request.Frequency);
            if (frequency is null) errors["frequency"] = [HabitValidation.FrequencyMessage];
            HabitValidation.ValidateWeeklyTarget(request.WeeklyTarget, frequency, errors);
            var days = HabitValidation.ValidateDays(request.Days, frequency, null, errors);
            var timesPerDay = HabitValidation.ValidateTimesPerDay(request.TimesPerDay ?? 1, frequency, errors);
            var reminderTimes = HabitValidation.ParseReminderTimes(request.ReminderTimes, timesPerDay, errors);
            var kind = HabitValidation.ParseKind(request.Kind);
            if (kind is null) errors["kind"] = [HabitValidation.KindMessage];
            else if (kind == HabitKind.LetGo)
                HabitValidation.ValidateLetGo(frequency, request.TimesPerDay, request.WeeklyTarget, request.Days, request.ReminderTimes?.Length, errors);
            var userId = principal.UserId();
            var exclusions = await HabitValidation.ParseReminderExclusionsAsync(request.ReminderExclusions, reminders, userId, errors);
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, habit) = await habits.AddHabitAsync(userId, name!, reminder, frequency!.Value, request.WeeklyTarget,
                exclusions, days, timesPerDay, reminderTimes, kind!.Value);
            if (result == HabitWriteResult.ConsentRequired) return Problems.ConsentRequired();
            if (result == HabitWriteResult.LimitReached) return Problems.HabitLimit();

            return Results.Created($"/api/habits/{habit!.Id}", ToDto(habit, await LoadUserDays(habits, settings, reminders, userId)));
        });

        api.MapPatch("/habits/{id:int}", async (int id, UpdateHabitRequest request, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders, ConsentService consent) =>
        {
            var userId = principal.UserId();
            var habit = await habits.GetHabitAsync(userId, id);
            if (habit is null) return Problems.NotFound();

            var errors = new Dictionary<string, string[]>();
            var name = request.Name is null ? null : HabitValidation.ValidName(request.Name, errors);
            var reminder = request.ReminderTimes is null ? HabitValidation.ParseReminder(request.ReminderTime, errors) : null;
            if (request.ReminderTime is not null && request.ClearReminder == true)
                errors["reminderTime"] = ["Can't be combined with clearReminder."];
            if (request.ReminderTimes is not null && request.ClearReminder == true)
                errors["reminderTimes"] = ["Can't be combined with clearReminder."];
            var frequency = HabitValidation.ParseFrequency(request.Frequency);
            if (request.Frequency is not null && frequency is null) errors["frequency"] = [HabitValidation.FrequencyMessage];
            var newFrequency = request.Frequency is null ? habit.FrequencyType : frequency;
            HabitValidation.ValidateWeeklyTarget(request.WeeklyTarget, frequency ?? habit.FrequencyType, errors);
            var days = HabitValidation.ValidateDays(request.Days, newFrequency, habit.Days, errors);
            var timesPerDay = HabitValidation.ValidateTimesPerDay(
                request.TimesPerDay ?? (newFrequency == FrequencyType.Weekly ? 1 : habit.TimesPerDay), newFrequency, errors);
            var reminderTimes = HabitValidation.ParseReminderTimes(request.ReminderTimes, timesPerDay, errors);
            if (request.Kind is not null && HabitValidation.ParseKind(request.Kind) != habit.Kind) errors["kind"] = ["Can't be changed."];
            if (habit.Kind == HabitKind.LetGo)
                HabitValidation.ValidateLetGo(frequency, request.TimesPerDay, request.WeeklyTarget, request.Days, request.ReminderTimes?.Length, errors);
            var exclusions = await HabitValidation.ParseReminderExclusionsAsync(request.ReminderExclusions, reminders, userId, errors);
            if (errors.Count > 0) return Problems.Validation(errors);
            if (name is not null && name != habit.Name && !await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();

            if (frequency is not null || request.WeeklyTarget is not null || request.Days is not null || request.TimesPerDay is not null)
                await habits.SetScheduleAsync(userId, id, newFrequency!.Value, request.WeeklyTarget ?? habit.WeeklyTarget, days, timesPerDay);
            if ((name is not null || reminder is not null || reminderTimes is not null || request.ClearReminder == true || exclusions is not null)
                && (await habits.EditHabitAsync(userId, id, name, reminder, request.ClearReminder == true, exclusions, reminderTimes)).Result
                    == HabitWriteResult.ConsentRequired)
                return Problems.ConsentRequired();

            return await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapDelete("/habits/{id:int}", async (int id, ClaimsPrincipal principal, HabitService habits) =>
            await habits.DeleteHabitAsync(principal.UserId(), id) ? Results.NoContent() : Problems.NotFound());

        api.MapPost("/habits/{id:int}/pause", async (int id, PauseRequest request, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            var userId = principal.UserId();
            DateOnly? until = null;
            if (request.Until is not null)
            {
                if (!Formats.TryParseDate(request.Until, out var date)) return Problems.Validation("until", Formats.DateMessage);
                if (date < await settings.GetLocalTodayAsync(userId)) return Problems.Validation("until", "Can't be before today.");
                until = date;
            }

            return await habits.PauseHabitAsync(userId, id, until) is null
                ? Problems.NotFound()
                : await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapGet("/habits/{id:int}/days", async (int id, string? from, string? to, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings) =>
        {
            var userId = principal.UserId();
            var (start, end, errors) = Formats.DayRange(from, to, await settings.GetLocalTodayAsync(userId), defaultDays: 13);
            if (errors.Count > 0) return Problems.Validation(errors);
            if ((await habits.GetHabitKindsAsync(userId)).GetValueOrDefault(id) == HabitKind.LetGo)
                return Problems.DayEdit(DayEditResult.LetGoHabit)!;

            var days = await habits.GetHabitDaysAsync(userId, id, start, end);
            return days is null ? Problems.NotFound() : Results.Ok(days.Select(ToDayDto).ToList());
        });

        api.MapPut("/habits/{id:int}/days/{date}", async (int id, string date, DayRequest request, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!Formats.TryParseDate(date, out var day)) errors["date"] = [Formats.DateMessage];
            if (request.Completed is null && request.Count is null) errors["completed"] = ["Send completed or count."];
            if (request.Completed is not null && request.Count is not null) errors["count"] = ["Can't be combined with completed."];
            if (errors.Count > 0) return Problems.Validation(errors);

            var userId = principal.UserId();
            if (request.Count is not { } count)
                return Problems.DayEdit(await habits.CheckInAsync(userId, id, day, request.Completed!.Value))
                    ?? await HabitResult(habits, settings, reminders, userId, id);

            if (await habits.GetHabitAsync(userId, id) is not { } habit) return Problems.NotFound();
            if (count < 0) return Problems.Validation("count", "Can't be negative.");
            return Problems.DayEdit(await habits.SetCountAsync(userId, id, day, Math.Min(count, habit.TimesPerDay)))
                ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapDelete("/habits/{id:int}/days/{date}", async (int id, string date, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            return Problems.DayEdit(await habits.ClearDayAsync(userId, id, day))
                ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapGet("/habits/{id:int}/slips", async (int id, string? from, string? to, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings) =>
        {
            var userId = principal.UserId();
            var (start, end, errors) = Formats.DayRange(from, to, await settings.GetLocalTodayAsync(userId), defaultDays: 13);
            if (errors.Count > 0) return Problems.Validation(errors);

            var slips = await habits.GetSlipsAsync(userId, id, start, end);
            return slips is null ? Problems.NotFound() : Results.Ok(slips.Select(s => new SlipDto(s.Date, s.Count, s.Reasons)).ToList());
        });

        api.MapPost("/habits/{id:int}/slips", async (int id, SlipRequest request, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            var errors = new Dictionary<string, string[]>();
            if (!Formats.TryParseDate(request.Date, out var day)) errors["date"] = [Formats.DateMessage];
            if (request.Reason is not null && !HabitSlip.ReasonIds.Contains(request.Reason))
                errors["reason"] = [$"Must be one of {string.Join(", ", HabitSlip.ReasonIds)}."];
            if (errors.Count > 0) return Problems.Validation(errors);

            var userId = principal.UserId();
            return SlipProblem(await habits.AddSlipAsync(userId, id, day, request.Reason))
                ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapDelete("/habits/{id:int}/slips/{date}", async (int id, string date, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            return SlipProblem(await habits.RemoveSlipsAsync(userId, id, day)) ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapGet("/habits/{id:int}/made-it", async (int id, string? from, string? to, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings) =>
        {
            var userId = principal.UserId();
            var (start, end, errors) = Formats.DayRange(from, to, await settings.GetLocalTodayAsync(userId), defaultDays: 13);
            if (errors.Count > 0) return Problems.Validation(errors);

            var days = await habits.GetMadeItDaysAsync(userId, id, start, end);
            return days is null ? Problems.NotFound() : Results.Ok(days.Select(m => new MadeItDto(m.Date)).ToList());
        });

        api.MapPut("/habits/{id:int}/made-it/{date}", async (int id, string date, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            return MadeItProblem(await habits.MarkMadeItAsync(userId, id, day)) ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        api.MapDelete("/habits/{id:int}/made-it/{date}", async (int id, string date, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ReminderService reminders) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            return MadeItProblem(await habits.RemoveMadeItAsync(userId, id, day)) ?? await HabitResult(habits, settings, reminders, userId, id);
        });

        return api;
    }

    public static RouteGroupBuilder MapHabitTemplates(this RouteGroupBuilder api)
    {
        api.MapGet("/habit-templates", () => HabitTemplates.All
            .Select(t => new TemplateDto(t.Key, t.DisplayName, t.Description, Formats.Time(t.SuggestedReminderTime),
                t.Frequency.ToString().ToLowerInvariant(),
                t.Frequency == HabitTemplates.FrequencyHint.Weekly ? t.WeeklyTarget : null))
            .ToList());
        return api;
    }

    private static IResult? SlipProblem(SlipResult result) => result switch
    {
        SlipResult.Saved => null,
        SlipResult.HabitNotFound => Problems.NotFound(),
        SlipResult.NotLetGo => Problems.Validation("kind", "Only for let-go habits."),
        SlipResult.InFuture => Problems.Validation("date", "Can't be after today."),
        SlipResult.BeforeHabitStart => Problems.Validation("date", "Can't be before the habit's start."),
        SlipResult.ConsentRequired => Problems.ConsentRequired(),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    private static IResult? MadeItProblem(MadeItResult result) => result switch
    {
        MadeItResult.Saved => null,
        MadeItResult.HabitNotFound => Problems.NotFound(),
        MadeItResult.NotLetGo => Problems.Validation("kind", "Only for let-go habits."),
        MadeItResult.InFuture => Problems.Validation("date", "Can't be after today."),
        MadeItResult.BeforeHabitStart => Problems.Validation("date", "Can't be before the habit's start."),
        MadeItResult.Paused => Problems.Validation("date", "Can't be on a paused day."),
        MadeItResult.Slipped => Problems.Validation("date", "That day has a slip noted."),
        MadeItResult.ConsentRequired => Problems.ConsentRequired(),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, null),
    };

    private static async Task<IResult> HabitResult(HabitService habits, UserSettingsService settings, ReminderService reminders, Guid userId, int id)
        => await habits.GetHabitAsync(userId, id) is { } habit
            ? Results.Ok(ToDto(habit, await LoadUserDays(habits, settings, reminders, userId)))
            : Problems.NotFound();

    private static async Task<UserDays> LoadUserDays(HabitService habits, UserSettingsService settings, ReminderService reminders, Guid userId)
    {
        var destinations = await reminders.GetUserWithDestinationsAsync(userId) ?? new User { Id = userId };
        return new(await settings.GetTimeZoneAsync(userId), await settings.GetLocalTodayAsync(userId),
            await settings.GetGraceDaysAsync(userId), await habits.GetLowDaysAsync(userId), destinations, ReminderDestinations.Ids(destinations));
    }

    private static HabitDto ToDto(Habit habit, UserDays user)
    {
        object today, stats;
        if (habit.Kind == HabitKind.LetGo)
        {
            var day = LetGoRules.DayOn(habit, user.Today, user.Tz);
            var l = LetGoRules.StatsOn(habit, user.Today, user.Tz);
            today = new LetGoDayDto(day.Date, Lower(day.State), day.Slips, day.MadeIt);
            stats = new LetGoStatsDto(l.DaysFree, l.LongestStretch, l.FreeDaysThisMonth, l.SlipDaysLast30);
        }
        else
        {
            var s = HabitRules.StatsOn(habit, user.Today, user.GraceDays, user.LowDays, user.Tz);
            today = ToDayDto(HabitRules.DayOn(habit, user.Today, user.Today, user.Tz, user.LowDays));
            stats = new StatsDto(s.Streak, s.GraceUsed, s.BestStreak, s.Last7, s.Week is { } week ? new WeekDto(week.Done, week.Target) : null);
        }
        var targets = ReminderDestinations.Resolve(user.Destinations, habit.ReminderExclusions);

        return new HabitDto(
            habit.Id, habit.Name, Lower(habit.FrequencyType), habit.WeeklyTarget,
            habit.ReminderTime is { } time ? Formats.Time(time) : null,
            habit.PausedFrom, habit.PausedUntil, HabitRules.IsPausedOn(habit, user.Today),
            DateTime.SpecifyKind(habit.CreatedAt, DateTimeKind.Utc), HabitRules.StartDate(habit, user.Tz),
            today, stats,
            ReminderDestinations.LegacyChannels(targets),
            habit.FrequencyType == FrequencyType.Weekdays && habit.Days is { } days ? ScheduleDayNames.ToNames(days) : null,
            habit.TimesPerDay, habit.RoutineId, habit.ReminderTimes.Select(Formats.Time).ToList(),
            habit.ReminderExclusions.Where(user.DestinationIds.Contains).ToList(), targets.Any,
            habit.Kind == HabitKind.LetGo ? "letGo" : "build");
    }

    private static DayDto ToDayDto(HabitDay day) => new(day.Date, Lower(day.State), day.Completed, day.Count);

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
}
