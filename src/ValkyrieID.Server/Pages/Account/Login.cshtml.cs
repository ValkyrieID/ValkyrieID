using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Pages.Account;

[EnableRateLimiting(RateLimitPolicies.Login)]
internal sealed class LoginModel(LoginService login, SessionManager sessions, LoginAttemptLimiter limiter) : PageModel
{
    // Deliberately the same for unknown users, wrong passwords, and disabled or locked-out accounts.
    internal const string InvalidCredentials = "Invalid username or password.";
    internal const string TooManyAttempts = "Too many sign-in attempts. Wait a few minutes and try again.";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string? Identifier { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl) : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var identifier = Identifier?.Trim() ?? string.Empty;
        var password = Password ?? string.Empty;
        Password = null;

        if (identifier.Length == 0 || password.Length == 0)
        {
            Errors = ["Enter your username or email and your password."];
            return Page();
        }

        if (!limiter.TryAcquire(identifier))
        {
            Errors = [TooManyAttempts];
            Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return Page();
        }

        var user = await login.PasswordLoginAsync(identifier, password, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
        if (user is null)
        {
            Errors = [InvalidCredentials];
            return Page();
        }

        await sessions.SignInAsync(HttpContext, user, ["pwd"], cancellationToken);
        return LocalRedirect(SafeReturnUrl);
    }

    /// <summary>Only local URLs are followed, so the login page can't be used as an open redirect.</summary>
    private string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";
}
