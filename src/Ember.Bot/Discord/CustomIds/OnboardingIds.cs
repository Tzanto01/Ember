namespace Ember.Bot.Discord.CustomIds;

/// <summary>
/// The timezone steps, carrying the habit id on to the reminder prompt: <c>onboard:timezone:{habitId}</c> (button), then the
/// selects <c>onboard:region:{habitId}</c> and <c>onboard:tz:{habitId}</c>. The string overloads pass on the id as the clicked button carried it.
/// </summary>
internal static class OnboardingIds
{
    public const string TimezonePattern = "onboard:timezone:*";
    public const string RegionPattern = "onboard:region:*";
    public const string TimezoneSelectPattern = "onboard:tz:*";

    public static string Timezone(int habitId) => $"onboard:timezone:{habitId}";

    public static string Timezone(string habitId) => $"onboard:timezone:{habitId}";

    public static string Region(string habitId) => $"onboard:region:{habitId}";

    public static string TimezoneSelect(string habitId) => $"onboard:tz:{habitId}";
}
