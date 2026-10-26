using System.Security.Cryptography;
using System.Text;
using ValkyrieID.Core.Security;

namespace ValkyrieID.Core.Tests.Security;

public sealed class SecretProtectorTests
{
    private const string Purpose = "test.purpose";

    [Fact]
    public void Protect_then_unprotect_round_trips()
    {
        var protector = new SecretProtector(MasterKey.Generate());

        var protectedData = protector.Protect("s3cret", Purpose);

        Assert.Equal("s3cret", protector.UnprotectString(protectedData, Purpose));
    }

    [Fact]
    public void Protect_round_trips_empty_plaintext()
    {
        var protector = new SecretProtector(MasterKey.Generate());

        Assert.Empty(protector.Unprotect(protector.Protect([], Purpose), Purpose));
    }

    [Fact]
    public void Protect_uses_a_fresh_nonce_each_time()
    {
        var protector = new SecretProtector(MasterKey.Generate());

        Assert.NotEqual(protector.Protect("same", Purpose), protector.Protect("same", Purpose));
    }

    [Fact]
    public void Ciphertext_does_not_contain_plaintext()
    {
        var protector = new SecretProtector(MasterKey.Generate());
        var plaintext = Encoding.UTF8.GetBytes("a-very-recognizable-secret-value");

        var protectedData = protector.Protect(plaintext, Purpose);

        Assert.Equal(-1, protectedData.AsSpan().IndexOf(plaintext));
    }

    [Theory]
    [InlineData(0)] // version
    [InlineData(1)] // key id
    [InlineData(6)] // nonce
    [InlineData(20)] // ciphertext
    [InlineData(-1)] // tag
    public void Unprotect_rejects_tampered_data(int index)
    {
        var protector = new SecretProtector(MasterKey.Generate());
        var protectedData = protector.Protect("s3cret-value", Purpose);

        protectedData[index < 0 ? protectedData.Length + index : index] ^= 0x01;

        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedData, Purpose));
    }

    [Fact]
    public void Unprotect_rejects_truncated_data()
    {
        var protector = new SecretProtector(MasterKey.Generate());
        var protectedData = protector.Protect("s3cret", Purpose);

        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedData.AsSpan(0, 20), Purpose));
        Assert.Throws<CryptographicException>(() => protector.Unprotect([], Purpose));
    }

    [Fact]
    public void Unprotect_rejects_a_different_purpose()
    {
        var protector = new SecretProtector(MasterKey.Generate());
        var protectedData = protector.Protect("s3cret", "totp");

        Assert.Throws<CryptographicException>(() => protector.Unprotect(protectedData, "signing-key"));
    }

    [Fact]
    public void Unprotect_rejects_data_from_an_unknown_key()
    {
        var protectedData = new SecretProtector(MasterKey.Generate()).Protect("s3cret", Purpose);

        Assert.Throws<CryptographicException>(() => new SecretProtector(MasterKey.Generate()).Unprotect(protectedData, Purpose));
    }

    [Fact]
    public void Previous_key_can_decrypt_but_new_data_uses_current_key()
    {
        var oldKey = MasterKey.Generate();
        var newKey = MasterKey.Generate();
        var oldData = new SecretProtector(oldKey).Protect("s3cret", Purpose);

        var rotated = new SecretProtector(newKey, [oldKey]);

        Assert.Equal("s3cret", rotated.UnprotectString(oldData, Purpose));
        Assert.True(rotated.RequiresReencryption(oldData));

        var reencrypted = rotated.Protect(rotated.Unprotect(oldData, Purpose), Purpose);
        Assert.False(rotated.RequiresReencryption(reencrypted));
        Assert.Throws<CryptographicException>(() => new SecretProtector(oldKey).Unprotect(reencrypted, Purpose));
    }

    [Fact]
    public void Protect_requires_a_purpose()
    {
        var protector = new SecretProtector(MasterKey.Generate());

        Assert.Throws<ArgumentException>(() => protector.Protect("x", ""));
    }
}
