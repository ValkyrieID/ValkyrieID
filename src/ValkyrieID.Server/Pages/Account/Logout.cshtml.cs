using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ValkyrieID.Server.Authentication;

namespace ValkyrieID.Server.Pages.Account;

internal sealed class LogoutModel(SessionManager sessions) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            await sessions.SignOutAsync(HttpContext, cancellationToken);
        }

        return RedirectToPage();
    }
}
