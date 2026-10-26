using System.Net.Mail;
using System.Text;

namespace ValkyrieID.Core.Users;

public static class UserNames
{
    public const int MaxUserNameLength = 64;
    public const int MaxEmailLength = 254;

    /// <summary>
    /// Usernames are 1-64 letters, digits, <c>.</c>, <c>_</c> or <c>-</c>. They can't contain <c>@</c>, so a login
    /// identifier containing <c>@</c> is always an email address.
    /// </summary>
    public static bool IsValidUserName(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxUserNameLength
        && value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-');

    public static bool IsValidEmail(string? value) =>
        !string.IsNullOrEmpty(value)
        && value.Length <= MaxEmailLength
        && MailAddress.TryCreate(value, out var address)
        && address.Address == value
        && address.DisplayName.Length == 0;

    public static bool IsEmail(string identifier) => identifier.Contains('@', StringComparison.Ordinal);

    /// <summary>Case-insensitive lookup key for usernames and email addresses.</summary>
    public static string Normalize(string value) => value.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
}
