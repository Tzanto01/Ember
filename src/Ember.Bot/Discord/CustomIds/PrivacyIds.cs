namespace Ember.Bot.CustomIds;

/// <summary>Deleting the account and withdrawing consent, bound to the owner: <c>privacy:{delete|withdraw}:{step}:{discordUserId}</c>.</summary>
internal static class PrivacyIds
{
    public const string DeleteStartPattern = "privacy:delete:start:*";
    public const string DeleteConfirmPattern = "privacy:delete:confirm:*";
    public const string DeleteCancelPattern = "privacy:delete:cancel:*";
    public const string WithdrawConfirmPattern = "privacy:withdraw:confirm:*";
    public const string WithdrawCancelPattern = "privacy:withdraw:cancel:*";

    public static string DeleteStart(ulong ownerId) => $"privacy:delete:start:{ownerId}";

    public static string DeleteConfirm(ulong ownerId) => $"privacy:delete:confirm:{ownerId}";

    public static string DeleteCancel(ulong ownerId) => $"privacy:delete:cancel:{ownerId}";

    public static string WithdrawConfirm(ulong ownerId) => $"privacy:withdraw:confirm:{ownerId}";

    public static string WithdrawCancel(ulong ownerId) => $"privacy:withdraw:cancel:{ownerId}";
}
