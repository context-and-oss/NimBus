#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

[TestClass]
public class AgentApiKeyFormatTests
{
    [TestMethod]
    public void Generated_key_parses_back_to_its_id_and_matches_its_hash()
    {
        var generated = AgentApiKeyFormat.Generate();

        StringAssert.StartsWith(generated.Key, "nbak_");
        Assert.AreEqual(12, generated.KeyId.Length);
        Assert.IsTrue(generated.KeyId.All(c => "abcdefghijklmnopqrstuvwxyz234567".Contains(c)));

        Assert.IsTrue(AgentApiKeyFormat.TryParse(generated.Key, out var keyId, out var secret));
        Assert.AreEqual(generated.KeyId, keyId);
        Assert.AreEqual(32, secret.Length);
        Assert.IsTrue(AgentApiKeyFormat.SecretMatches(secret, generated.SecretHash));
    }

    [TestMethod]
    public void Generated_keys_are_unique()
    {
        var first = AgentApiKeyFormat.Generate();
        var second = AgentApiKeyFormat.Generate();

        Assert.AreNotEqual(first.Key, second.Key);
        Assert.AreNotEqual(first.KeyId, second.KeyId);
        Assert.AreNotEqual(first.SecretHash, second.SecretHash);
    }

    [TestMethod]
    public void Hash_is_over_the_secret_only()
    {
        var generated = AgentApiKeyFormat.Generate();
        Assert.IsTrue(AgentApiKeyFormat.TryParse(generated.Key, out _, out var secret));

        Assert.AreEqual(AgentApiKeyFormat.HashSecret(secret), generated.SecretHash);
        Assert.IsFalse(generated.SecretHash.Contains(generated.KeyId, StringComparison.Ordinal));
    }

    [TestMethod]
    public void A_different_secret_does_not_match()
    {
        var generated = AgentApiKeyFormat.Generate();
        var other = AgentApiKeyFormat.Generate();
        Assert.IsTrue(AgentApiKeyFormat.TryParse(other.Key, out _, out var otherSecret));

        Assert.IsFalse(AgentApiKeyFormat.SecretMatches(otherSecret, generated.SecretHash));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-a-hash")]
    [DataRow("AAAA")]
    public void A_malformed_stored_hash_never_matches(string storedHash)
    {
        var generated = AgentApiKeyFormat.Generate();
        Assert.IsTrue(AgentApiKeyFormat.TryParse(generated.Key, out _, out var secret));

        Assert.IsFalse(AgentApiKeyFormat.SecretMatches(secret, storedHash));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("nbak_")]
    [DataRow("nbxk_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("nbak_abcdefghijk_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("nbak_ABCDEFGHIJKL_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("nbak_abcdefghijk1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("nbak_abcdefghijklXAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow("nbak_abcdefghijkl_AAAA")]
    [DataRow("nbak_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA*")]
    [DataRow("nbak_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [DataRow(" nbak_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_keys_are_rejected(string? value)
    {
        Assert.IsFalse(AgentApiKeyFormat.TryParse(value, out _, out _));
    }

    [TestMethod]
    public void A_well_formed_key_parses()
    {
        // 43 base64url characters encode exactly 32 bytes.
        Assert.IsTrue(AgentApiKeyFormat.TryParse(
            "nbak_abcdefghijkl_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", out var keyId, out var secret));
        Assert.AreEqual("abcdefghijkl", keyId);
        Assert.AreEqual(32, secret.Length);
    }
}
