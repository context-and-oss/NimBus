using System.Buffers.Text;
using System.Security.Cryptography;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>A newly issued key: the full value is shown to the Owner once and never stored.</summary>
/// <param name="KeyId">The public lookup id.</param>
/// <param name="Key">The full key, <c>nbak_&lt;keyId&gt;_&lt;secret&gt;</c>.</param>
/// <param name="SecretHash">What the store keeps: the Base64 SHA-256 of the secret.</param>
internal sealed record GeneratedAgentApiKey(string KeyId, string Key, string SecretHash);

/// <summary>
/// Agent API key material (Spec 039): <c>nbak_&lt;keyId&gt;_&lt;secret&gt;</c>, where the key id is
/// 12 lowercase base32 characters used for lookup and the secret is 32 random bytes,
/// base64url-encoded. Only the secret's SHA-256 is stored; with 256 bits of entropy a slow
/// key-derivation function adds nothing.
/// </summary>
internal static class AgentApiKeyFormat
{
    /// <summary>Marks the value as a NimBus agent key, also for secret scanners.</summary>
    public const string Prefix = "nbak_";

    private const string KeyIdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const int KeyIdLength = 12;
    private const int SecretBytes = 32;
    private const int SecretChars = 43; // base64url without padding for 32 bytes
    private const int KeyLength = 5 + KeyIdLength + 1 + SecretChars;

    /// <summary>Issues a new key.</summary>
    public static GeneratedAgentApiKey Generate()
    {
        var keyId = string.Create(KeyIdLength, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = KeyIdAlphabet[RandomNumberGenerator.GetInt32(KeyIdAlphabet.Length)];
        });
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        var key = Prefix + keyId + "_" + Base64Url.EncodeToString(secret);
        return new GeneratedAgentApiKey(keyId, key, HashSecret(secret));
    }

    /// <summary>Splits a presented key into its id and secret; false for anything malformed.</summary>
    public static bool TryParse(string? value, out string keyId, out byte[] secret)
    {
        keyId = string.Empty;
        secret = [];
        if (value is null || value.Length != KeyLength || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var id = value.AsSpan(Prefix.Length, KeyIdLength);
        foreach (var c in id)
        {
            if (!KeyIdAlphabet.Contains(c, StringComparison.Ordinal))
                return false;
        }

        if (value[Prefix.Length + KeyIdLength] != '_')
            return false;

        var encoded = value.AsSpan(Prefix.Length + KeyIdLength + 1);
        if (!Base64Url.IsValid(encoded, out var decodedLength) || decodedLength != SecretBytes)
            return false;

        secret = Base64Url.DecodeFromChars(encoded);
        keyId = id.ToString();
        return true;
    }

    /// <summary>The stored form of a secret: its Base64 SHA-256.</summary>
    public static string HashSecret(ReadOnlySpan<byte> secret) => Convert.ToBase64String(SHA256.HashData(secret));

    /// <summary>Compares a presented secret with a stored hash in constant time.</summary>
    public static bool SecretMatches(byte[] secret, string storedHash)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        if (!Convert.TryFromBase64String(storedHash ?? string.Empty, expected, out var written) || written != SHA256.HashSizeInBytes)
            return false;

        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(secret), expected);
    }
}
