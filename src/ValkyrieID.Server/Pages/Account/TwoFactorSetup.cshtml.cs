using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Core.Users;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Turns 2FA on or off and regenerates recovery codes from the account page. The current password is required
/// for each, so someone with a stolen session cookie can't add their own authenticator, remove the user's, or
/// take over the recovery codes.
/// </summary>
[EnableRateLimiting(RateLimitPolicies.TwoFactor)]
internal sealed class TwoFactorSetupModel(AccountService account, TwoFactorService twoFactor, SessionManager sessionManager) : AccountPageModel(account)
{
    [BindProperty]
    public string? State { get; set; }

    [BindProperty]
    public string? Code { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    public TotpEnrollment? Enrollment { get; private set; }

    public IReadOnlyList<string>? RecoveryCodes { get; private set; }

    /// <summary>The recovery codes shown were regenerated, not created by turning 2FA on.</summary>
    public bool Regenerated { get; private set; }

    public bool Disabled { get; private set; }

    public bool Required { get; private set; }

    public int UnusedRecoveryCodes { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        await ShowAsync(user, cancellationToken);
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

        var (password, code) = TakeSecrets();
        if (!await Account.VerifyPasswordAsync(user, password, Actor, cancellationToken))
        {
            return RetryEnrollment(await LoadUserAsync(cancellationToken), AccountService.IncorrectPassword);
        }

        var codes = await twoFactor.ConfirmEnrollmentAsync(user, State, code, Actor, cancellationToken);
        if (codes is null)
        {
            return RetryEnrollment(user, TwoFactorModel.InvalidCode);
        }

        RecoveryCodes = codes;
        return Page();
    }

    public async Task<IActionResult> OnPostDisableAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        var (password, code) = TakeSecrets();
        Errors = await Account.DisableTwoFactorAsync(user, password, code, CurrentSessionId, Actor, cancellationToken);
        if (Errors.Count > 0)
        {
            return await RetryAsync(cancellationToken);
        }

        await sessionManager.RefreshCookieAsync(HttpContext, user, cancellationToken);
        Disabled = true;
        await ShowAsync(user, cancellationToken);
        return Page();
    }

    public async Task<IActionResult> OnPostRegenerateAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        var (password, _) = TakeSecrets();
        var codes = await Account.RegenerateRecoveryCodesAsync(user, password, Actor, cancellationToken);
        if (codes is null)
        {
            Errors = [AccountService.IncorrectPassword];
            return await RetryAsync(cancellationToken);
        }

        RecoveryCodes = codes;
        Regenerated = true;
        return Page();
    }

    private (string? Password, string? Code) TakeSecrets()
    {
        var secrets = (Password, Code);
        (Password, Code) = (null, null);
        return secrets;
    }

    private async Task ShowAsync(User user, CancellationToken cancellationToken)
    {
        Required = await twoFactor.IsRequiredAsync(user, cancellationToken);
        if (user.TwoFactorEnabled)
        {
            UnusedRecoveryCodes = await twoFactor.CountUnusedRecoveryCodesAsync(user, cancellationToken);
        }
        else
        {
            Enrollment = twoFactor.BeginEnrollment(user);
        }
    }

    /// <summary>Shows the page again after a failed action; the user is reloaded because a failure may have changed it.</summary>
    private async Task<IActionResult> RetryAsync(CancellationToken cancellationToken)
    {
        var errors = Errors;
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        await ShowAsync(user, cancellationToken);
        Errors = errors;
        return Page();
    }

    private IActionResult RetryEnrollment(User? user, string error)
    {
        if (user is null)
        {
            return Challenge();
        }

        Enrollment = twoFactor.ContinueEnrollment(user, State) ?? twoFactor.BeginEnrollment(user);
        Errors = [error];
        return Page();
    }
}
