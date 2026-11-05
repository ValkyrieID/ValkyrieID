using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Second sign-in step with a one-time recovery code, for when the authenticator is unavailable.</summary>
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class RecoveryCodeModel(LoginService login, SessionManager sessions, TwoFactorService twoFactor, PendingLogin pending) : PageModel, IReturnUrlPage
{
    internal const string InvalidCode = "The recovery code is invalid or has already been used.";

    /// <summary>RFC 8176 §2: password and multiple factors. A recovery code is a stored secret, not an OTP device.</summary>
    internal static readonly string[] Methods = ["pwd", "mfa"];

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
        if (!await twoFactor.RedeemRecoveryCodeAsync(user, code, ipAddress, cancellationToken))
        {
            await login.RecordFailureAsync(user, ipAddress, "invalid_recovery_code", cancellationToken);
            Errors = [InvalidCode];
            return Page();
        }

        await pending.EndAsync(HttpContext);
        await login.CompleteAsync(user, Methods, ipAddress, cancellationToken);
        await sessions.SignInAsync(HttpContext, user, Methods, cancellationToken);
        return LocalRedirect(SafeReturnUrl);
    }

    private string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";

    private RedirectToPageResult BackToLogin() => RedirectToPage("/Account/Login", new { returnUrl = SafeReturnUrl });
}
