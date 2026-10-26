using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ValkyrieID.Server.Configuration;
using ValkyrieID.Server.Users;

namespace ValkyrieID.Server.IntegrationTests;

public sealed class PasswordPolicyTests
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Policy_enforces_length_in_code_points_and_optional_complexity()
    {
        var validator = Validator(new PasswordPolicyOptions { RequireDigit = true, RequireUppercase = true, RequireNonAlphanumeric = true });

        Assert.Empty(await validator.ValidateAsync("Abcdefg1!", null, null, CancellationToken));
        Assert.Contains(await validator.ValidateAsync("Abc1!", null, null, CancellationToken), e => e.Contains("at least 8", StringComparison.Ordinal));
        Assert.Contains(await validator.ValidateAsync("abcdefgh1!", null, null, CancellationToken), e => e.Contains("uppercase", StringComparison.Ordinal));
        Assert.Contains(await validator.ValidateAsync("Abcdefghi!", null, null, CancellationToken), e => e.Contains("digit", StringComparison.Ordinal));
        Assert.Contains(await validator.ValidateAsync("Abcdefghi1", null, null, CancellationToken), e => e.Contains("isn't a letter", StringComparison.Ordinal));

        // Four emoji are 8 UTF-16 code units but only 4 characters.
        var plain = Validator(new PasswordPolicyOptions());
        Assert.NotEmpty(await plain.ValidateAsync("😀😀😀😀", null, null, CancellationToken));
        Assert.NotEmpty(await plain.ValidateAsync(new string('a', 129), null, null, CancellationToken));
        Assert.Empty(await plain.ValidateAsync(new string('a', 128), null, null, CancellationToken));
    }

    [Fact]
    public async Task Breached_passwords_are_rejected_using_only_a_hash_prefix()
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes("password123")));
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"0000000000000000000000000000000000A:0\r\n{hash[5..]}:42\r\n"),
        });
        var validator = Validator(new PasswordPolicyOptions { CheckBreachedPasswords = true }, handler);

        var errors = await validator.ValidateAsync("password123", null, null, CancellationToken);

        Assert.Contains(errors, e => e.Contains("data breach", StringComparison.Ordinal));
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"https://api.pwnedpasswords.com/range/{hash[..5]}", request.RequestUri?.ToString());
        Assert.Equal("true", Assert.Single(request.Headers.GetValues("Add-Padding")));
        Assert.Empty(await Validator(new PasswordPolicyOptions { CheckBreachedPasswords = true }, handler)
            .ValidateAsync("a different password", null, null, CancellationToken));
    }

    [Fact]
    public async Task Breach_check_fails_open_when_the_service_is_unavailable()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("down"));
        var validator = Validator(new PasswordPolicyOptions { CheckBreachedPasswords = true }, handler);

        Assert.Empty(await validator.ValidateAsync("password123", null, null, CancellationToken));
    }

    [Theory]
    [InlineData("0018A45C4D1DEF81644B54AB7F969B88D65:1", true)]
    [InlineData("0018A45C4D1DEF81644B54AB7F969B88D65:0", false)]
    [InlineData("0018a45c4d1def81644b54ab7f969b88d65:3", true)]
    [InlineData("0018A45C4D1DEF81644B54AB7F969B88D66:1", false)]
    [InlineData("garbage", false)]
    public void Range_responses_are_parsed(string body, bool listed)
    {
        Assert.Equal(listed, BreachedPasswordChecker.IsListed(body, "0018A45C4D1DEF81644B54AB7F969B88D65"));
    }

    private static PasswordValidator Validator(PasswordPolicyOptions options, FakeHandler? handler = null)
    {
        var wrapped = Options.Create(options);
        var checker = new BreachedPasswordChecker(
            new HttpClient(handler ?? new FakeHandler(_ => throw new InvalidOperationException("No request expected."))),
            wrapped,
            NullLogger<BreachedPasswordChecker>.Instance);
        return new PasswordValidator(wrapped, checker);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
