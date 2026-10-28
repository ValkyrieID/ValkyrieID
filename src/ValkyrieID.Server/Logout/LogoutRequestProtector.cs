using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace ValkyrieID.Server.Logout;

/// <summary>
/// A validated RP-initiated logout request, handed from the end-session endpoint to the sign-out page.
/// <see cref="RedirectUri"/> was registered for <see cref="ClientId"/> when the request was made; <see cref="Invalid"/>
/// means part of the request failed validation and was ignored.
/// </summary>
internal sealed record LogoutRequest(string? ClientId, string? RedirectUri, string? State, bool Invalid, long IssuedAt = 0);

/// <summary>Protects <see cref="LogoutRequest"/>s so the sign-out page can trust them; they expire after 10 minutes.</summary>
internal sealed class LogoutRequestProtector(IDataProtectionProvider provider, TimeProvider timeProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly IDataProtector _protector = provider.CreateProtector("ValkyrieID.Logout.Request.v1");

    public string Protect(LogoutRequest request) =>
        _protector.Protect(JsonSerializer.Serialize(request with { IssuedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() }));

    public LogoutRequest? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 8000)
        {
            return null;
        }

        try
        {
            var request = JsonSerializer.Deserialize<LogoutRequest>(_protector.Unprotect(value));
            var age = timeProvider.GetUtcNow().ToUnixTimeSeconds() - request?.IssuedAt;
            return age >= 0 && age <= (long)Lifetime.TotalSeconds ? request : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
