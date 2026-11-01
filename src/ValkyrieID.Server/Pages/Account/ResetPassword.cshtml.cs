using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Sets a new password with a link from a reset email. Opening the link only shows the form; the token is used
/// when the form is submitted, so mail scanners that fetch links can't use it up.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class ResetPasswordModel(EmailAccountService emailAccounts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    [BindProperty]
    public string? NewPassword { get; set; }

    [BindProperty]
    public string? ConfirmPassword { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public bool InvalidLink { get; private set; }

    public bool Done { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        InvalidLink = !await emailAccounts.IsValidAsync(Token, EmailTokenPurpose.ResetPassword, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        var (next, confirm) = (NewPassword, ConfirmPassword);
        (NewPassword, ConfirmPassword) = (null, null);
        Errors = await emailAccounts.ResetPasswordAsync(Token, next, confirm, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        InvalidLink = Errors is [EmailAccountService.InvalidLink];
        Done = Errors.Count == 0;
        return Page();
    }
}
