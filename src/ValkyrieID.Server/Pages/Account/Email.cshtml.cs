using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Shows the email address, sends verification links, and starts email changes. A change needs the current
/// password and takes effect only when the link sent to the new address is opened.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class EmailModel(AccountService account, EmailAccountService emailAccounts) : AccountPageModel(account)
{
    [BindProperty]
    public string? NewEmail { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public bool ChangeRequested { get; private set; }

    public bool VerificationSent { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        return await LoadUserAsync(cancellationToken) is null ? Challenge() : Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        var password = Password;
        Password = null;
        Errors = await emailAccounts.RequestEmailChangeAsync(user, NewEmail, password, Actor, cancellationToken);
        ChangeRequested = Errors.Count == 0;
        return Page();
    }

    public async Task<IActionResult> OnPostVerifyAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        await emailAccounts.SendVerificationAsync(user, Actor, cancellationToken);
        VerificationSent = true;
        return Page();
    }
}
