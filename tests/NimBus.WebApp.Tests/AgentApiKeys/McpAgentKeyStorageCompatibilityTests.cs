#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Tests.AgentApiKeys;

/// <summary>
/// Spec 052 §7: records saved before MCP keys existed read back unchanged. A stored key without a
/// use is an Agent API key, and a stored policy without the switch keeps keys off.
/// </summary>
[TestClass]
public sealed class McpAgentKeyStorageCompatibilityTests
{
    [TestMethod]
    public void A_key_stored_before_Spec_052_is_an_agent_api_key_without_a_grant()
    {
        const string Stored = """
            {"id":"AgentApiKeys","Revision":"r1","Keys":[{"KeyId":"abcdefghijkl","AgentId":"enrichment-agent","SecretHash":"x","CreatedAtUtc":"2026-10-01T00:00:00Z"}]}
            """;

        var settings = JsonConvert.DeserializeObject<AgentApiKeySettings>(Stored)!;

        var key = settings.Keys.Single();
        Assert.AreEqual(AgentApiKeyUse.AgentApi, key.Use);
        Assert.IsNull(key.Mcp);
        Assert.IsNull(key.Replaces);
    }

    [TestMethod]
    public void A_policy_stored_before_Spec_052_keeps_agent_keys_off()
    {
        const string Stored = """
            {"id":"McpAccessSettings","Revision":"r1","Enabled":true,"AllowWorkloads":true,"AllowWorkloadResubmit":true}
            """;

        var settings = JsonConvert.DeserializeObject<McpAccessSettings>(Stored)!;

        Assert.IsFalse(settings.AllowAgentKeys);
    }

    [TestMethod]
    public void The_use_is_stored_as_a_name()
    {
        var json = JsonConvert.SerializeObject(new AgentApiKey { KeyId = "k", Use = AgentApiKeyUse.Mcp, Mcp = new AgentApiKeyMcpGrant() });

        StringAssert.Contains(json, "\"Use\":\"Mcp\"", StringComparison.Ordinal);
        Assert.AreEqual(AgentApiKeyUse.Mcp, JsonConvert.DeserializeObject<AgentApiKey>(json)!.Use);
    }
}
