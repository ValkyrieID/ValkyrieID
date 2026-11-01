using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Confirms an email address with a link from a verification email. Like the reset page, opening the link only
/// shows a button; the token is used when it's pressed. Works without signing in.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class VerifyEmailModel(EmailAccountService emailAccounts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public bool? Verified { get; private set; }

    public IActionResult OnGet()
    {
        Response.Headers.CacheControl = "no-store";
        return emailAccounts.Enabled ? Page() : NotFound();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        Verified = await emailAccounts.VerifyEmailAsync(Token, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        return Page();
    }
}
