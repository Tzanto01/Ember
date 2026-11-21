using Ember.Core.Localization;
using Ember.Core.Models;

namespace Ember.Api.Account;

/// <summary>The field checks of the account settings, shared by <c>PATCH /me</c> and the import.</summary>
internal static class SettingsValidation
{
    internal static void ValidateGraceAndTheme(int? graceDaysPerWeek, string? theme, Dictionary<string, string[]> errors)
    {
        if (graceDaysPerWeek is < 0 or > 3)
            errors["graceDaysPerWeek"] = ["Must be between 0 and 3."];
        if (theme is not null && !User.Themes.Contains(theme))
            errors["theme"] = ["Unknown theme."];
    }

    internal static void ValidateSecondReminder(int? minutes, Dictionary<string, string[]> errors)
    {
        if (minutes is { } m && !User.SecondReminderChoices.Contains(m))
            errors["secondReminderMinutes"] = ["Must be 0, 15, 30, 60 or 120."];
    }

    internal static void ValidateLanguage(string? language, Dictionary<string, string[]> errors)
    {
        if (language is not null && !Languages.All.Contains(language))
            errors["language"] = ["Must be en or de."];
    }
}
