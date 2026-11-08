using Microsoft.AspNetCore.Mvc;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

internal sealed class IndexModel(AccountService account) : AccountPageModel(account)
{
    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await LoadUserAsync(cancellationToken) is null ? Challenge() : Page();
}
