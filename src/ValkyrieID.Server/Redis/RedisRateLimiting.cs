using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using StackExchange.Redis;
using ValkyrieID.Core.Security;
using ValkyrieID.Server.Configuration;

namespace ValkyrieID.Server.Redis;

internal readonly record struct RateLimitDecision(bool Allowed, TimeSpan RetryAfter, bool Unavailable)
{
    public static RateLimitDecision Unreachable { get; } = new(false, TimeSpan.FromSeconds(5), true);
}

/// <summary>
/// Fixed-window counters shared by every instance, with the same semantics as the in-process
/// <see cref="FixedWindowRateLimiter"/>: at most <see cref="RateLimitRule.PermitLimit"/> requests per window, and
/// <c>Retry-After</c> is the time until the window ends. Partition keys (IP addresses, usernames, emails) are only
/// stored as an HMAC, so Redis holds no personal data.
/// </summary>
internal sealed class RedisRateLimitCounter
{
    private const string Purpose = "ValkyrieID.RateLimitKey";

    // INCR and PEXPIRE in one step: a crash between them can't leave a counter without expiry.
    private const string HitScript = """
        local count = redis.call('INCR', KEYS[1])
        local ttl = redis.call('PTTL', KEYS[1])
        if count == 1 or ttl < 0 then
          redis.call('PEXPIRE', KEYS[1], ARGV[1])
          ttl = tonumber(ARGV[1])
        end
        return {count, ttl}
        """;

    private readonly RedisContext _redis;
    private readonly byte[] _key;

    public RedisRateLimitCounter(RedisContext redis, SecretProtector protector)
    {
        _redis = redis;
        _key = protector.DeriveKey(Purpose);
    }

    public async Task<RateLimitDecision> HitAsync(string partition, RateLimitRule rule, CancellationToken cancellationToken)
    {
        var name = "rl:" + Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(partition)));
        try
        {
            var result = (RedisResult[])(await _redis.Database
                .ScriptEvaluateAsync(HitScript, [_redis.Key(name)], [(long)rule.Window.TotalMilliseconds])
                .WaitAsync(cancellationToken))!;
            var count = (long)result[0];
            var ttl = TimeSpan.FromMilliseconds(Math.Max((long)result[1], 0));
            return new RateLimitDecision(count <= rule.PermitLimit, ttl, Unavailable: false);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Fail closed: without the shared counters the limits would multiply with the number of instances.
            return RateLimitDecision.Unreachable;
        }
    }
}

/// <summary>
/// A <see cref="RateLimiter"/> for one partition backed by <see cref="RedisRateLimitCounter"/>. The synchronous
/// <see cref="RateLimiter.AttemptAcquire"/> can't reach Redis, so it never grants a permit; the rate limiting middleware
/// then calls <see cref="RateLimiter.AcquireAsync"/>, which does.
/// </summary>
internal sealed class RedisFixedWindowRateLimiter(RedisRateLimitCounter counter, string partition, RateLimitRule rule) : RateLimiter
{
    public static readonly MetadataName<bool> UnavailableMetadata = new("ValkyrieID.SharedStateUnavailable");

    private long _lastUsed = Stopwatch.GetTimestamp();

    public override TimeSpan? IdleDuration => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastUsed));

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount) => DecisionLease.Deferred;

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastUsed, Stopwatch.GetTimestamp());
        if (permitCount == 0)
        {
            return DecisionLease.Granted;
        }

        var decision = await counter.HitAsync(partition, rule, cancellationToken);
        return decision.Allowed ? DecisionLease.Granted : new DecisionLease(decision);
    }

    private sealed class DecisionLease : RateLimitLease
    {
        public static readonly DecisionLease Granted = new(new RateLimitDecision(true, TimeSpan.Zero, false));

        /// <summary>Not acquired, without metadata: tells the middleware to wait for <see cref="AcquireAsyncCore"/>.</summary>
        public static readonly DecisionLease Deferred = new(null);

        private readonly RateLimitDecision? _decision;

        public DecisionLease(RateLimitDecision? decision) => _decision = decision;

        public override bool IsAcquired => _decision?.Allowed ?? false;

        public override IEnumerable<string> MetadataNames => _decision is { Allowed: false } ? [MetadataName.RetryAfter.Name, UnavailableMetadata.Name] : [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (_decision is { Allowed: false } decision)
            {
                if (metadataName == MetadataName.RetryAfter.Name)
                {
                    metadata = decision.RetryAfter;
                    return true;
                }

                if (metadataName == UnavailableMetadata.Name)
                {
                    metadata = decision.Unavailable;
                    return true;
                }
            }

            metadata = null;
            return false;
        }
    }
}
