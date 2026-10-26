using ValkyrieID.Core.Protocol;

namespace ValkyrieID.Core.Tests.Protocol;

public sealed class ProtocolSyntaxTests
{
    [Theory]
    [InlineData("orders.read", true)]
    [InlineData("api:write!", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("quote\"", false)]
    [InlineData("back\\slash", false)]
    [InlineData("tab\t", false)]
    [InlineData("ümlaut", false)]
    public void Scope_tokens_follow_rfc6749_section_3_3(string value, bool valid) =>
        Assert.Equal(valid, ProtocolSyntax.IsValidScopeToken(value));

    [Theory]
    [InlineData("billing", true)]
    [InlineData("svc:a+b%", true)]
    [InlineData("", false)]
    [InlineData("with space", false)]
    [InlineData("ümlaut", false)]
    public void Client_ids_are_printable_ascii(string value, bool valid) =>
        Assert.Equal(valid, ProtocolSyntax.IsValidClientId(value));
}
