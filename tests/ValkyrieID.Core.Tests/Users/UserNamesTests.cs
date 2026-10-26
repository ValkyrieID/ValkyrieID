using ValkyrieID.Core.Users;

namespace ValkyrieID.Core.Tests.Users;

public sealed class UserNamesTests
{
    [Theory]
    [InlineData("alice", true)]
    [InlineData("Alice.Smith_2-x", true)]
    [InlineData("jürgen", true)]
    [InlineData("", false)]
    [InlineData("alice@example.test", false)]
    [InlineData("alice smith", false)]
    [InlineData("alice/../bob", false)]
    [InlineData("<script>", false)]
    public void Usernames_are_restricted(string value, bool valid)
    {
        Assert.Equal(valid, UserNames.IsValidUserName(value));
    }

    [Fact]
    public void Usernames_are_at_most_64_characters()
    {
        Assert.True(UserNames.IsValidUserName(new string('a', 64)));
        Assert.False(UserNames.IsValidUserName(new string('a', 65)));
    }

    [Theory]
    [InlineData("alice@example.test", true)]
    [InlineData("alice+tag@example.test", true)]
    [InlineData("alice", false)]
    [InlineData("Alice <alice@example.test>", false)]
    [InlineData("alice@example.test, bob@example.test", false)]
    [InlineData(" alice@example.test", false)]
    public void Emails_must_be_a_single_plain_address(string value, bool valid)
    {
        Assert.Equal(valid, UserNames.IsValidEmail(value));
    }

    [Fact]
    public void Normalization_is_case_insensitive_and_unicode_normalized()
    {
        Assert.Equal(UserNames.Normalize("ALICE"), UserNames.Normalize(" alice "));
        Assert.Equal(UserNames.Normalize("jürgen"), UserNames.Normalize("ju\u0308rgen"));
    }
}
