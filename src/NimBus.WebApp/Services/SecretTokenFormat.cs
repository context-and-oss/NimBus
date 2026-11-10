using System.Buffers.Text;
using System.Security.Cryptography;

namespace NimBus.WebApp.Services;

/// <summary>A newly issued credential: the full value is shown once and never stored.</summary>
/// <param name="Id">The public lookup id.</param>
/// <param name="Value">The full credential, <c>&lt;prefix&gt;&lt;id&gt;_&lt;secret&gt;</c>.</param>
/// <param name="SecretHash">What the store keeps: the Base64 SHA-256 of the secret.</param>
internal sealed record GeneratedSecretToken(string Id, string Value, string SecretHash);

/// <summary>
/// The shared shape of NimBus-issued credentials (agent API keys, dashboard tokens):
/// <c>&lt;prefix&gt;&lt;id&gt;_&lt;secret&gt;</c>, where the id is 12 lowercase base32 characters used for
/// lookup and the secret is 32 random bytes, base64url-encoded. Only the secret's SHA-256 is
/// stored; with 256 bits of entropy a slow key-derivation function adds nothing.
/// </summary>
internal static class SecretTokenFormat
{
    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private const int IdLength = 12;
    private const int SecretBytes = 32;
    private const int SecretChars = 43; // base64url without padding for 32 bytes

    /// <summary>Issues a new credential with <paramref name="prefix"/>.</summary>
    public static GeneratedSecretToken Generate(string prefix)
    {
        var id = string.Create(IdLength, 0, static (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++)
                chars[i] = IdAlphabet[RandomNumberGenerator.GetInt32(IdAlphabet.Length)];
        });
        var secret = RandomNumberGenerator.GetBytes(SecretBytes);
        return new GeneratedSecretToken(id, prefix + id + "_" + Base64Url.EncodeToString(secret), HashSecret(secret));
    }

    /// <summary>Splits a presented credential into its id and secret; false for anything malformed.</summary>
    public static bool TryParse(string prefix, string? value, out string id, out byte[] secret)
    {
        id = string.Empty;
        secret = [];
        if (value is null || value.Length != prefix.Length + IdLength + 1 + SecretChars
            || !value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var idSpan = value.AsSpan(prefix.Length, IdLength);
        foreach (var c in idSpan)
        {
            if (!IdAlphabet.Contains(c, StringComparison.Ordinal))
                return false;
        }

        if (value[prefix.Length + IdLength] != '_')
            return false;

        var encoded = value.AsSpan(prefix.Length + IdLength + 1);
        if (!Base64Url.IsValid(encoded, out var decodedLength) || decodedLength != SecretBytes)
            return false;

        secret = Base64Url.DecodeFromChars(encoded);
        id = idSpan.ToString();
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
