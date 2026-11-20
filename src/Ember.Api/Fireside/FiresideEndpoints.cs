using System.Globalization;
using System.Security.Claims;
using Ember.Api.Hosting;
using Ember.Core.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Ember.Api.Fireside;

/// <summary>
/// Fireside: friends by single-use codes and the five fixed warmth messages. Every id in a route is the caller's own link or warmth
/// id; anything else is 404. The code is kept encrypted with Data Protection (<see cref="ProtectorPurpose"/>) so the page can show it
/// again; a code that can't be decrypted shows as none.
/// </summary>
internal static class FiresideEndpoints
{
    public const string ProtectorPurpose = "Ember.Fireside.Code";

    /// <summary>The one answer for an invalid, expired, used or own code.</summary>
    public const string InvalidCodeMessage = "That code didn't work. Ask for a new one.";

    /// <summary>The one answer when either side is full; never says which.</summary>
    public const string FullMessage = "One of you already has 20 friends at the fireside.";

    private const string OffMessage = "Turn on Fireside first.";

    public sealed record CodeDto(string Code, DateTime ExpiresAt);
    public sealed record FiresideDto(bool Enabled, string? Name, bool Notify, DateTime? AskingUntil, CodeDto? Code,
        IReadOnlyList<FiresideFriend> Friends, IReadOnlyList<FiresideWarmth> Warmth, string ConsentVersion, bool ConsentGiven);
    public sealed record AskDto(DateTime AskingUntil);

    public sealed record SettingsRequest(bool? Enabled, string? Name, bool? Notify, string? ConsentVersion);
    public sealed record JoinRequest(string? Code);
    public sealed record WarmthRequest(string? Message);
    public sealed record FriendPatch(bool? Muted, bool? ShareFlame);
    public sealed record AcknowledgeRequest(bool? Rename, bool? Joined);

    public static RouteGroupBuilder MapFireside(this RouteGroupBuilder api)
    {
        var fireside = api.MapGroup("/fireside");

        fireside.MapGet("", async (ClaimsPrincipal principal, FiresideService service, IDataProtectionProvider protection) =>
            await StateAsync(principal.UserId(), service, protection));

        fireside.MapPut("", async (SettingsRequest request, ClaimsPrincipal principal, FiresideService service,
            IDataProtectionProvider protection) =>
        {
            if (request.Enabled is not { } enabled) return Problems.Validation("enabled", "Say whether Fireside is on.");
            string? name = null;
            if (request.Name is not null && (name = FiresideService.ValidName(request.Name)) is null)
                return Problems.Validation("name", NameMessage);

            var userId = principal.UserId();
            return await service.UpdateSettingsAsync(userId, enabled, name, request.Notify, request.ConsentVersion) switch
            {
                FiresideSettingsResult.Saved => await StateAsync(userId, service, protection),
                FiresideSettingsResult.NameRequired => Problems.Validation("name", NameMessage),
                FiresideSettingsResult.ConsentRequired => Problems.FiresideConsentRequired(),
                _ => Problems.NotFound(),
            };
        });

        fireside.MapPost("/code", async (ClaimsPrincipal principal, FiresideService service, IDataProtectionProvider protection) =>
        {
            var protector = protection.CreateProtector(ProtectorPurpose);
            return await service.CreateCodeAsync(principal.UserId(), protector.Protect) is { } code
                ? Results.Ok(new CodeDto(code.Code, code.ExpiresAt))
                : Problems.Conflict(OffMessage);
        });

        fireside.MapDelete("/code", async (ClaimsPrincipal principal, FiresideService service) =>
        {
            await service.DeleteCodeAsync(principal.UserId());
            return Results.NoContent();
        });

        fireside.MapPost("/join", async (JoinRequest request, HttpContext http, ClaimsPrincipal principal, FiresideService service) =>
        {
            var (result, friend, retryAfter) = await service.JoinAsync(principal.UserId(), request.Code);
            switch (result)
            {
                case FiresideJoinResult.Joined when friend is not null:
                    return Results.Ok(friend);
                case FiresideJoinResult.Off:
                    return Problems.Conflict(OffMessage);
                case FiresideJoinResult.Full:
                    return Problems.Conflict(FullMessage);
                case FiresideJoinResult.RateLimited:
                    http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                    return Problems.RateLimited();
                default:
                    return Problems.Validation("code", InvalidCodeMessage);
            }
        });

        fireside.MapPost("/friends/{id:int}/warmth", async (int id, WarmthRequest request, ClaimsPrincipal principal, FiresideService service) =>
            await service.SendWarmthAsync(principal.UserId(), id, request.Message) switch
            {
                FiresideSendResult.Sent => Results.NoContent(),
                FiresideSendResult.InvalidMessage => Problems.Validation("message", "Pick one of the messages."),
                FiresideSendResult.Limit => Problems.Conflict("You already sent them warmth today."),
                _ => Problems.NotFound(),
            });

        fireside.MapPatch("/friends/{id:int}", async (int id, FriendPatch request, ClaimsPrincipal principal, FiresideService service) =>
            await service.UpdateFriendAsync(principal.UserId(), id, request.Muted, request.ShareFlame) is { } friend
                ? Results.Ok(friend)
                : Problems.NotFound());

        fireside.MapPost("/friends/{id:int}/acknowledge", async (int id, AcknowledgeRequest request, ClaimsPrincipal principal,
            FiresideService service) =>
            await service.AcknowledgeAsync(principal.UserId(), id, request.Rename == true, request.Joined == true) is { } friend
                ? Results.Ok(friend)
                : Problems.NotFound());

        fireside.MapDelete("/friends/{id:int}", async (int id, bool? revokeCode, ClaimsPrincipal principal, FiresideService service) =>
            await service.RemoveFriendAsync(principal.UserId(), id, revokeCode == true) ? Results.NoContent() : Problems.NotFound());

        fireside.MapPost("/warmth/{id:int}/dismiss", async (int id, ClaimsPrincipal principal, FiresideService service) =>
            await service.DismissWarmthAsync(principal.UserId(), id) ? Results.NoContent() : Problems.NotFound());

        fireside.MapPut("/ask", async (ClaimsPrincipal principal, FiresideService service) =>
            await service.AskAsync(principal.UserId()) is { } until ? Results.Ok(new AskDto(until)) : Problems.Conflict(OffMessage));

        fireside.MapDelete("/ask", async (ClaimsPrincipal principal, FiresideService service) =>
        {
            await service.StopAskingAsync(principal.UserId());
            return Results.NoContent();
        });

        return api;
    }

    private const string NameMessage = "Choose a name of 1 to 32 characters.";

    private static async Task<IResult> StateAsync(Guid userId, FiresideService service, IDataProtectionProvider protection)
    {
        if (await service.GetStateAsync(userId) is not { } state) return Problems.NotFound();
        CodeDto? code = null;
        if (state.CodeProtected is { } stored && state.CodeExpiresAt is { } expiresAt)
        {
            try
            {
                code = new CodeDto(protection.CreateProtector(ProtectorPurpose).Unprotect(stored), expiresAt);
            }
            catch (Exception)
            {
                code = null;
            }
        }

        return Results.Ok(new FiresideDto(state.Enabled, state.Name, state.Notify, state.AskingUntil, code, state.Friends, state.Warmth,
            FiresideService.CurrentConsentVersion, state.ConsentGiven));
    }
}
