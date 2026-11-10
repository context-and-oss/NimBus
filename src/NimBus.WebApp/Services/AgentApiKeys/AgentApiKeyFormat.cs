namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>A newly issued key: the full value is shown to the Owner once and never stored.</summary>
/// <param name="KeyId">The public lookup id.</param>
/// <param name="Key">The full key, <c>nbak_&lt;keyId&gt;_&lt;secret&gt;</c>.</param>
/// <param name="SecretHash">What the store keeps: the Base64 SHA-256 of the secret.</param>
internal sealed record GeneratedAgentApiKey(string KeyId, string Key, string SecretHash);

/// <summary>
/// Agent API key material (Spec 039): <c>nbak_&lt;keyId&gt;_&lt;secret&gt;</c>, in the shared
/// <see cref="SecretTokenFormat"/>: a 12-character base32 key id for lookup and a 32-byte secret,
/// base64url-encoded, of which only the SHA-256 is stored.
/// </summary>
internal static class AgentApiKeyFormat
{
    /// <summary>Marks the value as a NimBus agent key, also for secret scanners.</summary>
    public const string Prefix = "nbak_";

    /// <summary>Issues a new key.</summary>
    public static GeneratedAgentApiKey Generate()
    {
        var generated = SecretTokenFormat.Generate(Prefix);
        return new GeneratedAgentApiKey(generated.Id, generated.Value, generated.SecretHash);
    }

    /// <summary>Splits a presented key into its id and secret; false for anything malformed.</summary>
    public static bool TryParse(string? value, out string keyId, out byte[] secret)
        => SecretTokenFormat.TryParse(Prefix, value, out keyId, out secret);

    /// <summary>The stored form of a secret: its Base64 SHA-256.</summary>
    public static string HashSecret(ReadOnlySpan<byte> secret) => SecretTokenFormat.HashSecret(secret);

    /// <summary>Compares a presented secret with a stored hash in constant time.</summary>
    public static bool SecretMatches(byte[] secret, string storedHash) => SecretTokenFormat.SecretMatches(secret, storedHash);
}
