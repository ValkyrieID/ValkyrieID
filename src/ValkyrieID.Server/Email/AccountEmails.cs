using Microsoft.Extensions.Options;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Email;

internal enum PasswordChange
{
    Changed,
    Reset,
    Admin,
}

/// <summary>
/// Builds and queues the account emails. Links are built from the configured issuer URL, never from the request,
/// so a forged <c>Host</c> header can't redirect a reset link to an attacker's site. Does nothing when SMTP isn't
/// configured.
/// </summary>
internal sealed class AccountEmails(
    EmailQueue queue,
    IOptionsMonitor<SmtpOptions> smtp,
    IOptions<IssuerOptions> issuer,
    IOptionsMonitor<BrandingOptions> branding)
{
    public const string ResetPasswordPath = "/account/reset-password";
    public const string VerifyEmailPath = "/account/verify-email";
    public const string ConfirmEmailChangePath = "/account/confirm-email";

    public bool Enabled => smtp.CurrentValue.Enabled;

    private string Product => branding.CurrentValue.ProductName;

    public void PasswordReset(User user, string token, TimeSpan lifetime) =>
        Add("password_reset", user.Id, EmailTemplates.PasswordReset(Product, user.Email!, user.UserName, Link(ResetPasswordPath, token), lifetime));

    public void VerifyEmail(User user, string token, TimeSpan lifetime) =>
        Add("verify_email", user.Id, EmailTemplates.VerifyEmail(Product, user.Email!, user.UserName, Link(VerifyEmailPath, token), lifetime));

    public void ConfirmEmailChange(User user, string newEmail, string token, TimeSpan lifetime) =>
        Add("email_change", user.Id, EmailTemplates.ConfirmEmailChange(Product, newEmail, user.UserName, Link(ConfirmEmailChangePath, token), lifetime));

    public void EmailInUse(Guid userId, string email) =>
        Add("email_in_use", userId, EmailTemplates.EmailInUse(Product, email));

    public void EmailChanged(User user, string oldEmail) =>
        Add("email_changed", user.Id, EmailTemplates.EmailChanged(Product, oldEmail, user.UserName, user.Email!));

    public void PasswordChanged(User user, PasswordChange how)
    {
        if (user.Email is not null)
        {
            Add("password_changed", user.Id, EmailTemplates.PasswordChanged(Product, user.Email, user.UserName, how));
        }
    }

    /// <summary>Wakes the email worker; call after saving.</summary>
    public void Wake() => queue.Wake();

    private void Add(string kind, Guid userId, EmailMessage message)
    {
        if (Enabled)
        {
            queue.Add(kind, userId, message);
        }
    }

    private Uri Link(string path, string token) => new(issuer.Value.Endpoint(path) + "?token=" + token);
}
