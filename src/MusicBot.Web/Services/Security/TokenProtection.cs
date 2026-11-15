using System.Security.Cryptography;
using System.Text;

namespace MusicBot.Services.Security;

/// <summary>
/// Provides secure encryption and decryption of sensitive tokens (Spotify, Kick)
/// using Windows Data Protection API (DPAPI) scoped to the current user.
/// Stored tokens are prefixed with 'enc:dpapi:' to allow seamless coexistence
/// and automatic migration of unencrypted legacy tokens.
/// </summary>
public static class TokenProtection
{
    public const string EncryptedPrefix = "enc:dpapi:";

    /// <summary>
    /// Encrypts plaintext using DPAPI (CurrentUser scope) and encodes as base64 with prefix.
    /// Returns unchanged if already encrypted or if empty.
    /// </summary>
    public static string Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
            return string.Empty;

        if (plainText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return plainText;

        try
        {
            var bytes = Encoding.UTF8.GetBytes(plainText);
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return EncryptedPrefix + Convert.ToBase64String(encrypted);
        }
        catch (Exception ex)
        {
            throw new CryptographicException("Gizli bilgi güvenli olarak şifrelenemedi; düz metin kaydedilmedi.", ex);
        }
    }

    /// <summary>
    /// Decrypts a DPAPI-encrypted token. If token is unencrypted plaintext,
    /// returns it as-is for backward compatibility.
    /// </summary>
    public static string Unprotect(string? cipherOrPlainText)
    {
        if (string.IsNullOrEmpty(cipherOrPlainText))
            return string.Empty;

        if (!cipherOrPlainText.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            return cipherOrPlainText;

        try
        {
            var base64 = cipherOrPlainText[EncryptedPrefix.Length..];
            var bytes = Convert.FromBase64String(base64);
            var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return string.Empty;
        }
    }
}
