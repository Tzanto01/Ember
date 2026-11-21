using System.Security.Claims;
using System.Text.Json;
using Ember.Api.Auth;
using Ember.Api.Hosting;
using Ember.Core.Models;
using Ember.Core.Rules;
using Ember.Core.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Ember.Api.Journal;

/// <summary>The journal for the day, own moods and the private-journal key (contract "Journal for the day", "Private journal").</summary>
internal static class JournalEndpoints
{
    public sealed record EncryptedDto(int? V, string? Iv, string? Ct);
    public sealed record DayNoteRequest(string? Text, string? Mood, EncryptedDto? Encrypted);
    public sealed record HabitNoteRequest(string? Text, EncryptedDto? Encrypted);
    public sealed record MoodRequest(string? Label, string? Emoji, EncryptedDto? Encrypted, bool? Archived);
    public sealed record PassphraseDto(string? Salt, int? Iterations, string? Iv, string? Wrapped);
    public sealed record RecoveryDto(string? Salt, string? Iv, string? Wrapped);
    public sealed record JournalKeyRequest(PassphraseDto? Passphrase, RecoveryDto? Recovery, string? Password, int? IfVersion);
    public sealed record JournalKeyDto(int Version, bool TurningOff, PassphraseDto Passphrase, RecoveryDto Recovery);
    public sealed record PasswordRequest(string? Password);
    public sealed record DeleteJournalKeyRequest(string? Password, bool? DiscardEncrypted);

    public static RouteGroupBuilder MapJournal(this RouteGroupBuilder api)
    {
        api.MapGet("/day-notes", async (string? from, string? to, ClaimsPrincipal principal, UserSettingsService settings,
            JournalService journal) =>
        {
            var userId = principal.UserId();
            var today = await settings.GetLocalTodayAsync(userId);
            var (start, end, errors) = Formats.DayRange(from, to, today, defaultDays: Formats.MaxRangeDays - 1, allowFutureTo: true,
                limitMessage: Formats.JournalRangeLimitMessage);
            if (errors.Count > 0) return Problems.Validation(errors);

            return Results.Ok((await journal.GetDayNotesAsync(userId, start, end)).Select(ToDto).ToList());
        });

        api.MapPut("/day-notes/{date}", async (string date, DayNoteRequest request, ClaimsPrincipal principal, HabitService habits,
            UserSettingsService settings, ConsentService consent, JournalService journal) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();
            var today = await settings.GetLocalTodayAsync(userId);
            if (day > today) return Problems.DayEdit(DayEditResult.InFuture)!;
            if (day != today && (await journal.GetDayNotesAsync(userId, day, day)).Count == 0
                && (await habits.GetEarliestHabitStartAsync(userId) is not { } start || day < start))
                return Problems.DayEdit(DayEditResult.BeforeHabitStart)!;

            var errors = new Dictionary<string, string[]>();
            var cipher = JournalValidation.WriteForm(request.Encrypted, request.Text is not null || request.Mood is not null, true,
                await journal.GetJournalKeyAsync(userId), errors);
            string? text = null, mood = null;
            if (request.Encrypted is null)
            {
                text = JournalValidation.ValidText(request.Text, errors);
                mood = request.Mood;
                if (mood is not null && !await IsAllowedMoodAsync(userId, day, mood, journal))
                    errors["mood"] = ["Must be low, meh, okay, good, great or one of your own active moods."];
            }
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, note) = await journal.SetDayNoteAsync(userId, day, text, mood, cipher);
            if (result == NoteWriteResult.ConsentRequired) return Problems.ConsentRequired();
            return Results.Ok(note is null ? new { date = day, text = (string?)null, mood = (string?)null } : ToDto(note));
        });

        api.MapDelete("/day-notes/{date}", async (string date, ClaimsPrincipal principal, JournalService journal) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            await journal.DeleteDayNoteAsync(principal.UserId(), day);
            return Results.NoContent();
        });

        api.MapGet("/habit-notes", async (string? from, string? to, int? habitId, ClaimsPrincipal principal, UserSettingsService settings,
            JournalService journal) =>
        {
            var userId = principal.UserId();
            var today = await settings.GetLocalTodayAsync(userId);
            var (start, end, errors) = Formats.DayRange(from, to, today, defaultDays: Formats.MaxRangeDays - 1, allowFutureTo: true,
                limitMessage: Formats.JournalRangeLimitMessage);
            if (errors.Count > 0) return Problems.Validation(errors);

            return Results.Ok((await journal.GetHabitNotesAsync(userId, start, end, habitId)).Select(ToDto).ToList());
        });

        api.MapPut("/habits/{id:int}/notes/{date}", async (int id, string date, HabitNoteRequest request, ClaimsPrincipal principal,
            HabitService habits, UserSettingsService settings, ConsentService consent, JournalService journal) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            var userId = principal.UserId();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();
            if (await habits.GetHabitAsync(userId, id) is not { } habit) return Problems.NotFound();
            var today = await settings.GetLocalTodayAsync(userId);
            if (day > today) return Problems.DayEdit(DayEditResult.InFuture)!;
            if (day != today && day < HabitRules.StartDate(habit, await settings.GetTimeZoneAsync(userId))
                && (await journal.GetHabitNotesAsync(userId, day, day, id)).Count == 0)
                return Problems.DayEdit(DayEditResult.BeforeHabitStart)!;

            var errors = new Dictionary<string, string[]>();
            var cipher = JournalValidation.WriteForm(request.Encrypted, request.Text is not null, true, await journal.GetJournalKeyAsync(userId),
                errors, JournalValidation.MaxHabitNoteCipherTextLength);
            string? text = null;
            if (request.Encrypted is null)
            {
                text = JournalValidation.ValidText(request.Text, errors, JournalValidation.MaxHabitNoteLength);
                if (text is null && !errors.ContainsKey("text")) errors["text"] = ["Set text or encrypted. Delete the note to clear it."];
            }
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, note) = await journal.SetHabitNoteAsync(userId, id, day, text, cipher);
            return result switch
            {
                NoteWriteResult.Saved => Results.Ok(ToDto(note!)),
                NoteWriteResult.ConsentRequired => Problems.ConsentRequired(),
                _ => Problems.NotFound(),
            };
        });

        api.MapDelete("/habits/{id:int}/notes/{date}", async (int id, string date, ClaimsPrincipal principal, JournalService journal) =>
        {
            if (!Formats.TryParseDate(date, out var day)) return Problems.Validation("date", Formats.DateMessage);

            return await journal.DeleteHabitNoteAsync(principal.UserId(), id, day) ? Results.NoContent() : Problems.NotFound();
        });

        api.MapGet("/moods", async (ClaimsPrincipal principal, JournalService journal) =>
            (await journal.GetMoodsAsync(principal.UserId())).Select(ToDto).ToList());

        api.MapPost("/moods", async (MoodRequest request, ClaimsPrincipal principal, ConsentService consent, JournalService journal) =>
        {
            var userId = principal.UserId();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();

            var errors = new Dictionary<string, string[]>();
            var cipher = JournalValidation.WriteForm(request.Encrypted, request.Label is not null || request.Emoji is not null, true,
                await journal.GetJournalKeyAsync(userId), errors);
            string? label = null, emoji = null;
            if (request.Encrypted is null)
            {
                label = JournalValidation.ValidLabel(request.Label, errors);
                emoji = JournalValidation.ValidEmoji(request.Emoji, errors);
            }
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, mood) = await journal.AddMoodAsync(userId, label, emoji, cipher, request.Archived == true);
            return result switch
            {
                MoodWriteResult.Saved => Results.Created($"/api/moods/{mood!.Id}", ToDto(mood)),
                MoodWriteResult.ConsentRequired => Problems.ConsentRequired(),
                _ => MoodLimit(),
            };
        });

        api.MapPatch("/moods/{id:int}", async (int id, JsonElement body, ClaimsPrincipal principal, ConsentService consent,
            JournalService journal) =>
        {
            MoodRequest? request;
            try
            {
                request = body.Deserialize<MoodRequest>(JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                request = null;
            }
            if (request is null) return Problems.Validation("body", "Must be a JSON object with label, emoji, archived or encrypted.");
            var emojiGiven = body.EnumerateObject().Any(p => string.Equals(p.Name, "emoji", StringComparison.OrdinalIgnoreCase));

            var userId = principal.UserId();
            if (!await consent.HasHealthDataConsentAsync(userId)) return Problems.ConsentRequired();
            if (await journal.GetMoodAsync(userId, id) is not { } existing) return Problems.NotFound();

            var errors = new Dictionary<string, string[]>();
            var plaintextGiven = request.Label is not null || emojiGiven;
            var cipher = JournalValidation.WriteForm(request.Encrypted, plaintextGiven, plaintextGiven, await journal.GetJournalKeyAsync(userId),
                errors);
            string? label = null, emoji = null;
            if (request.Encrypted is null && plaintextGiven)
            {
                label = request.Label is null ? existing.Label : JournalValidation.ValidLabel(request.Label, errors);
                if (label is null && !errors.ContainsKey("label")) errors["label"] = ["Required while the mood is stored encrypted."];
                emoji = emojiGiven ? JournalValidation.ValidEmoji(request.Emoji, errors) : existing.Emoji;
            }
            if (errors.Count > 0) return Problems.Validation(errors);

            var (result, mood) = await journal.EditMoodAsync(userId, id, label, emoji, cipher, request.Archived);
            return result switch
            {
                MoodWriteResult.Saved => Results.Ok(ToDto(mood!)),
                MoodWriteResult.LimitReached => MoodLimit(),
                MoodWriteResult.ConsentRequired => Problems.ConsentRequired(),
                _ => Problems.NotFound(),
            };
        });

        api.MapGet("/me/journal-key", async (ClaimsPrincipal principal, JournalService journal) =>
            await journal.GetJournalKeyAsync(principal.UserId()) is { } key ? Results.Ok(ToDto(key)) : Problems.NotFound());

        api.MapPut("/me/journal-key", async (JournalKeyRequest request, ClaimsPrincipal principal, UserManager<User> users,
            SignInManager<User> signIn, JournalService journal) =>
        {
            var errors = new Dictionary<string, string[]>();
            var material = JournalValidation.ValidKey(request, errors);
            if (errors.Count > 0) return Problems.Validation(errors);

            var user = await users.GetUserAsync(principal);
            if (user is null) return Problems.Unauthenticated();
            if (await PasswordChecks.PasswordProblemAsync(user, request.Password, users, signIn) is { } problem) return problem;

            return await journal.PutJournalKeyAsync(user.Id, material!, request.IfVersion) is { } key
                ? Results.Ok(ToDto(key))
                : Problems.Conflict("The journal key was changed meanwhile. Reload and try again.");
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        api.MapPost("/me/journal-key/turn-off", async (PasswordRequest? request, ClaimsPrincipal principal, UserManager<User> users,
            SignInManager<User> signIn, JournalService journal) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Problems.Unauthenticated();
            if (await PasswordChecks.PasswordProblemAsync(user, request?.Password, users, signIn) is { } problem) return problem;

            return await journal.TurnOffJournalAsync(user.Id) is { } key ? Results.Ok(ToDto(key)) : Problems.NotFound();
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        api.MapDelete("/me/journal-key", async ([FromBody] DeleteJournalKeyRequest? request, ClaimsPrincipal principal,
            UserManager<User> users, SignInManager<User> signIn, JournalService journal) =>
        {
            var user = await users.GetUserAsync(principal);
            if (user is null) return Problems.Unauthenticated();
            if (await PasswordChecks.PasswordProblemAsync(user, request?.Password, users, signIn) is { } problem) return problem;

            return await journal.DeleteJournalKeyAsync(user.Id, request?.DiscardEncrypted == true)
                ? Results.NoContent()
                : Problems.Conflict("Some journal entries or moods are still encrypted. Finish turning the private journal off first.");
        }).RequireRateLimiting(RateLimits.AuthPolicy);

        return api;
    }

    private static IResult MoodLimit()
        => Problems.Conflict($"You already have {JournalService.MaxActiveMoodsPerUser} active moods. Archive one to add another.");

    private static async Task<bool> IsAllowedMoodAsync(Guid userId, DateOnly day, string mood, JournalService journal)
    {
        if (JournalValidation.BuiltInMoods.Contains(mood)) return true;
        if (!JournalValidation.TryParseCustomMood(mood, out var id)) return false;
        var stored = await journal.GetDayNoteAsync(userId, day);
        if (stored?.Mood == mood) return true;
        return await journal.GetMoodAsync(userId, id) is { CipherVersion: null } own && (!own.Archived || stored?.CipherVersion is not null);
    }

    private static object ToDto(DayNote note) => note.CipherVersion is { } version
        ? new { date = note.Date, encrypted = new EncryptedDto(version, note.CipherIv, note.CipherText) }
        : new { date = note.Date, text = note.Text, mood = note.Mood };

    private static object ToDto(HabitNote note) => note.CipherVersion is { } version
        ? new { habitId = note.HabitId, date = note.Date, encrypted = new EncryptedDto(version, note.CipherIv, note.CipherText) }
        : new { habitId = note.HabitId, date = note.Date, text = note.Text };

    private static object ToDto(Mood mood) => mood.CipherVersion is { } version
        ? new { id = mood.Id, encrypted = new EncryptedDto(version, mood.CipherIv, mood.CipherText), archived = mood.Archived }
        : new { id = mood.Id, label = mood.Label, emoji = mood.Emoji, archived = mood.Archived };

    private static JournalKeyDto ToDto(JournalKey key) => new(
        key.Version, key.TurningOff,
        new PassphraseDto(key.PassphraseSalt, key.PassphraseIterations, key.PassphraseIv, key.PassphraseWrapped),
        new RecoveryDto(key.RecoverySalt, key.RecoveryIv, key.RecoveryWrapped));
}
