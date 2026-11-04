using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Shown after sign-in while the user's email address isn't verified (<see cref="EmailOptions.VerificationAtSignIn"/>).
/// In prompt mode the user can continue; in require mode they return to the application only once verified.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class VerifyRequiredModel(AccountService account, EmailAccountService emailAccounts, EmailVerificationGate gate)
    : AccountPageModel(account)
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public EmailVerificationAtSignIn Mode { get; private set; }

    public bool VerificationSent { get; private set; }

    public string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user || CurrentSessionId is not { } sessionId)
        {
            return Challenge();
        }

        Mode = gate.Mode;
        if (Mode == EmailVerificationAtSignIn.Off || user is { Email: not null, EmailVerified: true })
        {
            return LocalRedirect(SafeReturnUrl);
        }

        if (Mode == EmailVerificationAtSignIn.Prompt)
        {
            // Offered once per sign-in: continuing returns to the application without asking again.
            EmailVerificationGate.MarkPrompted(HttpContext, sessionId);
        }

        Response.Headers.CacheControl = "no-store";
        return Page();
    }

    public async Task<IActionResult> OnPostSendAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        Mode = gate.Mode;
        if (Mode == EmailVerificationAtSignIn.Off || !emailAccounts.Enabled)
        {
            return LocalRedirect(SafeReturnUrl);
        }

        await emailAccounts.SendVerificationAsync(user, Actor, cancellationToken);
        VerificationSent = true;
        Response.Headers.CacheControl = "no-store";
        return Page();
    }
}
