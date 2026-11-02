using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.AdminUi;

/// <summary>The built admin UI (the <c>ng build</c> output in <c>wwwroot/admin</c>).</summary>
internal sealed class AdminUiFiles(IFileProvider files)
{
    public IFileProvider Files => files;
}

/// <summary>
/// Serves the Angular admin UI under <c>/admin</c> (PROJECT.md §8.2): its files, and <c>index.html</c> for every
/// other path so the SPA's own routes (deep links, <c>/admin/callback</c>) work.
/// </summary>
internal static partial class AdminUiExtensions
{
    public const string IndexFile = "index.html";

    public static IServiceCollection AddValkyrieAdminUi(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var environment = sp.GetRequiredService<IWebHostEnvironment>();
            var directory = Path.Combine(environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot"), "admin");
            return new AdminUiFiles(Directory.Exists(directory) ? new PhysicalFileProvider(directory) : new NullFileProvider());
        });
        return services;
    }

    /// <summary>Must run before routing: the branch answers every request under <c>/admin</c> itself.</summary>
    public static WebApplication UseValkyrieAdminUi(this WebApplication app)
    {
        var files = app.Services.GetRequiredService<AdminUiFiles>().Files;
        var issuer = app.Services.GetRequiredService<IOptions<IssuerOptions>>().Value;
        var contentTypes = new FileExtensionContentTypeProvider();
        contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

        app.Map(BuiltIn.AdminUiPath, branch =>
        {
            branch.Use((context, next) =>
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = "GET, HEAD";
                    return Task.CompletedTask;
                }

                // "/admin" without the slash: relative URLs in the SPA resolve against "/admin/".
                if (!context.Request.Path.HasValue)
                {
                    context.Response.Redirect($"{context.Request.PathBase}/{context.Request.QueryString}");
                    return Task.CompletedTask;
                }

                return next(context);
            });

            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = files,
                ContentTypeProvider = contentTypes,
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                    HashedFileName().IsMatch(context.File.Name) ? "public, max-age=31536000, immutable" : "no-cache",
            });

            branch.Run(async context =>
            {
                // A missing asset is a 404, not the SPA: only extension-less paths are client-side routes.
                var path = context.Request.Path.Value ?? "/";
                if (Path.HasExtension(path[(path.LastIndexOf('/') + 1)..]))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                var index = files.GetFileInfo(IndexFile);
                if (!index.Exists)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    await context.Response.WriteAsync("The admin UI isn't built. Run `npm run build` in src/ValkyrieID.AdminUI.", context.RequestAborted);
                    return;
                }

                string html;
                await using (var stream = index.CreateReadStream())
                using (var reader = new StreamReader(stream))
                {
                    html = await reader.ReadToEndAsync(context.RequestAborted);
                }

                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.WriteAsync(RenderIndex(html, issuer), context.RequestAborted);
            });
        });

        return app;
    }

    /// <summary>
    /// Points <c>&lt;base href&gt;</c> at the public admin path and tells the SPA the issuer. Both come from
    /// <c>Issuer:Url</c>, never from the request (PROJECT.md §11), so a path base (e.g. <c>/auth</c>) works whether
    /// or not the proxy strips it.
    /// </summary>
    internal static string RenderIndex(string html, IssuerOptions issuer)
    {
        var basePath = new Uri(issuer.Issuer + "/").AbsolutePath.TrimEnd('/') + BuiltIn.AdminUiPath + "/";
        html = BaseHrefTag().Replace(html, _ => $"<base href=\"{WebUtility.HtmlEncode(basePath)}\">", 1);
        return IssuerTag().Replace(html, _ => $"<meta name=\"valkyrieid-issuer\" content=\"{WebUtility.HtmlEncode(issuer.Issuer)}\">", 1);
    }

    [GeneratedRegex("""<base href="[^"]*"\s*/?>""")]
    private static partial Regex BaseHrefTag();

    [GeneratedRegex("""<meta name="valkyrieid-issuer" content="[^"]*"\s*/?>""")]
    private static partial Regex IssuerTag();

    // Angular's output hashing: e.g. main-4WZ2KXJQ.js, chunk-DMUn8ovk.js, styles-PDC3HXA2.css.
    [GeneratedRegex(@"^[a-z]+-[A-Za-z0-9_-]{8,}\.(?:js|css)$")]
    private static partial Regex HashedFileName();
}
