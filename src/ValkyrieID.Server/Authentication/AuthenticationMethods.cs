namespace ValkyrieID.Server.Authentication;

/// <summary>RFC 8176 authentication method reference (<c>amr</c>) values used by ValkyrieID.</summary>
internal static class AuthenticationMethods
{
    public const string Password = "pwd";
    public const string OneTimePassword = "otp";
    public const string MultiFactor = "mfa";

    /// <summary>
    /// <c>amr</c> values that show a second factor was used. TOTP and recovery code sign-ins both record <c>mfa</c>
    /// (RFC 8176 §2). Future methods (e.g. passkeys, PROJECT.md §6) must be added here to satisfy 2FA requirements.
    /// </summary>
    public static readonly IReadOnlyList<string> SecondFactor = [MultiFactor];

    public static bool HasSecondFactor(IEnumerable<string> methods) =>
        methods.Any(m => SecondFactor.Contains(m, StringComparer.Ordinal));
}
