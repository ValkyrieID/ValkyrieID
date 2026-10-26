using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Protocol;

/// <summary>Minimal server-rendered pages returned by protocol endpoints (errors and <c>form_post</c> responses).</summary>
internal static class ProtocolPages
{
    private const string Csp = "default-src 'none'; style-src 'self'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'none'";

    /// <summary>An error page shown instead of redirecting, when the client or redirect URI can't be trusted.</summary>
    public static IResult Error(string message) => new HtmlPage(StatusCodes.Status400BadRequest, "Sign-in error", null,
        $"""
        <h1>Sign-in error</h1>
        <div class="alert" role="alert"><p>{HtmlEncoder.Default.Encode(message)}</p></div>
        <p>Return to the application and try again.</p>
        """);

    /// <summary>
    /// OAuth 2.0 Form Post Response Mode: an HTML form that posts the response parameters to the redirect URI. A
    /// nonce-allowed script submits it; without JavaScript the user presses the button.
    /// </summary>
    public static IResult FormPost(string action, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var html = new StringBuilder();
        html.Append("<h1>Continue to the application</h1>\n");
        html.Append("<form method=\"post\" action=\"").Append(HtmlEncoder.Default.Encode(action)).Append("\">\n");
        foreach (var (name, value) in fields)
        {
            html.Append("<input type=\"hidden\" name=\"").Append(HtmlEncoder.Default.Encode(name))
                .Append("\" value=\"").Append(HtmlEncoder.Default.Encode(value)).Append("\">\n");
        }

        html.Append("<button type=\"submit\">Continue</button>\n</form>\n");
        html.Append("<script nonce=\"").Append(nonce).Append("\">document.forms[0].submit();</script>\n");
        return new HtmlPage(StatusCodes.Status200OK, "Continue", nonce, html.ToString());
    }

    private sealed class HtmlPage(int statusCode, string title, string? scriptNonce, string body) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            var response = httpContext.Response;
            var branding = httpContext.RequestServices.GetRequiredService<IOptions<BrandingOptions>>().Value;
            var pathBase = httpContext.Request.PathBase.Value ?? string.Empty;
            response.StatusCode = statusCode;
            ProtocolHeaders.NoStore(response);
            if (scriptNonce is not null)
            {
                response.Headers.ContentSecurityPolicy = $"{Csp}; script-src 'nonce-{scriptNonce}'";
            }

            var encoder = HtmlEncoder.Default;
            var customCss = branding.CustomCss is null
                ? string.Empty
                : $"<link rel=\"stylesheet\" href=\"{encoder.Encode($"{pathBase}{AccountExtensions.BrandingPath}/{branding.CustomCss}")}\">";
            var html = $"""
                <!DOCTYPE html>
                <html lang="en">
                <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <meta name="robots" content="noindex">
                <title>{encoder.Encode(title)} - {encoder.Encode(branding.ProductName)}</title>
                <link rel="stylesheet" href="{encoder.Encode(pathBase)}/css/site.css">
                <link rel="stylesheet" href="{encoder.Encode(pathBase)}{AccountExtensions.ThemePath}">
                {customCss}
                </head>
                <body>
                <main class="card">
                <header class="brand"><p class="product-name">{encoder.Encode(branding.ProductName)}</p></header>
                {body}
                </main>
                </body>
                </html>
                """;
            response.ContentType = "text/html; charset=utf-8";
            return response.WriteAsync(html, httpContext.RequestAborted);
        }
    }
}
