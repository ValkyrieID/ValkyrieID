using Microsoft.Extensions.Options;

namespace ValkyrieID.Server.Configuration;

public sealed class IssuerOptions
{
    public const string SectionName = "Issuer";

    /// <summary>
    /// The public issuer URL (<c>iss</c>). Every endpoint URL is derived from it; it is never derived from
    /// request headers.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The issuer without a trailing slash, as published in discovery and tokens.</summary>
    public string Issuer => (Url ?? string.Empty).TrimEnd('/');

    public string Endpoint(string path) => Issuer + path;
}

internal sealed class IssuerOptionsValidator : IValidateOptions<IssuerOptions>
{
    public ValidateOptionsResult Validate(string? name, IssuerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
        {
            return ValidateOptionsResult.Fail(
                "Issuer:Url is required, e.g. https://id.example.com (environment variable Issuer__Url).");
        }

        // OIDC Core §2 (iss), OIDC Discovery §3, and RFC 8414 §2: the issuer MUST be an https URL with no query or
        // fragment. No exception for loopback hosts: development uses https://localhost too.
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0
            || uri.UserInfo.Length > 0)
        {
            return ValidateOptionsResult.Fail(
                "Issuer:Url must be an absolute https URL without query, fragment, or user info.");
        }

        return ValidateOptionsResult.Success;
    }
}
