using Microsoft.AspNetCore.Mvc;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.Pages.Account;

/// <summary>Edits the user's names. The email address can't be changed here; that needs verification (phase 7c).</summary>
internal sealed class ProfileModel(AccountService account) : AccountPageModel(account)
{
    [BindProperty]
    public string? DisplayName { get; set; }

    [BindProperty]
    public string? GivenName { get; set; }

    [BindProperty]
    public string? FamilyName { get; set; }

    public bool Saved { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        DisplayName = user.DisplayName;
        GivenName = user.GivenName;
        FamilyName = user.FamilyName;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (await LoadUserAsync(cancellationToken) is not { } user)
        {
            return Challenge();
        }

        Errors = await Account.UpdateProfileAsync(user, DisplayName, GivenName, FamilyName, Actor, cancellationToken);
        Saved = Errors.Count == 0;
        return Page();
    }
}
