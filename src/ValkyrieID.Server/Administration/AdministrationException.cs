namespace ValkyrieID.Server.Administration;

internal enum AdministrationError
{
    /// <summary>The request is invalid (HTTP 400).</summary>
    Invalid,

    /// <summary>The request conflicts with the current state, e.g. a duplicate name or the last-admin guard (HTTP 409).</summary>
    Conflict,

    /// <summary>The entity doesn't exist (HTTP 404).</summary>
    NotFound,
}

/// <summary>
/// A change that was rejected by the shared administration services (setup wizard, CLI, Admin API).
/// <see cref="Errors"/> are safe to show to the person making it.
/// </summary>
internal sealed class AdministrationException(IReadOnlyList<string> errors, AdministrationError kind = AdministrationError.Invalid)
    : Exception(string.Join(" ", errors))
{
    public AdministrationException(string error, AdministrationError kind = AdministrationError.Invalid)
        : this([error], kind)
    {
    }

    public IReadOnlyList<string> Errors { get; } = errors;

    public AdministrationError Kind { get; } = kind;

    public static AdministrationException Conflict(string error) => new(error, AdministrationError.Conflict);

    public static AdministrationException NotFound(string error) => new(error, AdministrationError.NotFound);
}
