using Microsoft.EntityFrameworkCore;
using ValkyrieID.Server.Administration;

namespace ValkyrieID.Server.AdminApi;

/// <summary>A page of a list: <paramref name="Total"/> is the number of matches across all pages.</summary>
internal sealed record Page<T>(IReadOnlyList<T> Items, int Total);

/// <summary>Turns rejected changes into RFC 9457 problem responses.</summary>
internal sealed class AdminErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (AdministrationException ex)
        {
            return ex.Kind switch
            {
                // The empty key holds errors that aren't about a single field (as in ASP.NET Core model state).
                AdministrationError.Invalid => TypedResults.ValidationProblem(new Dictionary<string, string[]> { [string.Empty] = [.. ex.Errors] }),
                AdministrationError.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, detail: ex.Message),
                _ => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, detail: ex.Message),
            };
        }
        catch (DbUpdateException)
        {
            // A unique index or concurrent change; the request may succeed when retried with fresh data.
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, detail: "The change conflicts with another change. Reload and try again.");
        }
    }
}

internal static class AdminRequests
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;
    public const int MaxQueryLength = 200;

    /// <summary>Validates paging parameters; invalid values are rejected rather than clamped.</summary>
    public static (int Limit, int Offset, string? Query) Paging(int? limit, int? offset, string? q = null)
    {
        Dictionary<string, string[]> errors = [];
        if (limit is < 1 or > MaxLimit)
        {
            errors["limit"] = [$"limit must be between 1 and {MaxLimit}."];
        }

        if (offset is < 0)
        {
            errors["offset"] = ["offset must not be negative."];
        }

        if (q?.Length > MaxQueryLength)
        {
            errors["q"] = [$"q must be at most {MaxQueryLength} characters."];
        }

        if (errors.Count > 0)
        {
            throw new AdministrationException([.. errors.SelectMany(e => e.Value)]);
        }

        return (limit ?? DefaultLimit, offset ?? 0, string.IsNullOrWhiteSpace(q) ? null : q.Trim());
    }

    public static T Found<T>(T? value, string what)
        where T : class =>
        value ?? throw AdministrationException.NotFound($"The {what} does not exist.");

    public static TimeSpan? Seconds(int? seconds, string field) => seconds switch
    {
        null => null,
        <= 0 => throw new AdministrationException($"{field} must be a positive number of seconds."),
        _ => TimeSpan.FromSeconds(seconds.Value),
    };

    public static int? Seconds(TimeSpan? value) => value is { } span ? (int)span.TotalSeconds : null;
}
