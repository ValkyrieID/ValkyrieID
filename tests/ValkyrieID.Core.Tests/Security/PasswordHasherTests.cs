using ValkyrieID.Core.Security;

namespace ValkyrieID.Core.Tests.Security;

public sealed class PasswordHasherTests
{
    // Cheap parameters keep the tests fast; production defaults are covered by Default_parameters_follow_owasp.
    private static readonly Argon2Parameters Fast = new(MemorySizeKiB: 1024, Iterations: 1, Parallelism: 1);
    private readonly PasswordHasher _hasher = new(Fast);

    [Fact]
    public void Hash_is_a_phc_string_with_random_salt()
    {
        var first = _hasher.Hash("hunter22hunter22");
        var second = _hasher.Hash("hunter22hunter22");

        Assert.StartsWith("$argon2id$v=19$m=1024,t=1,p=1$", first, StringComparison.Ordinal);
        Assert.Equal(6, first.Split('$').Length);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Verify_accepts_the_right_password_only()
    {
        var hash = _hasher.Hash("hunter22hunter22");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify("hunter22hunter22", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("hunter22hunter23", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("HUNTER22HUNTER22", hash));
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify(string.Empty, hash));
    }

    [Fact]
    public void Passwords_are_unicode_normalized()
    {
        // "é" precomposed vs. "e" + combining acute accent.
        var hash = _hasher.Hash("caf\u00e9-password");

        Assert.Equal(PasswordVerificationResult.Success, _hasher.Verify("cafe\u0301-password", hash));
    }

    [Fact]
    public void Changed_parameters_request_a_rehash()
    {
        var hash = new PasswordHasher(Fast with { Iterations = 2 }).Hash("hunter22hunter22");

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, _hasher.Verify("hunter22hunter22", hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("plaintext")]
    [InlineData("$argon2i$v=19$m=1024,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=16$m=1024,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1,x=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=99999999,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1000,p=1$c2FsdHNhbHRzYWx0$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$!!$aGFzaGhhc2hoYXNoaGFzaA")]
    [InlineData("$argon2id$v=19$m=1024,t=1,p=1$c2FsdHNhbHRzYWx0$aGFzaA")]
    public void Malformed_or_unsafe_hashes_never_verify(string? hash)
    {
        Assert.Equal(PasswordVerificationResult.Failed, _hasher.Verify("hunter22hunter22", hash));
    }

    [Fact]
    public void Default_parameters_follow_owasp()
    {
        Assert.Equal(new Argon2Parameters(12288, 3, 1), Argon2Parameters.Default);
        Assert.StartsWith("$argon2id$v=19$m=12288,t=3,p=1$", new PasswordHasher().Hash("x"), StringComparison.Ordinal);
    }

    [Fact]
    public void Dummy_verification_does_not_throw()
    {
        _hasher.VerifyDummy("anything");
    }
}
