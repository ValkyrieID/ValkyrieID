using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>
/// Stores the choice from the theme form in the page header. Razor Pages validate the anti-forgery token on every
/// POST; the redirect only goes to local URLs, so it can't be used as an open redirect (RFC 9700 §4.11).
/// </summary>
[EnableRateLimiting(RateLimitPolicies.Account)]
internal sealed class ThemeModel(TimeProvider time) : PageModel
{
    [BindProperty]
    public string? Theme { get; set; }

    [BindProperty]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet() => NotFound();

    public IActionResult OnPost()
    {
        if (Theme is not { } theme || !ThemePreference.IsValid(theme))
        {
            return BadRequest();
        }

        ThemePreference.Write(Response, theme, time.GetUtcNow());

        // 303: the browser follows with a GET, so reloading the page doesn't submit the form again.
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : Url.Content("~/account");
        return new EmptyResult();
    }
}
