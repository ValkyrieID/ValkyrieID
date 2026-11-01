using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ValkyrieID.Core.Users;
using ValkyrieID.Data;

namespace ValkyrieID.Server.Users;

/// <summary>
/// Issues and checks the single-use tokens in email links: 256 random bits, of which only the SHA-256 hash is
/// stored. Changes are added to the context and saved by the caller.
/// </summary>
internal sealed class EmailTokenService(ValkyrieDbContext db, TimeProvider timeProvider)
{
    private const int TokenBytes = 32;

    /// <summary>
    /// Whether a token of this kind was issued to the user within <paramref name="interval"/>; used to limit how
    /// often emails are sent to one account.
    /// </summary>
    public Task<bool> IssuedRecentlyAsync(Guid userId, EmailTokenPurpose purpose, TimeSpan interval, CancellationToken cancellationToken)
    {
        var since = timeProvider.GetUtcNow() - interval;
        return db.EmailTokens.AnyAsync(t => t.UserId == userId && t.Purpose == purpose && t.CreatedAt > since, cancellationToken);
    }

    /// <summary>Issues a token, replacing the user's earlier tokens of the same kind. Returns the token for the link.</summary>
    public async Task<string> IssueAsync(User user, EmailTokenPurpose purpose, string? email, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await DeleteAsync(user.Id, purpose, cancellationToken);

        var token = RandomNumberGenerator.GetBytes(TokenBytes);
        var now = timeProvider.GetUtcNow();
        db.EmailTokens.Add(new EmailToken
        {
            UserId = user.Id,
            Purpose = purpose,
            TokenHash = SHA256.HashData(token),
            Email = email,
            SecurityStamp = user.SecurityStamp,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        });
        return Base64Url.EncodeToString(token);
    }

    /// <summary>
    /// Finds the user for an unused, unexpired token of this kind, issued for the user's current security stamp.
    /// Returns <see langword="null"/> for every kind of invalid token alike.
    /// </summary>
    public async Task<(EmailToken Token, User User)?> FindAsync(string? token, EmailTokenPurpose purpose, CancellationToken cancellationToken)
    {
        if (token is null || token.Length > 64 || !Base64Url.IsValid(token, out var length) || length != TokenBytes)
        {
            return null;
        }

        var hash = SHA256.HashData(Base64Url.DecodeFromChars(token));
        var row = await db.EmailTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        if (row is null
            || !CryptographicOperations.FixedTimeEquals(row.TokenHash, hash)
            || row.Purpose != purpose
            || row.UsedAt is not null
            || timeProvider.GetUtcNow() >= row.ExpiresAt)
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == row.UserId, cancellationToken);
        if (user is not { Enabled: true }
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(row.SecurityStamp), Encoding.UTF8.GetBytes(user.SecurityStamp)))
        {
            return null;
        }

        return (row, user);
    }

    /// <summary>
    /// Marks the token used. The conditional update succeeds for exactly one caller, so concurrent requests can't
    /// both use it.
    /// </summary>
    public async Task<bool> ConsumeAsync(EmailToken token, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var consumed = await db.EmailTokens
            .Where(t => t.Id == token.Id && t.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.UsedAt, now), cancellationToken) == 1;
        db.Entry(token).State = EntityState.Detached;
        return consumed;
    }

    /// <summary>Deletes the user's tokens of this kind, e.g. after one was used.</summary>
    public Task DeleteAsync(Guid userId, EmailTokenPurpose purpose, CancellationToken cancellationToken) =>
        db.EmailTokens.Where(t => t.UserId == userId && t.Purpose == purpose).ExecuteDeleteAsync(cancellationToken);
}
