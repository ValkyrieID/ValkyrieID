using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Requests a password reset link. The response is the same whether or not the account exists.</summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class ForgotPasswordModel(EmailAccountService emailAccounts) : PageModel
{
    [BindProperty]
    public string? Identifier { get; set; }

    public bool Sent { get; private set; }

    public IActionResult OnGet()
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        ViewData[EntryAnimation.Key] = true;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!emailAccounts.Enabled)
        {
            return NotFound();
        }

        await emailAccounts.RequestPasswordResetAsync(Identifier, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        Identifier = null;
        Sent = true;
        return Page();
    }
}
