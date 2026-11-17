#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

/// <summary>v5.0.0 removes the built-in provider (Spec 044 §6.10) from the WebApp, its settings and MCP.</summary>
[TestClass]
public sealed class ProviderRemovalTests
{
    private const string SettingsJson = """
        {"enabled":true,"includeEventPayload":false,"includeRecentFailureHistory":true,"maximumHistoryItems":5,
         "additionalRedactedKeys":[],"allowedEndpoints":[],"minimumCategoryConfidence":0.6,"retryLikely":0.75,"changeRequired":0.75
        """;

    [TestMethod]
    public void The_Mcp_Server_Has_No_Classify_Failure_Tool()
    {
        var tools = typeof(Startup).Assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .ToArray();

        CollectionAssert.Contains(tools, "nimbus_record_classification");
        CollectionAssert.DoesNotContain(tools, "nimbus_classify_failure");
    }

    [TestMethod]
    public async Task A_Settings_Record_Saved_With_Provider_Fields_Still_Loads_And_Applies_No_Provider_Keys()
    {
        // Records saved by 4.x carry model, timeoutSeconds and a sealed key; they must not fail startup.
        var saved = """{"Settings":""" + SettingsJson + ""","model":"jev-1.13.0","timeoutSeconds":20},"Revision":"8f3c2a64-0d0e-4b8e-9c55-2f1f7e3c1a10","ProtectedApiKey":"CfDJ8-sealed"}""";
        var store = new JsonSettingsStore(saved);

        var effective = await IntelligenceSettingsBootstrap.LoadAsync(new ConfigurationBuilder().Build(), store, CancellationToken.None);

        Assert.AreEqual("8f3c2a64-0d0e-4b8e-9c55-2f1f7e3c1a10", effective[IntelligenceSettingsBootstrap.RevisionKey]);
        Assert.AreEqual("True", effective["NimBus:IntegrationIntelligence:Enabled"]);
        Assert.IsFalse(effective.AsEnumerable().Any(pair => pair.Key.Contains("TypeSafe", StringComparison.OrdinalIgnoreCase)
            || pair.Key.EndsWith(":TimeoutSeconds", StringComparison.OrdinalIgnoreCase)), "No provider setting is applied.");
    }

    [TestMethod]
    public void The_Settings_Save_Accepts_Settings_Without_Provider_Fields_And_Rejects_An_Api_Key()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var body = """{"settings":""" + SettingsJson + """},"revision":"none","payloadSharingAcknowledged":false""";

        Assert.IsNotNull(System.Text.Json.JsonSerializer.Deserialize<SaveIntelligenceSettings>(body + "}", options)?.Settings);
        Assert.ThrowsExactly<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<SaveIntelligenceSettings>(body + ""","apiKey":"secret"}""", options));
    }

    private sealed class JsonSettingsStore(string json) : IIntelligenceSettingsStore
    {
        public Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken)
            => Task.FromResult(JsonConvert.DeserializeObject<IntelligenceSettingsDocument>(json));

        public Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken)
            => Task.FromResult(false);
    }
}
