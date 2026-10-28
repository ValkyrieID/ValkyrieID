using System.Globalization;
using System.Net;

namespace ValkyrieID.Core.Clients;

/// <summary>Validation and matching of redirect URIs and CORS origins.</summary>
public static class RedirectUris
{
    private static readonly string[] LoopbackPrefixes = ["http://127.0.0.1", "http://[::1]"];

    /// <summary>
    /// Whether <paramref name="value"/> may be registered as a redirect URI: an absolute URI without a fragment
    /// (RFC 6749 A3.1.2). It must use https, http on a loopback host (RFC 8252 A7.3), or a private-use scheme in
    /// reverse domain name notation (RFC 8252 A7.1).
    /// </summary>
    public static bool IsValid(string value)
    {
        if (value.Length is 0 or > 2000
            || value.Contains('#', StringComparison.Ordinal)
            || value.Any(c => c is <= '\x20' or >= '\x7F')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        return uri.Scheme switch
        {
            "https" => uri.Host.Length > 0,
            "http" => IsLoopbackHost(uri),
            _ => uri.Scheme.Contains('.', StringComparison.Ordinal),
        };
    }

    /// <summary>
    /// Exact, case-sensitive string comparison. The only exception is loopback IP literals over http, where any
    /// port is accepted (RFC 8252 A7.3, RFC 9700 A4.1.3).
    /// </summary>
    public static bool Matches(string registered, string requested)
    {
        if (string.Equals(registered, requested, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var prefix in LoopbackPrefixes)
        {
            if (registered.StartsWith(prefix, StringComparison.Ordinal)
                && requested.StartsWith(prefix, StringComparison.Ordinal)
                && TryStripPort(registered[prefix.Length..], out var registeredRest)
                && TryStripPort(requested[prefix.Length..], out var requestedRest))
            {
                return string.Equals(registeredRest, requestedRest, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="value"/> may be registered as a back-channel logout URI (OIDC Back-Channel Logout 1.0
    /// A2.2): an absolute URI without a fragment. It must use https; http is only allowed on a loopback host and only
    /// for confidential clients ("it MAY use the http scheme, provided that the Client Type is confidential").
    /// </summary>
    public static bool IsValidBackChannelLogoutUri(string value, ClientType clientType)
    {
        if (value.Length is 0 or > 2000
            || value.Contains('#', StringComparison.Ordinal)
            || value.Any(c => c is <= '\x20' or >= '\x7F')
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.UserInfo.Length > 0)
        {
            return false;
        }

        return uri.Scheme switch
        {
            "https" => uri.Host.Length > 0,
            "http" => clientType == ClientType.Confidential && IsLoopbackHost(uri),
            _ => false,
        };
    }

    /// <summary>A browser origin (RFC 6454): <c>scheme://host[:port]</c> in canonical form, without a path.</summary>
    public static bool IsValidOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && IsLoopbackHost(uri)))
        && uri.UserInfo.Length == 0
        && string.Equals(uri.GetLeftPart(UriPartial.Authority), value, StringComparison.Ordinal);

    private static bool TryStripPort(string rest, out string remainder)
    {
        remainder = rest;
        if (!rest.StartsWith(':'))
        {
            return rest.Length == 0 || rest[0] is '/' or '?';
        }

        var end = 1;
        while (end < rest.Length && char.IsAsciiDigit(rest[end]))
        {
            end++;
        }

        if (end is 1 or > 6
            || !int.TryParse(rest.AsSpan(1, end - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port > 65535)
        {
            return false;
        }

        remainder = rest[end..];
        return remainder.Length == 0 || remainder[0] is '/' or '?';
    }

    private static bool IsLoopbackHost(Uri uri) =>
        uri.Host == "localhost" || (IPAddress.TryParse(uri.IdnHost, out var ip) && IPAddress.IsLoopback(ip));
}
