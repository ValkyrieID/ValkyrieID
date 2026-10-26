using ValkyrieID.Core.Security;

namespace ValkyrieID.Core.Tests.Security;

public sealed class MasterKeyTests
{
    [Fact]
    public void Parses_a_valid_256_bit_key()
    {
        var generated = MasterKey.Generate();

        var parsed = MasterKey.Parse(generated.ToBase64());

        Assert.Equal(generated.Id, parsed.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64!")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==")] // 16 bytes
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")] // 66 bytes
    public void Rejects_invalid_keys(string? value)
    {
        Assert.False(MasterKey.TryParse(value, out var key, out var error));
        Assert.Null(key);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void Error_message_does_not_echo_the_key()
    {
        const string almostKey = "c2VjcmV0LWtleS1tYXRlcmlhbA==";

        MasterKey.TryParse(almostKey, out _, out var error);

        Assert.DoesNotContain(almostKey, error);
    }

    [Fact]
    public void Different_keys_have_different_ids()
    {
        Assert.NotEqual(MasterKey.Generate().Id, MasterKey.Generate().Id);
    }
}
