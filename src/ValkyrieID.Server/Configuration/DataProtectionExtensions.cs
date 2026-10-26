using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using ValkyrieID.Core.Security;
using ValkyrieID.Data;

namespace ValkyrieID.Server.Configuration;

/// <summary>Encrypts ASP.NET Data Protection keys with the master key before they are stored in the database.</summary>
internal sealed class MasterKeyXmlEncryptor(SecretProtector protector) : IXmlEncryptor
{
    internal const string Purpose = "ValkyrieID.DataProtectionKey";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var ciphertext = protector.Protect(plaintextElement.ToString(SaveOptions.DisableFormatting), Purpose);
        var element = new XElement("encryptedKey", new XElement("value", Convert.ToBase64String(ciphertext)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <remarks>Instantiated by Data Protection through its <see cref="IServiceProvider"/> constructor.</remarks>
internal sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var value = encryptedElement.Element("value")?.Value
            ?? throw new InvalidOperationException("The encrypted Data Protection key is malformed.");
        var plaintext = services.GetRequiredService<SecretProtector>()
            .UnprotectString(Convert.FromBase64String(value), MasterKeyXmlEncryptor.Purpose);
        return XElement.Parse(plaintext);
    }
}

internal static class DataProtectionExtensions
{
    public static IServiceCollection AddValkyrieDataProtection(this IServiceCollection services)
    {
        services.AddDataProtection()
            .SetApplicationName("ValkyrieID")
            .PersistKeysToDbContext<ValkyrieDbContext>();
        services.AddOptions<KeyManagementOptions>()
            .Configure<SecretProtector>((options, protector) => options.XmlEncryptor = new MasterKeyXmlEncryptor(protector));

        return services;
    }
}
