using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Authentication;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages;

/// <summary>First-run setup wizard: creates the first admin user, then disappears (404) for good.</summary>
[EnableRateLimiting(RateLimitPolicies.Login)]
internal sealed class SetupModel(UserAdministration users, SessionManager sessions, IOptions<PasswordPolicyOptions> passwordPolicy) : PageModel
{
    [BindProperty]
    public string? UserName { get; set; }

    [BindProperty]
    public string? Email { get; set; }

    [BindProperty]
    public string? Password { get; set; }

    [BindProperty]
    public string? ConfirmPassword { get; set; }

    public IReadOnlyList<string> Errors { get; private set; } = [];

    public int MinimumPasswordLength => passwordPolicy.Value.MinimumLength;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) =>
        await users.AnyUsersAsync(cancellationToken) ? NotFound() : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await users.AnyUsersAsync(cancellationToken))
        {
            return NotFound();
        }

        var password = Password ?? string.Empty;
        var confirmation = ConfirmPassword ?? string.Empty;
        Password = ConfirmPassword = null;
        if (password != confirmation)
        {
            Errors = ["The passwords don't match."];
            return Page();
        }

        try
        {
            var user = await users.CompleteSetupAsync(
                new NewUser(UserName ?? string.Empty, Email, null, password),
                new AuditActor("setup", HttpContext.Connection.RemoteIpAddress?.ToString()),
                cancellationToken);
            if (user is null)
            {
                return NotFound();
            }

            await sessions.SignInAsync(HttpContext, user, ["pwd"], cancellationToken);
            return LocalRedirect("/account");
        }
        catch (UserAdministrationException ex)
        {
            Errors = ex.Errors;
            return Page();
        }
    }
}
