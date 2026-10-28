using ValkyrieID.Core.Clients;

namespace ValkyrieID.Core.Tests.Clients;

public sealed class RedirectUrisTests
{
    [Theory]
    [InlineData("https://app.example.com/callback", true)]
    [InlineData("https://app.example.com/callback?tenant=a", true)]
    [InlineData("http://127.0.0.1/callback", true)]
    [InlineData("http://[::1]:8080/callback", true)]
    [InlineData("http://localhost:4200/callback", true)]
    [InlineData("com.example.app:/oauth2redirect", true)]
    [InlineData("http://app.example.com/callback", false)]
    [InlineData("https://app.example.com/callback#fragment", false)]
    [InlineData("https://user@app.example.com/callback", false)]
    [InlineData("/callback", false)]
    [InlineData("myapp:/callback", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://app.example.com/call back", false)]
    [InlineData("", false)]
    public void IsValid(string value, bool expected) => Assert.Equal(expected, RedirectUris.IsValid(value));

    [Theory]
    [InlineData("https://app.example.com/backchannel", ClientType.Confidential, true)]
    [InlineData("https://app.example.com/backchannel?tenant=a", ClientType.Public, true)]
    [InlineData("http://127.0.0.1:9000/backchannel", ClientType.Confidential, true)]
    [InlineData("http://localhost/backchannel", ClientType.Confidential, true)]
    [InlineData("http://127.0.0.1:9000/backchannel", ClientType.Public, false)]
    [InlineData("http://app.example.com/backchannel", ClientType.Confidential, false)]
    [InlineData("https://app.example.com/backchannel#fragment", ClientType.Confidential, false)]
    [InlineData("https://user@app.example.com/backchannel", ClientType.Confidential, false)]
    [InlineData("com.example.app:/backchannel", ClientType.Confidential, false)]
    [InlineData("/backchannel", ClientType.Confidential, false)]
    [InlineData("", ClientType.Confidential, false)]
    public void IsValidBackChannelLogoutUri(string value, ClientType type, bool expected) =>
        Assert.Equal(expected, RedirectUris.IsValidBackChannelLogoutUri(value, type));

    [Theory]
    [InlineData("https://app.example.com/cb", "https://app.example.com/cb", true)]
    [InlineData("https://app.example.com/cb", "https://app.example.com/cb/", false)]
    [InlineData("https://app.example.com/cb", "https://APP.example.com/cb", false)]
    [InlineData("https://app.example.com/cb", "https://app.example.com:443/cb", false)]
    [InlineData("https://app.example.com/cb", "https://app.example.com/cb?x=1", false)]
    [InlineData("https://app.example.com:8443/cb", "https://app.example.com:9443/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:1234/cb", true)]
    [InlineData("http://127.0.0.1:80/cb", "http://127.0.0.1:65535/cb", true)]
    [InlineData("http://[::1]/cb", "http://[::1]:5000/cb", true)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:99999/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1:1234/other", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1.evil.com/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://127.0.0.1@evil.com/cb", false)]
    [InlineData("http://127.0.0.1/cb", "http://[::1]:1234/cb", false)]
    [InlineData("http://localhost/cb", "http://localhost:1234/cb", false)]
    public void Matches(string registered, string requested, bool expected) =>
        Assert.Equal(expected, RedirectUris.Matches(registered, requested));

    [Theory]
    [InlineData("https://spa.example.com", true)]
    [InlineData("https://spa.example.com:8443", true)]
    [InlineData("http://localhost:4200", true)]
    [InlineData("https://spa.example.com/", false)]
    [InlineData("https://spa.example.com/app", false)]
    [InlineData("https://spa.example.com:443", false)]
    [InlineData("http://spa.example.com", false)]
    [InlineData("*", false)]
    public void IsValidOrigin(string value, bool expected) => Assert.Equal(expected, RedirectUris.IsValidOrigin(value));
}
