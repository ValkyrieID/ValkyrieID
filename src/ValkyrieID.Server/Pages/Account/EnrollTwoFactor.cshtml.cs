using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Enrollment during sign-in, for users who must use 2FA but haven't set it up. The session is only created once
/// a code confirms the new authenticator.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class EnrollTwoFactorModel(LoginService login, SessionManager sessions, TwoFactorService twoFactor, PendingLogin pending) : PageModel, IReturnUrlPage
{
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public string? State { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    public TotpEnrollment? Enrollment { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public string SafeReturnUrl => Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/account";

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var user = await pending.GetAsync(HttpContext, PendingStep.Enroll, cancellationToken);
        if (user is null)
        {
            return BackToLogin();
        }

        Enrollment = twoFactor.BeginEnrollment(user);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        var user = await pending.GetAsync(HttpContext, PendingStep.Enroll, cancellationToken);
        if (user is null)
        {
            return BackToLogin();
        }

        var code = Code;
        Code = null;
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();
        var codes = await twoFactor.ConfirmEnrollmentAsync(user, State, code, new AuditActor(user.Id.ToString(), ipAddress), cancellationToken);
        if (codes is null)
        {
            Enrollment = twoFactor.ContinueEnrollment(user, State) ?? twoFactor.BeginEnrollment(user);
            Errors = [TwoFactorModel.InvalidCode];
            return Page();
        }

        await pending.EndAsync(HttpContext);
        await login.CompleteAsync(user, TwoFactorModel.Methods, ipAddress, cancellationToken);
        await sessions.SignInAsync(HttpContext, user, TwoFactorModel.Methods, cancellationToken);
        RecoveryCodes = codes;
        return Page();
    }

    private RedirectToPageResult BackToLogin() => RedirectToPage("/Account/Login", new { returnUrl = SafeReturnUrl });
}
