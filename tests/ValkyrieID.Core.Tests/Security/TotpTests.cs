using System.Text;
using ValkyrieID.Core.Security;

namespace ValkyrieID.Core.Tests.Security;

public sealed class TotpTests
{
    // RFC 6238 Appendix B test secret for HMAC-SHA1.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void Codes_match_the_rfc_6238_test_vectors(long unixTime, string expected)
    {
        // The RFC lists 8-digit codes; 6-digit codes are their last six digits (RFC 4226 §5.3).
        Assert.Equal(expected, Totp.Compute(RfcSecret, Totp.TimeStep(DateTimeOffset.FromUnixTimeSeconds(unixTime))));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Codes_within_one_step_of_drift_are_accepted(int offset)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.TimeStep(now) + offset;

        Assert.Equal(step, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, step), now, lastUsedStep: null));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void Codes_outside_the_drift_window_are_rejected(int offset)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, Totp.TimeStep(now) + offset), now, lastUsedStep: null));
    }

    [Fact]
    public void Codes_at_or_before_the_last_used_step_are_rejected()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var current = Totp.TimeStep(now);
        var code = Totp.Compute(RfcSecret, current);

        Assert.Null(Totp.Verify(RfcSecret, code, now, lastUsedStep: current));
        Assert.Null(Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, current - 1), now, lastUsedStep: current));
        Assert.Equal(current + 1, Totp.Verify(RfcSecret, Totp.Compute(RfcSecret, current + 1), now, lastUsedStep: current));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12345a")]
    [InlineData("１２３４５６")]
    public void Malformed_codes_are_rejected(string code)
    {
        Assert.Null(Totp.Verify(RfcSecret, code, DateTimeOffset.UtcNow, lastUsedStep: null));
    }

    [Fact]
    public void Codes_with_spaces_are_accepted()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.Compute(RfcSecret, Totp.TimeStep(now));

        Assert.NotNull(Totp.Verify(RfcSecret, $"{code[..3]} {code[3..]}", now, lastUsedStep: null));
    }

    [Fact]
    public void Key_uri_contains_the_base32_secret_and_issuer()
    {
        var uri = Totp.KeyUri(RfcSecret, "Valkyrie ID", "alice");

        Assert.Equal(
            "otpauth://totp/Valkyrie%20ID:alice?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=Valkyrie%20ID&algorithm=SHA1&digits=6&period=30",
            uri);
    }

    [Fact]
    public void Base32_round_trips_and_rejects_invalid_characters()
    {
        var data = Totp.GenerateSecret();

        Assert.Equal(data, Base32.Decode(Base32.Encode(data)));
        Assert.Equal("MZXW6YTBOI", Base32.Encode("foobar"u8));
        Assert.Throws<FormatException>(() => Base32.Decode("ABC1"));
    }
}

public sealed class RecoveryCodesTests
{
    [Fact]
    public void Generated_codes_are_unique_and_formatted()
    {
        var codes = Enumerable.Range(0, 100).Select(_ => RecoveryCodes.Generate()).ToList();

        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$", c));
    }

    [Fact]
    public void Hash_ignores_case_dashes_and_spaces()
    {
        var code = RecoveryCodes.Generate();

        Assert.Equal(RecoveryCodes.Hash(code), RecoveryCodes.Hash(code.Replace("-", " ", StringComparison.Ordinal).ToLowerInvariant()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ABCD-EFGH-JKLM")]
    [InlineData("ABCD-EFGH-JKLM-NPQ0")]
    public void Hash_rejects_values_that_cannot_be_codes(string code)
    {
        Assert.Null(RecoveryCodes.Hash(code));
    }
}
