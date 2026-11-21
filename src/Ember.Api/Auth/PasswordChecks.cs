using Ember.Api.Hosting;
using Ember.Core.Models;
using Microsoft.AspNetCore.Identity;

namespace Ember.Api.Auth;

/// <summary>The account's current-password and authenticator-code checks, shared by several endpoints.</summary>
internal static class PasswordChecks
{
    /// <summary>A 400 on <c>password</c> when the account has a login password and <paramref name="password"/> isn't it (counts toward lockout), else null.</summary>
    internal static async Task<IResult?> PasswordProblemAsync(User user, string? password, UserManager<User> users, SignInManager<User> signIn)
        => await users.HasPasswordAsync(user)
            && !(await signIn.CheckPasswordSignInAsync(user, password ?? "", lockoutOnFailure: true)).Succeeded
                ? Problems.Validation("password", "The password is not correct.")
                : null;

    /// <summary>
    /// Whether <paramref name="code"/> is the account's current authenticator code. A locked-out account never passes; a wrong
    /// code counts towards lockout, a right one resets the count.
    /// </summary>
    internal static async Task<bool> CheckAuthenticatorCodeAsync(UserManager<User> users, User user, string? code)
    {
        if (await users.IsLockedOutAsync(user)) return false;
        if (code is { Length: > 0 } && await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            await users.ResetAccessFailedCountAsync(user);
            return true;
        }

        await users.AccessFailedAsync(user);
        return false;
    }
}
