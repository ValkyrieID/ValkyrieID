using Microsoft.AspNetCore.Mvc;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Lists the user's active sessions and revokes them (with back-channel logout and their refresh tokens).</summary>
internal sealed class SessionsModel(AccountService account, SessionManager sessionManager) : AccountPageModel(account)
{
    [BindProperty]
    public string? SessionId { get; set; }

    public IReadOnlyList<UserSession> Sessions { get; private set; } = [];

    public string? Message { get; private set; }

    public bool IsCurrent(UserSession session) => session.Id == CurrentSessionId;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        Sessions = await Account.ListActiveSessionsAsync(user.Id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        if (SessionId is not null && SessionId == CurrentSessionId)
        {
            // Revoking the current session is signing out.
            await sessionManager.SignOutAsync(HttpContext, cancellationToken);
            return RedirectToPage("/Account/Login");
        }

        Message = await Account.RevokeSessionAsync(user.Id, SessionId, Actor, cancellationToken)
            ? "The session has been signed out."
            : null;
        Errors = Message is null ? ["The session wasn't found. It may have already ended."] : [];
        Sessions = await Account.ListActiveSessionsAsync(user.Id, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeOthersAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        var count = await Account.RevokeOtherSessionsAsync(user.Id, CurrentSessionId, Actor, cancellationToken);
        Message = count == 1 ? "1 other session has been signed out." : $"{count} other sessions have been signed out.";
        Sessions = await Account.ListActiveSessionsAsync(user.Id, cancellationToken);
        return Page();
    }
}
