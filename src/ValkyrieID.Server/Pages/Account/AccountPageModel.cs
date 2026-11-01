using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Base for the signed-in user's account pages.</summary>
[Authorize]
internal abstract class AccountPageModel(AccountService account) : PageModel
{
    public User? CurrentUser { get; private set; }

    public IReadOnlyList<string> Errors { get; protected set; } = [];

    protected AccountService Account => account;

    protected string? CurrentSessionId => User.FindFirstValue(SessionClaims.SessionId);

    protected AuditActor Actor => new(CurrentUser?.Id.ToString() ?? string.Empty, HttpContext.Connection.RemoteIpAddress?.ToString());

    protected async Task<User?> LoadUserAsync(CancellationToken cancellationToken)
    {
        CurrentUser = Guid.TryParse(User.FindFirstValue(SessionClaims.Subject), out var id)
            ? await account.FindAsync(id, cancellationToken)
            : null;
        return CurrentUser;
    }
}
