using ValkyrieID.Core.Protocol;

namespace ValkyrieID.Core.Tests.Protocol;

public sealed class PkceTests
{
    // RFC 7636 Appendix B.
    private const string Verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    private const string Challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";

    [Fact]
    public void Verifies_the_rfc7636_example() => Assert.True(Pkce.Verify(Verifier, Challenge));

    [Fact]
    public void Rejects_a_different_verifier() => Assert.False(Pkce.Verify(Verifier[..^1] + "Y", Challenge));

    [Fact]
    public void Plain_challenge_does_not_verify() => Assert.False(Pkce.Verify(Verifier, Verifier));

    [Theory]
    [InlineData(Verifier, true)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789-._~ABCDEFG", true)]
    [InlineData("too-short", false)]
    [InlineData("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjX+", false)]
    public void Verifier_syntax(string value, bool expected) => Assert.Equal(expected, Pkce.IsValidVerifier(value));

    [Fact]
    public void Verifier_length_limits()
    {
        Assert.False(Pkce.IsValidVerifier(new string('a', 42)));
        Assert.True(Pkce.IsValidVerifier(new string('a', 43)));
        Assert.True(Pkce.IsValidVerifier(new string('a', 128)));
        Assert.False(Pkce.IsValidVerifier(new string('a', 129)));
    }

    [Theory]
    [InlineData(Challenge, true)]
    [InlineData(Challenge + "=", false)]
    [InlineData("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw+cM", false)]
    public void S256_challenge_syntax(string value, bool expected) => Assert.Equal(expected, Pkce.IsValidS256Challenge(value));
}
