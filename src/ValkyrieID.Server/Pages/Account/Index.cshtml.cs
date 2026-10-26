using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ValkyrieID.Server.Pages.Account;

[Authorize]
internal sealed class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
