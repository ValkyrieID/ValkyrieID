using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Second sign-in step for users with 2FA: a code from their authenticator app.</summary>
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class TwoFactorModel(LoginService login, SessionManager sessions, TwoFactorService twoFactor, PendingLogin pending) : PageModel, IReturnUrlPage
{
    internal const string InvalidCode = "The code is invalid or has already been used.";

    /// <summary>RFC 8176 §2: password, one-time password, and multiple factors.</summary>
    internal static readonly string[] Methods = ["pwd", "otp", "mfa"];

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await pending.GetAsync(HttpContext, PendingStep.Verify, cancellationToken) is null ? BackToLogin() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await pending.GetAsync(HttpContext, PendingStep.Verify, cancellationToken);
        if (user is null)
        {
            return BackToLogin();
        }

        var code = Code;
        Code = null;
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!await twoFactor.VerifyTotpAsync(user, code, cancellationToken))
        {
            await login.RecordFailureAsync(user, ipAddress, "invalid_totp", cancellationToken);
            Errors = [InvalidCode];
            return Page();
        }

        await pending.EndAsync(HttpContext);
        await login.CompleteAsync(user, Methods, ipAddress, cancellationToken);
        await sessions.SignInAsync(HttpContext, user, Methods, cancellationToken);
        return LocalRedirect(SafeReturnUrl);
    }

    private string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";

    /// <summary>The pending login expired or is no longer valid (e.g. locked out): start over.</summary>
    private RedirectToPageResult BackToLogin() => RedirectToPage("/Account/Login", new { returnUrl = SafeReturnUrl });
}
