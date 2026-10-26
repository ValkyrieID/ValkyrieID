using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Clients;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;
using ValkyrieID.Server.Auditing;

namespace ValkyrieID.Server.Protocol;

internal sealed record ClientAuthenticationResult(Client? Client, OAuthError? Error);

/// <summary>
/// Authenticates confidential clients with <c>client_secret_basic</c> or <c>client_secret_post</c>, and identifies
/// public clients by <c>client_id</c> (<c>none</c>).
/// </summary>
internal sealed class ClientAuthenticator(ValkyrieDbContext db, AuditRecorder audit, TimeProvider timeProvider)
{
    public const string ClientSecretBasic = "client_secret_basic";
    public const string ClientSecretPost = "client_secret_post";
    public const string None = "none";

    public async Task<ClientAuthenticationResult> AuthenticateAsync(HttpContext context, IFormCollection form, CancellationToken cancellationToken)
    {
        var bodyClientId = Single(form, "client_id");
        var bodySecret = Single(form, "client_secret");
        string? clientId;
        string? secret;

        if (context.Request.Headers.Authorization.Count > 0)
        {
            // RFC 6749 §2.3: the client MUST NOT use more than one authentication method per request.
            if (bodySecret is not null)
            {
                return Fail(OAuthError.Request("Multiple client authentication methods were used."));
            }

            if (!TryParseBasic(context.Request.Headers.Authorization.ToString(), out clientId, out secret))
            {
                return await RejectAsync(context, null, "malformed_authorization_header", cancellationToken);
            }

            if (bodyClientId is not null && bodyClientId != clientId)
            {
                return Fail(OAuthError.Request("client_id does not match the authenticated client."));
            }
        }
        else
        {
            clientId = bodyClientId;
            secret = bodySecret;
        }

        // Public clients have no secret and identify themselves with client_id only (RFC 6749 A2.1, A3.2.1;
        // token endpoint auth method "none"). Their ids aren't secret, so no constant-time path is needed.
        if (!string.IsNullOrEmpty(clientId) && secret is null && context.Request.Headers.Authorization.Count == 0)
        {
            var publicClient = await db.Clients.SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);
            return publicClient switch
            {
                { Enabled: true, Type: ClientType.Public } => new ClientAuthenticationResult(publicClient, null),
                null => await RejectAsync(context, clientId, "unknown_client", cancellationToken),
                { Type: ClientType.Public } => await RejectAsync(context, clientId, "client_not_usable", cancellationToken),
                _ => await RejectAsync(context, clientId, "missing_credentials", cancellationToken),
            };
        }

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(secret))
        {
            return await RejectAsync(context, clientId, "missing_credentials", cancellationToken);
        }

        var client = await db.Clients.Include(c => c.Secrets).AsSplitQuery()
            .SingleOrDefaultAsync(c => c.ClientId == clientId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var usable = client is { Enabled: true, Type: ClientType.Confidential };
        var hashes = usable && client is not null
            ? client.Secrets.Where(s => s.IsActive(now)).Select(s => s.Hash)
            : Enumerable.Empty<byte[]>();

        // Always hash and compare, also for unknown or disabled clients, so timing doesn't reveal which clients exist.
        if (!SecretHasher.Verify(secret, hashes) || !usable)
        {
            var reason = client is null ? "unknown_client" : !usable ? "client_not_usable" : "invalid_secret";
            return await RejectAsync(context, clientId, reason, cancellationToken);
        }

        return new ClientAuthenticationResult(client, null);
    }

    private async Task<ClientAuthenticationResult> RejectAsync(HttpContext context, string? clientId, string reason, CancellationToken cancellationToken)
    {
        audit.Add(
            "client.authentication_failed",
            success: false,
            actor: clientId,
            clientId: clientId,
            ipAddress: context.Connection.RemoteIpAddress?.ToString(),
            details: new Dictionary<string, object?> { ["reason"] = reason });
        await db.SaveChangesAsync(cancellationToken);
        return Fail(OAuthError.Client());
    }

    private static ClientAuthenticationResult Fail(OAuthError error) => new(null, error);

    private static string? Single(IFormCollection form, string name) =>
        form.TryGetValue(name, out var values) ? values.ToString() : null;

    /// <summary>
    /// RFC 6749 §2.3.1: client_id and client_secret are form-urlencoded (Appendix B) before being used as the
    /// RFC 7617 user-id and password.
    /// </summary>
    internal static bool TryParseBasic(string? header, out string? clientId, out string? secret)
    {
        clientId = secret = null;
        if (!AuthenticationHeaderValue.TryParse(header, out var value)
            || !string.Equals(value.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(value.Parameter))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(value.Parameter));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        clientId = FormDecode(decoded[..separator]);
        secret = FormDecode(decoded[(separator + 1)..]);
        return true;
    }

    private static string FormDecode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
