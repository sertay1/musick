using MusicBot.Services.Security;
using Xunit;

namespace MusicBot.Tests;

public class TokenProtectionTests
{
    [Fact]
    public void Protect_EncryptsPlaintext_AddsEncryptedPrefix()
    {
        var rawToken = "my_super_secret_kick_or_spotify_token_12345";
        var protectedToken = TokenProtection.Protect(rawToken);

        Assert.NotNull(protectedToken);
        Assert.NotEqual(rawToken, protectedToken);
        Assert.StartsWith(TokenProtection.EncryptedPrefix, protectedToken);
    }

    [Fact]
    public void Protect_AlreadyEncrypted_DoesNotDoubleEncrypt()
    {
        var rawToken = "secret_token";
        var first = TokenProtection.Protect(rawToken);
        var second = TokenProtection.Protect(first);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Protect_NullOrEmpty_ReturnsOriginal()
    {
        Assert.Equal(string.Empty, TokenProtection.Protect(""));
        Assert.Equal(string.Empty, TokenProtection.Protect(null));
    }

    [Fact]
    public void Unprotect_DecryptsBackToOriginalText()
    {
        var original = "oauth2_refresh_token_xyz987_with_special_characters_!@#$%^&*()";
        var encrypted = TokenProtection.Protect(original);

        var decrypted = TokenProtection.Unprotect(encrypted);

        Assert.Equal(original, decrypted);
    }

    [Fact]
    public void Unprotect_LegacyUnencryptedToken_ReturnsAsIs()
    {
        var legacyPlaintext = "old_unencrypted_token_from_db";
        var result = TokenProtection.Unprotect(legacyPlaintext);

        Assert.Equal(legacyPlaintext, result);
    }

    [Fact]
    public void Unprotect_NullOrEmpty_ReturnsOriginal()
    {
        Assert.Equal(string.Empty, TokenProtection.Unprotect(""));
        Assert.Equal(string.Empty, TokenProtection.Unprotect(null));
    }
}
