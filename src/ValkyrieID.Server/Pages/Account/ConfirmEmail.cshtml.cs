using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Completes an email change with the link sent to the new address. The token is used only on submit.</summary>
[EnableRateLimiting(RateLimitPolicies.Email)]
internal sealed class ConfirmEmailModel(EmailAccountService emailAccounts) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Token { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public bool Changed { get; private set; }

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
        Errors = await emailAccounts.ConfirmEmailChangeAsync(Token, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        Changed = Errors.Count == 0;
        return Page();
    }
}
