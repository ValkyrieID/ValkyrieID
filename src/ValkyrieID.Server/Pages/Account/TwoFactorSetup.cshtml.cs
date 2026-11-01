using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Security;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Turns on 2FA from the account page. The current password is required too, so someone with a stolen session
/// cookie can't add their own authenticator and lock the user out.
/// </summary>
[Authorize]
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class TwoFactorSetupModel(ValkyrieDbContext db, LoginService login, TwoFactorService twoFactor, PasswordHasher hasher) : PageModel
{
    [BindProperty]
    public string? State { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public User? CurrentUser { get; private set; }

    public TotpEnrollment? Enrollment { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    public int UnusedRecoveryCodes { get; private set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        if (user.TwoFactorEnabled)
        {
            UnusedRecoveryCodes = await twoFactor.CountUnusedRecoveryCodesAsync(user, cancellationToken);
        }
        else
        {
            Enrollment = twoFactor.BeginEnrollment(user);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        if (user.TwoFactorEnabled)
        {
            return RedirectToPage();
        }

        var password = Password ?? string.Empty;
        var code = Code;
        Password = null;
        Code = null;
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString();

        if (hasher.Verify(password.Length > 1024 ? password[..1024] : password, user.PasswordHash) == PasswordVerificationResult.Failed)
        {
            await login.RecordFailureAsync(user, ipAddress, "invalid_password", cancellationToken);
            return Retry(await LoadUserAsync(cancellationToken), "The password is incorrect.");
        }

        var codes = await twoFactor.ConfirmEnrollmentAsync(user, State, code, new AuditActor(user.Id.ToString(), ipAddress), cancellationToken);
        if (codes is null)
        {
            return Retry(user, TwoFactorModel.InvalidCode);
        }

        RecoveryCodes = codes;
        return Page();
    }

    private IActionResult Retry(User? user, string error)
    {
        if (user is null)
        {
            return Challenge();
        }

        Enrollment = twoFactor.ContinueEnrollment(user, State) ?? twoFactor.BeginEnrollment(user);
        Errors = [error];
        return Page();
    }

    private async Task<User?> LoadUserAsync(CancellationToken cancellationToken)
    {
        CurrentUser = Guid.TryParse(User.FindFirstValue(SessionClaims.Subject), out var id)
            ? await db.Users.SingleOrDefaultAsync(u => u.Id == id, cancellationToken)
            : null;
        return CurrentUser;
    }
}
