using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Changes the password. The current session stays signed in with a re-issued cookie; other sessions and all
/// refresh tokens are revoked.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class PasswordModel(AccountService account, SessionManager sessionManager) : AccountPageModel(account)
{
    [BindProperty]
    public string? CurrentPassword { get; set; }

    [BindProperty]
    public string? NewPassword { get; set; }

    [BindProperty]
    public string? ConfirmPassword { get; set; }

    public bool Changed { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is null ? Challenge() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        var (current, next, confirm) = (CurrentPassword, NewPassword, ConfirmPassword);
        (CurrentPassword, NewPassword, ConfirmPassword) = (null, null, null);

        Errors = await Account.ChangePasswordAsync(user, current, next, confirm, CurrentSessionId, Actor, cancellationToken);
        if (Errors.Count > 0)
        {
            return Page();
        }

        await sessionManager.RefreshCookieAsync(HttpContext, user, cancellationToken);
        Changed = true;
        return Page();
    }
}
