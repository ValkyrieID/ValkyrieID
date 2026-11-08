using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Pages.Account;

[EnableRateLimiting(RateLimitPolicies.Login)]
internal sealed class LoginModel(
    LoginService login, SessionManager sessions, LoginAttemptLimiter limiter, TwoFactorService twoFactor, PendingLogin pending) : PageModel, IReturnUrlPage
{
    // Deliberately the same for unknown users, wrong passwords, and disabled or locked-out accounts.
    internal const string InvalidCredentials = "Invalid username or password.";
    internal const string TooManyAttempts = "Too many sign-in attempts. Wait a few minutes and try again.";
    internal const string Unavailable = "Signing in is temporarily unavailable. Try again in a moment.";

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    /// <summary><c>prompt=login</c> shows the form even when a session exists (set by the authorize endpoint).</summary>
    [BindProperty(SupportsGet = true, Name = "prompt")]
    public string? Prompt { get; set; }

    /// <summary>Pre-fills the identifier (OIDC Core A3.1.2.1 <c>login_hint</c>).</summary>
    [BindProperty(SupportsGet = true, Name = "login_hint")]
    public string? LoginHint { get; set; }

    [BindProperty]
    public string? Identifier { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true && Prompt != "login")
        {
            return LocalRedirect(SafeReturnUrl);
        }

        Identifier ??= LoginHint is { Length: <= 256 } hint ? hint : null;
        ViewData[EntryAnimation.Key] = true;
        return Page();
    }

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

        var decision = await limiter.AcquireAsync(identifier, cancellationToken);
        if (!decision.Allowed)
        {
            Errors = [decision.Unavailable ? Unavailable : TooManyAttempts];
            Response.StatusCode = decision.Unavailable ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status429TooManyRequests;
            Response.Headers.RetryAfter = ((int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Page();
        }

        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var user = await login.PasswordLoginAsync(identifier, password, ipAddress, cancellationToken);
        if (user is null)
        {
            Errors = [InvalidCredentials];
            return Page();
        }

        if (user.TwoFactorEnabled)
        {
            await pending.StartAsync(HttpContext, user, PendingStep.Verify);
            return RedirectToPage("/Account/TwoFactor", new { returnUrl = SafeReturnUrl });
        }

        if (await twoFactor.IsRequiredAsync(user, cancellationToken))
        {
            await pending.StartAsync(HttpContext, user, PendingStep.Enroll);
            return RedirectToPage("/Account/EnrollTwoFactor", new { returnUrl = SafeReturnUrl });
        }

        await login.CompleteAsync(user, ["pwd"], ipAddress, cancellationToken);
        await sessions.SignInAsync(HttpContext, user, ["pwd"], cancellationToken);
        return LocalRedirect(SafeReturnUrl);
    }

    /// <summary>Only local URLs are followed, so the login page can't be used as an open redirect.</summary>
    private string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";
}
