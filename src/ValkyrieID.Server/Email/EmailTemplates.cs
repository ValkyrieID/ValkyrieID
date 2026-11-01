using System.Globalization;

namespace ValkyrieID.Server.Email;

/// <summary>Plain-text emails. Notices contain no links, so they can't be used for phishing look-alikes of the real ones.</summary>
internal static class EmailTemplates
{
    public static EmailMessage PasswordReset(string product, string to, string userName, Uri link, TimeSpan lifetime) => new(
        to,
        $"Reset your {product} password",
        $"""
        Someone asked to reset the password of the {product} account "{userName}".

        To choose a new password, open this link within {Describe(lifetime)}:

        {link}

        Resetting your password signs you out everywhere.

        If you didn't ask for this, you can ignore this email; your password stays the same.
        """);

    public static EmailMessage VerifyEmail(string product, string to, string userName, Uri link, TimeSpan lifetime) => new(
        to,
        $"Verify your email address for {product}",
        $"""
        To confirm that this address belongs to the {product} account "{userName}", open this link within {Describe(lifetime)}:

        {link}

        If you didn't ask for this, you can ignore this email.
        """);

    public static EmailMessage ConfirmEmailChange(string product, string to, string userName, Uri link, TimeSpan lifetime) => new(
        to,
        $"Confirm your new email address for {product}",
        $"""
        Someone asked to change the email address of the {product} account "{userName}" to this address.

        To confirm the change, open this link within {Describe(lifetime)}:

        {link}

        If you didn't ask for this, you can ignore this email; nothing changes.
        """);

    public static EmailMessage EmailInUse(string product, string to) => new(
        to,
        $"Your email address and {product}",
        $"""
        Someone asked to use this email address for a {product} account, but it already belongs to an account.
        Nothing has been changed.

        If this was you, sign in to the account that uses this address instead.
        If it wasn't, you can ignore this email.
        """);

    public static EmailMessage EmailChanged(string product, string to, string userName, string newEmail) => new(
        to,
        $"Your {product} email address was changed",
        $"""
        The email address of the {product} account "{userName}" was changed to {Mask(newEmail)}.
        Emails about the account, including password resets, now go to the new address.

        If you didn't make this change, contact your administrator right away.
        """);

    public static EmailMessage PasswordChanged(string product, string to, string userName, PasswordChange how)
    {
        var what = how switch
        {
            PasswordChange.Reset => "reset with a link sent to this address",
            PasswordChange.Admin => "reset by an administrator",
            _ => "changed",
        };
        return new(
            to,
            $"Your {product} password was changed",
            $"""
            The password of the {product} account "{userName}" was {what}.

            If you didn't make this change, contact your administrator right away.
            """);
    }

    public static EmailMessage Test(string product, string to) => new(
        to,
        $"{product} test email",
        $"This is a test email from {product}. Your SMTP settings work.");

    /// <summary>Hides most of an address, e.g. <c>a***@example.com</c>.</summary>
    public static string Mask(string email)
    {
        var at = email.LastIndexOf('@');
        return at <= 0 ? "***" : string.Concat(email.AsSpan(0, 1), "***", email.AsSpan(at));
    }

    private static string Describe(TimeSpan lifetime) =>
        lifetime.TotalHours >= 1 && lifetime.TotalHours % 1 == 0
            ? lifetime.TotalHours == 1 ? "1 hour" : string.Create(CultureInfo.InvariantCulture, $"{lifetime.TotalHours:0} hours")
            : string.Create(CultureInfo.InvariantCulture, $"{Math.Ceiling(lifetime.TotalMinutes):0} minutes");
}
