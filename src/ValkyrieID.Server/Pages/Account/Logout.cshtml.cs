using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Data;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Logout;
using ValkyrieID.Server.Protocol;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Sign-out confirmation, also for RP-initiated logout requests from <see cref="EndSessionEndpoint"/>, which pass a
/// protected <see cref="LogoutRequest"/>.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Account)]
internal sealed class LogoutModel(SessionManager sessions, LogoutRequestProtector protector, ValkyrieDbContext db) : PageModel, IExternalFormTargetPage
{
    [BindProperty(SupportsGet = true, Name = "request")]
    public string? LogoutRequestValue { get; set; }

    /// <summary>The validated post-logout redirect the confirmation form leads to, for CSP <c>form-action</c>.</summary>
    public string? FormTarget { get; private set; }

    /// <summary>The application that asked for the sign-out, if known.</summary>
    public string? ClientName { get; private set; }

    /// <summary>The application's request was invalid, so the user won't be returned to it.</summary>
    public bool InvalidRequest { get; private set; }

    /// <summary>Where a signed-out user can return to, if the application asked for it.</summary>
    public string? ReturnLocation { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        ProtocolHeaders.NoStore(Response);
        var request = await LoadAsync(cancellationToken);
        if (request is not null)
        {
            FormTarget = await RedirectLocationAsync(request, cancellationToken);
        }

        if (User.Identity?.IsAuthenticated != true && request is not null)
        {
            ReturnLocation = FormTarget;
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        ProtocolHeaders.NoStore(Response);
        var request = await LoadAsync(cancellationToken);
        if (User.Identity?.IsAuthenticated == true)
        {
            await sessions.SignOutAsync(HttpContext, cancellationToken);
        }

        if (request is not null && await RedirectLocationAsync(request, cancellationToken) is { } location)
        {
            return Redirect(location);
        }

        return LogoutRequestValue is null ? RedirectToPage() : RedirectToPage(new { request = LogoutRequestValue });
    }

    private async Task<LogoutRequest?> LoadAsync(CancellationToken cancellationToken)
    {
        if (LogoutRequestValue is null)
        {
            return null;
        }

        var request = protector.Unprotect(LogoutRequestValue);
        if (request is null)
        {
            InvalidRequest = true;
            return null;
        }

        InvalidRequest = request.Invalid;
        if (request.ClientId is not null)
        {
            ClientName = await db.Clients.AsNoTracking()
                .Where(c => c.ClientId == request.ClientId)
                .Select(c => c.DisplayName ?? c.ClientId)
                .SingleOrDefaultAsync(cancellationToken);
        }

        return request;
    }

    private async Task<string?> RedirectLocationAsync(LogoutRequest request, CancellationToken cancellationToken) =>
        await EndSessionEndpoint.IsRedirectAllowedAsync(db, request, cancellationToken)
            ? EndSessionEndpoint.Location(request.RedirectUri!, request.State)
            : null;
}
