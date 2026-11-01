using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Auditing;
using ValkyrieID.Core.Tokens;
using ValkyrieID.Data;
using ValkyrieID.Server.Administration;
using ValkyrieID.Server.Tokens;

namespace ValkyrieID.Server.AdminApi;

/// <summary>A signing key; <see cref="Status"/> is <c>pending</c> (published, not yet signing), <c>active</c>, or <c>retired</c>.</summary>
internal sealed record SigningKeyDto(string Id, string Algorithm, DateTimeOffset CreatedAt, DateTimeOffset ActivatesAt, string Status);

internal sealed record AuditEntryDto(
    Guid Id,
    DateTimeOffset Timestamp,
    string EventType,
    bool Success,
    Guid? SubjectId,
    string? Actor,
    string? ClientId,
    string? IpAddress,
    JsonElement? Details);

/// <summary>Newest entries first. Pass <see cref="Next"/> as <c>cursor</c> for the following page; it's null on the last page.</summary>
internal sealed record AuditPage(IReadOnlyList<AuditEntryDto> Items, string? Next);

internal static class SystemEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        var keys = api.MapGroup("/signing-keys").WithTags("Signing keys");
        keys.MapGet("/", ListKeysAsync);
        keys.MapPost("/rotate", RotateAsync);

        api.MapGet("/audit", ListAuditAsync).WithTags("Audit log");
    }

    private static async Task<Ok<IReadOnlyList<SigningKeyDto>>> ListKeysAsync(ValkyrieDbContext db, TimeProvider time, CancellationToken ct)
    {
        var keys = await db.SigningKeys.AsNoTracking().OrderBy(k => k.ActivatesAt).ToListAsync(ct);
        return TypedResults.Ok(Dtos(keys, time.GetUtcNow()));
    }

    /// <summary>
    /// Starts a key rotation: the new key is published in JWKS now and signs tokens after the publish lead time.
    /// Returns 409 while a newer key is already pending.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<SigningKeyDto>>> RotateAsync(
        AdminCaller caller, SigningKeyRotator rotator, ValkyrieDbContext db, TimeProvider time, CancellationToken ct)
    {
        if (await rotator.RotateNowAsync(caller.Actor.Name, caller.IpAddress, ct) is null)
        {
            throw AdministrationException.Conflict("A new signing key is already pending; it becomes active at its activation time.");
        }

        return await ListKeysAsync(db, time, ct);
    }

    private static IReadOnlyList<SigningKeyDto> Dtos(List<SigningKey> keys, DateTimeOffset now)
    {
        var current = keys.LastOrDefault(k => k.ActivatesAt <= now);
        return [.. keys.OrderByDescending(k => k.ActivatesAt).Select(k => new SigningKeyDto(
            k.Id,
            k.Algorithm,
            k.CreatedAt,
            k.ActivatesAt,
            k.ActivatesAt > now ? "pending" : k == current ? "active" : "retired"))];
    }

    private static async Task<Ok<AuditPage>> ListAuditAsync(
        ValkyrieDbContext db,
        int? limit,
        string? cursor,
        string? type,
        Guid? subject,
        string? actor,
        string? clientId,
        bool? success,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        var (take, _, _) = AdminRequests.Paging(limit, null);
        var entries = db.AuditLog.AsNoTracking();
        if (!string.IsNullOrEmpty(type))
        {
            // "user." matches every user event.
            entries = type.EndsWith('.') ? entries.Where(e => e.EventType.StartsWith(type)) : entries.Where(e => e.EventType == type);
        }

        if (subject is { } subjectId)
        {
            entries = entries.Where(e => e.SubjectId == subjectId);
        }

        if (!string.IsNullOrEmpty(actor))
        {
            entries = entries.Where(e => e.Actor == actor);
        }

        if (!string.IsNullOrEmpty(clientId))
        {
            entries = entries.Where(e => e.ClientId == clientId);
        }

        if (success is { } succeeded)
        {
            entries = entries.Where(e => e.Success == succeeded);
        }

        if (from is { } start)
        {
            entries = entries.Where(e => e.Timestamp >= start);
        }

        if (to is { } end)
        {
            entries = entries.Where(e => e.Timestamp < end);
        }

        if (cursor is not null)
        {
            var (timestamp, id) = ParseCursor(cursor);
            entries = entries.Where(e => e.Timestamp < timestamp || (e.Timestamp == timestamp && e.Id.CompareTo(id) < 0));
        }

        var page = await entries.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).Take(take + 1).ToListAsync(ct);
        var next = page.Count > take ? Cursor(page[take - 1]) : null;
        return TypedResults.Ok(new AuditPage([.. page.Take(take).Select(Dto)], next));
    }

    private static AuditEntryDto Dto(AuditLogEntry entry) => new(
        entry.Id,
        entry.Timestamp,
        entry.EventType,
        entry.Success,
        entry.SubjectId,
        entry.Actor,
        entry.ClientId,
        entry.IpAddress,
        entry.Details is null ? null : JsonDocument.Parse(entry.Details).RootElement.Clone());

    private static string Cursor(AuditLogEntry entry) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(FormattableString.Invariant($"{entry.Timestamp.UtcTicks}:{entry.Id}")));

    private static (DateTimeOffset Timestamp, Guid Id) ParseCursor(string cursor)
    {
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor)).Split(':');
            if (parts.Length == 2
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks
                && Guid.TryParse(parts[1], out var id))
            {
                return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
            }
        }
        catch (FormatException)
        {
        }

        throw new AdministrationException("The cursor is invalid.");
    }
}
