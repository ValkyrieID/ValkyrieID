using System.Security.Cryptography;
using System.Text;
using ValkyrieID.Core.Security;

namespace ValkyrieID.Core.Tests.Security;

public sealed class SecretHasherTests
{
    [Fact]
    public void Generated_secrets_are_unique_256_bit_base64url_values()
    {
        var first = SecretHasher.Generate();
        var second = SecretHasher.Generate();

        Assert.NotEqual(first, second);
        Assert.Equal(43, first.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", first);
    }

    [Fact]
    public void Hash_is_sha256_of_the_utf8_secret()
    {
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes("secret")), SecretHasher.Hash("secret"));
    }

    [Fact]
    public void Verify_matches_any_of_several_hashes()
    {
        var secret = SecretHasher.Generate();

        Assert.True(SecretHasher.Verify(secret, [SecretHasher.Hash("other"), SecretHasher.Hash(secret)]));
    }

    [Fact]
    public void Verify_rejects_wrong_secrets_and_empty_hash_lists()
    {
        var secret = SecretHasher.Generate();

        Assert.False(SecretHasher.Verify(secret + "x", [SecretHasher.Hash(secret)]));
        Assert.False(SecretHasher.Verify(secret, []));
    }
}
