using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Ember.Api.Auth;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Ember.Api.Account;

/// <summary>The signed-in account: settings, consent, export and deletion.</summary>
internal static class MeEndpoints
{
    public sealed record DiscordDto(bool Linked, string? Id);
    /// <summary>
    /// <see cref="WithdrawnAt"/> is when the consent was withdrawn or declined (null otherwise); <see cref="DeletesAt"/> is when the health
    /// data is deleted, set only while withdrawn with health data left.
    /// </summary>
    public sealed record ConsentDto(bool Given, string? PolicyVersion, string CurrentPolicyVersion, DateTime? WithdrawnAt, DateTime? DeletesAt);
    public sealed record DmRemindersDto(bool Paused);

    public sealed record MeDto(
        Guid Id, string? Email, bool EmailConfirmed, bool HasPassword, DiscordDto Discord,
        string Timezone, bool TimezoneSet, DateOnly LocalToday, int GraceDaysPerWeek, string? Theme, int SecondReminderMinutes,
        bool ShowStreaks, string Language, bool CalmMode, ConsentDto Consent, DmRemindersDto DmReminders, JournalState Journal,
        string[] TipsSeen, string[]? TodayOrder, bool OfferHabitNote);

    public sealed record MePatch(string? Timezone, int? GraceDaysPerWeek, string? Theme, int? SecondReminderMinutes, bool? ShowStreaks,
        string? Language, bool? CalmMode, bool? OfferHabitNote);
    public sealed record TodayOrderRequest(List<string?>? Order);
    public sealed record TodayOrderDto(List<string>? Order);
    public sealed record ConsentRequest(string? PolicyVersion, bool? AgeConfirmed);
    public sealed record DeleteRequest(string? Password, string? Confirm);
    public sealed record WidgetKeyDto(string Key);

    public static RouteGroupBuilder MapMe(this RouteGroupBuilder api)
    {
        api.MapGet("/me", async (ClaimsPrincipal principal, UserManager<User> users, UserSettingsService settings, ConsentService consent,
            JournalService journal, TimeProvider time) =>
            await LoadMe(principal, users, settings, consent, journal, time) is { } me ? Results.Ok(me) : Problems.Unauthenticated());

        api.MapPatch("/me", async (JsonElement body, ClaimsPrincipal principal, UserManager<User> users, UserSettingsService settings,
            ReminderService reminders, ConsentService consent, JournalService journal, TimeProvider time) =>
        {
            MePatch? patch;
            try
            {
                patch = body.Deserialize<MePatch>(JsonSerializerOptions.Web);
            }
            catch (JsonException e)
            {
                return Problems.Validation(e.Path is { Length: > 2 } path ? path[2..] : "body", "Has the wrong type or format.");
            }
            if (patch is null) return Problems.Validation("body", "Must be a JSON object.");

            var errors = new Dictionary<string, string[]>();
            TimeZoneInfo? zone = null;
            if (patch.Timezone is not null && (!TimeZoneInfo.TryFindSystemTimeZoneById(patch.Timezone, out zone) || !zone.HasIanaId))
                errors["timezone"] = ["Unknown IANA timezone."];
            SettingsValidation.ValidateGraceAndTheme(patch.GraceDaysPerWeek, patch.Theme, errors);
            SettingsValidation.ValidateSecondReminder(patch.SecondReminderMinutes, errors);
            SettingsValidation.ValidateLanguage(patch.Language, errors);
            if (errors.Count > 0) return Problems.Validation(errors);

            var userId = principal.UserId();
            if (patch.SecondReminderMinutes is { } second) await reminders.SetSecondReminderMinutesAsync(userId, second);
            await settings.UpdateAsync(userId, new UserSettingsService.SettingsUpdate(patch.GraceDaysPerWeek, patch.Theme,
                patch.ShowStreaks, patch.Language, patch.CalmMode, patch.OfferHabitNote, zone?.Id));
            if (zone is not null) await reminders.RescheduleRemindersAsync(userId);

            return await LoadMe(principal, users, settings, consent, journal, time) is { } me ? Results.Ok(me) : Problems.Unauthenticated();
        });

        api.MapPut("/me/today-order", async (TodayOrderRequest request, ClaimsPrincipal principal, UserSettingsService settings) =>
        {
            if (request.Order?.Count > User.MaxTodayOrderEntries)
                return Problems.Validation("order", $"At most {User.MaxTodayOrderEntries} entries.");

            return Results.Ok(new TodayOrderDto(await settings.SetTodayOrderAsync(principal.UserId(), request.Order)));
        });

        api.MapPost("/me/consent", async (ConsentRequest request, ClaimsPrincipal principal, ConsentService consent) =>
        {
            if (request.AgeConfirmed != true)
                return Problems.Validation("ageConfirmed", "Confirm that you are at least 16.");
            if (request.PolicyVersion != ConsentService.CurrentConsentPolicyVersion)
                return Problems.Validation("policyVersion", "Not the current policy version.");

            await consent.GiveHealthDataConsentAsync(principal.UserId());
            return Results.NoContent();
        });

        api.MapPost("/me/consent/withdraw", async (ClaimsPrincipal principal, ConsentService consent) =>
        {
            await consent.WithdrawHealthDataConsentAsync(principal.UserId());
            return Results.NoContent();
        });

        api.MapPost("/me/consent/decline", async (ClaimsPrincipal principal, ConsentService consent) =>
        {
            await consent.DeclineConsentPromptAsync(principal.UserId());
            return Results.NoContent();
        });

        api.MapPut("/me/tips/{id}", async (string id, ClaimsPrincipal principal, UserSettingsService settings) =>
        {
            if (!User.TipIds.Contains(id)) return Problems.Validation("id", "Unknown tip.");

            await settings.MarkTipSeenAsync(principal.UserId(), id);
            return Results.NoContent();
        });

        api.MapPost("/me/dm-reminders/resume", async (ClaimsPrincipal principal, ReminderService reminders) =>
        {
            await reminders.ResumeDmRemindersAsync(principal.UserId());
            return Results.NoContent();
        });

        api.MapGet("/me/export", async (string? format, ClaimsPrincipal principal, AccountService accounts, TimeProvider time) =>
        {
            if (format is not (null or "json" or "csv"))
                return Problems.Validation("format", "Must be json or csv.");

            var user = await accounts.GetUserForExportAsync(principal.UserId(), withHabitNotes: true);
            if (user is null) return Problems.NotFound();

            var (content, contentType, extension) = format == "csv"
                ? (ExportBuilder.BuildCsv(user), "text/csv", "csv")
                : (ExportBuilder.BuildJson(user), "application/json", "json");
            return Results.File(Encoding.UTF8.GetBytes(content), contentType,
                $"ember-export-{time.GetUtcNow():yyyy-MM-dd}.{extension}");
        });

        api.MapPost("/me/widget-key", async (HttpContext http, AuthSessions sessions) =>
        {
            if (!(await http.AuthenticateAsync(IdentityConstants.BearerScheme)).Succeeded || AuthSessions.IdOf(http.User) is not { } session)
                return Problems.NotFound();

            return await sessions.IssueWidgetKeyAsync(session) is { } key ? Results.Ok(new WidgetKeyDto(key)) : Problems.Unauthenticated();
        });

        api.MapPost("/me/delete", async (DeleteRequest request, ClaimsPrincipal principal, UserManager<User> users,
            SignInManager<User> signIn, AccountService accounts, AuthSessions sessions) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Problems.Unauthenticated();

            if (await users.HasPasswordAsync(user))
            {
                if (!(await signIn.CheckPasswordSignInAsync(user, request.Password ?? "", lockoutOnFailure: true)).Succeeded)
                    return Problems.Validation("password", "The password is not correct.");
            }
            else if (request.Confirm != "DELETE")
            {
                return Problems.Validation("confirm", "Type DELETE to confirm.");
            }

            await sessions.EndAllAsync(user.Id);
            await accounts.DeleteUserDataAsync(user.Id);
            await signIn.SignOutAsync();
            return Results.NoContent();
        });

        return api;
    }

    private static async Task<MeDto?> LoadMe(ClaimsPrincipal principal, UserManager<User> users, UserSettingsService settings,
        ConsentService consent, JournalService journal, TimeProvider time)
    {
        var user = await users.GetUserAsync(principal);
        if (user is null) return null;

        var discordId = (await users.GetLoginsAsync(user)).FirstOrDefault(l => l.LoginProvider == DiscordLogin.Provider)?.ProviderKey;
        return new MeDto(
            user.Id, user.Email, user.EmailConfirmed, await users.HasPasswordAsync(user),
            new DiscordDto(discordId is not null, discordId),
            user.Timezone, await settings.HasTimezoneSetAsync(user.Id), TimeZones.LocalToday(user.Timezone, time.GetUtcNow()),
            user.GraceDaysPerWeek, user.Theme, user.SecondReminderMinutes, user.ShowStreaks, user.Language, user.CalmMode,
            new ConsentDto(await consent.HasHealthDataConsentAsync(user.Id), user.ConsentPolicyVersion, ConsentService.CurrentConsentPolicyVersion,
                user.ConsentWithdrawnAt,
                user.ConsentWithdrawnAt is { } withdrawn && await consent.HasHealthDataAsync(user.Id) ? ConsentService.DeletionDueUtc(withdrawn, user.DeletionReminderSentAt) : null),
            new DmRemindersDto(user.DmRemindersPausedAt is not null),
            await journal.GetJournalStateAsync(user.Id),
            [.. user.TipsSeen], user.TodayOrder?.ToArray(), user.OfferHabitNote);
    }
}
