#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.Extensions.IntegrationIntelligence.Evidence;
using NimBus.Extensions.IntegrationIntelligence.Storage;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using Event = NimBus.WebApp.ManagementApi.Event;
using EventDetails = NimBus.WebApp.ManagementApi.EventDetails;
using IEventApiController = NimBus.WebApp.ManagementApi.IEventApiController;
using JsonSerializer = System.Text.Json.JsonSerializer;
using SearchResponse = NimBus.WebApp.ManagementApi.SearchResponse;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 044 Phase 1 over the real MCP pipeline: an agent reads the evidence, records its answers
/// and finds what is classified, through the real recorder with no provider configured.
/// </summary>
[TestClass]
public class AgentClassificationToolsTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string EventId = "evt-1";
    private const string Attempt = "err-1";
    private const string Session = "session-1";
    private const string Sensitive = "Jane Customer";

    [TestMethod]
    public async Task Evidence_carries_the_question_set_the_sender_and_the_attempt_without_sensitive_values()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess());
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var evidence = await CallAsync(client, "nimbus_get_failure_evidence", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });

        Assert.AreEqual(Attempt, evidence.GetProperty("messageId").GetString());
        Assert.AreEqual("PartnerPortal", evidence.GetProperty("sourceEndpoint").GetString());
        Assert.AreEqual(FailureClassificationQuestionSet.Version, evidence.GetProperty("questionSet").GetProperty("version").GetInt32());
        var questions = evidence.GetProperty("questionSet").GetProperty("questions").EnumerateArray().ToList();
        var origin = questions.Single(q => q.GetProperty("id").GetString() == "fault_origin");
        Assert.AreEqual("faultOrigin and faultOriginConfidence", origin.GetProperty("answer").GetString());
        CollectionAssert.Contains(origin.GetProperty("choices").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList(), "adapter_code");
        Assert.IsFalse(evidence.GetProperty("payloadIncluded").GetBoolean(), "Agents see payloads only when the Owner allows it.");
        Assert.DoesNotContain(Sensitive, evidence.GetRawText());
        StringAssert.Contains(evidence.GetProperty("error").GetProperty("message").GetString(), "has no address", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Evidence_refuses_a_message_that_is_no_longer_failed()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess());
        await SeedFailureAsync(host, ResolutionStatus.Completed);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_get_failure_evidence", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["messageId"] = Attempt,
        });

        AssertError(result, "ActionNotAllowed");
    }

    [TestMethod]
    public async Task Recording_stores_an_agent_result_that_the_read_tools_show_with_its_provenance()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var recorded = await CallAsync(client, "nimbus_record_classification", Answers());
        var read = await CallAsync(client, "nimbus_get_classification", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        var found = await CallAsync(client, "nimbus_find_messages", new() { ["endpointId"] = Endpoint, ["includeClassification"] = true });

        Assert.IsFalse(recorded.GetProperty("existing").GetBoolean());
        var classification = recorded.GetProperty("classification");
        Assert.AreEqual("Agent", classification.GetProperty("provider").GetString());
        Assert.AreEqual("ChangeLikelyRequired", classification.GetProperty("guidance").GetString(), "NimBus composes the guidance.");
        Assert.AreEqual("source_data", classification.GetProperty("faultOrigin").GetString());
        Assert.AreEqual("test-model", classification.GetProperty("model").GetString());
        Assert.DoesNotContain(Sensitive, classification.GetProperty("rationale").GetString()!);

        Assert.AreEqual("Agent", read.GetProperty("classification").GetProperty("provider").GetString());
        Assert.AreEqual("available", found.GetProperty("classificationStatus").GetString());
        var row = found.GetProperty("messages").EnumerateArray().Single();
        Assert.IsTrue(row.GetProperty("classification").GetProperty("saved").GetBoolean());
        Assert.AreEqual("Agent", row.GetProperty("classification").GetProperty("provider").GetString());
        Assert.AreEqual("source_data", row.GetProperty("classification").GetProperty("faultOrigin").GetString());

        var audit = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Type == MessageAuditType.FailureClassified);
        StringAssert.Contains(audit.Data, "\"provider\":\"Agent\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Find_messages_marks_unclassified_failures_as_not_saved()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess());
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var found = await CallAsync(client, "nimbus_find_messages", new() { ["endpointId"] = Endpoint, ["includeClassification"] = true });
        var plain = await CallAsync(client, "nimbus_find_messages", new() { ["endpointId"] = Endpoint });

        Assert.IsFalse(found.GetProperty("messages")[0].GetProperty("classification").GetProperty("saved").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, plain.GetProperty("classificationStatus").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, plain.GetProperty("messages")[0].GetProperty("classification").ValueKind);
    }

    [TestMethod]
    public async Task Find_messages_reports_unreadable_classification_storage_instead_of_guessing()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess(), store: new UnreadableStore());
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var found = await CallAsync(client, "nimbus_find_messages", new() { ["endpointId"] = Endpoint, ["includeClassification"] = true });

        Assert.AreEqual("unavailable", found.GetProperty("classificationStatus").GetString());
        Assert.AreEqual(JsonValueKind.Null, found.GetProperty("messages")[0].GetProperty("classification").ValueKind);
    }

    [TestMethod]
    public async Task Recording_does_not_take_a_change_permit()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        // The change limit is 5 per window; recordings are not changes to a message.
        for (var i = 0; i < 7; i++)
        {
            var arguments = Answers();
            arguments["supersede"] = true;
            await CallAsync(client, "nimbus_record_classification", arguments);
        }

        var latest = await CallAsync(client, "nimbus_get_classification", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        Assert.AreEqual(7, latest.GetProperty("classification").GetProperty("revision").GetInt32());
    }

    [TestMethod]
    public async Task Without_supersede_an_existing_classification_is_returned_and_nothing_is_written()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        await CallAsync(client, "nimbus_record_classification", Answers());

        var again = Answers();
        again["category"] = "unknown";
        var second = await CallAsync(client, "nimbus_record_classification", again);

        Assert.IsTrue(second.GetProperty("existing").GetBoolean());
        Assert.AreEqual("business_rule", second.GetProperty("classification").GetProperty("category").GetString());
        Assert.AreEqual(1, second.GetProperty("classification").GetProperty("revision").GetInt32());
    }

    [TestMethod]
    [DataRow("contributor", "PermissionDenied")]
    [DataRow("other-endpoint", "MessageNotFound")]
    [DataRow("question-set", "InvalidArgument")]
    [DataRow("category", "InvalidArgument")]
    [DataRow("number", "InvalidArgument")]
    [DataRow("not-failed", "ActionNotAllowed")]
    [DataRow("hidden-endpoint", "EndpointNotFound")]
    public async Task Recording_refusals_store_nothing(string refusal, string code)
    {
        var store = new MemoryStore();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = refusal != "contributor" }, store: store,
            policy: refusal == "hidden-endpoint" ? p => { p.Endpoints.Visibility = McpEndpointVisibility.AllExcept; p.Endpoints.Hidden = [Endpoint]; } : null);
        await SeedFailureAsync(host, refusal == "not-failed" ? ResolutionStatus.Completed : ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();
        var arguments = Answers();
        switch (refusal)
        {
            case "other-endpoint": arguments["endpointId"] = "ErpEndpoint"; break;
            case "question-set": arguments["questionSetVersion"] = 1; break;
            case "category": arguments["category"] = "made_up"; break;
            case "number": arguments["retryLikelihood"] = 1.5; break;
        }

        var result = await client.CallToolAsync("nimbus_record_classification", arguments);

        AssertError(result, code);
        Assert.IsNull(await store.GetLatestOperationAsync(Attempt));
    }

    [TestMethod]
    public async Task A_switched_off_classify_capability_hides_and_refuses_recording()
    {
        var store = new MemoryStore();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, store: store, policy: p => p.Capabilities.Classify = false);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();
        var result = await client.CallToolAsync("nimbus_record_classification", Answers());

        CollectionAssert.DoesNotContain(tools, "nimbus_record_classification");
        CollectionAssert.Contains(tools, "nimbus_get_failure_evidence");
        AssertError(result, "PermissionDenied");
        Assert.IsNull(await store.GetLatestOperationAsync(Attempt));
    }

    [TestMethod]
    public async Task Capabilities_report_recording_and_permit_classify_without_a_provider()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await using var client = await host.CreateClientAsync();

        var capabilities = await CallAsync(client, "nimbus_get_capabilities", new());

        Assert.IsTrue(capabilities.GetProperty("features").GetProperty("canRecordClassifications").GetBoolean());
        Assert.IsFalse(capabilities.GetProperty("features").GetProperty("canAnalyzeWithProvider").GetBoolean());
        CollectionAssert.Contains(capabilities.GetProperty("permittedActions").EnumerateArray().Select(e => e.GetString()).ToList(), "classify");
    }

    [TestMethod]
    public async Task Without_a_recorder_the_evidence_and_record_tools_are_unavailable()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, recorder: false);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var evidence = await client.CallToolAsync("nimbus_get_failure_evidence", new Dictionary<string, object?> { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        var record = await client.CallToolAsync("nimbus_record_classification", Answers());

        AssertError(evidence, "FeatureUnavailable");
        AssertError(record, "FeatureUnavailable");
    }

    [TestMethod]
    [DataRow("nimbus.observe nimbus.classify", null, true, true, DisplayName = "delegated scope")]
    [DataRow(null, "Nimbus.Observe Nimbus.Classify", true, true, DisplayName = "workload role, switch on")]
    [DataRow(null, "Nimbus.Observe Nimbus.Classify", false, false, DisplayName = "workload role, switch off")]
    [DataRow(null, "Nimbus.Observe", true, false, DisplayName = "workload without the role")]
    [DataRow("nimbus.observe", "Nimbus.Classify", true, false, DisplayName = "delegated caller with only the app role")]
    public async Task Recording_needs_the_classify_scope_or_the_workload_role_with_the_switch_on(
        string? scopes, string? roles, bool workloadSwitch, bool allowed)
    {
        var store = new MemoryStore();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, store: store, entra: true,
            policy: p => p.AllowWorkloadClassification = workloadSwitch);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: scopes, roles: roles));

        var result = await client.CallToolAsync("nimbus_record_classification", Answers());

        if (allowed)
        {
            Assert.IsFalse(result.IsError == true, ErrorText(result));
            var stored = await store.GetLatestAsync(Attempt);
            Assert.AreEqual(McpTestHost.AgentClientId, stored!.ClientId);
        }
        else
        {
            AssertError(result, "PermissionDenied");
            Assert.IsNull(await store.GetLatestOperationAsync(Attempt));
        }
    }

    [TestMethod]
    public async Task A_workload_role_never_admits_the_deprecated_provider_analysis()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => p.AllowWorkloadClassification = true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: "Nimbus.Observe Nimbus.Classify"));

        var result = await client.CallToolAsync("nimbus_classify_failure", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });

        AssertError(result, "PermissionDenied");
        StringAssert.Contains(ErrorText(result), "can only record classifications", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_workload_with_the_role_sees_classify_in_its_permitted_actions_only_when_the_switch_is_on()
    {
        foreach (var on in new[] { true, false })
        {
            await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
                policy: p => p.AllowWorkloadClassification = on);
            await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: "Nimbus.Observe Nimbus.Classify"));

            var capabilities = await CallAsync(client, "nimbus_get_capabilities", new());

            var permitted = capabilities.GetProperty("permittedActions").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.AreEqual(on, permitted.Contains("classify"));
            CollectionAssert.DoesNotContain(permitted, "resubmit");
        }
    }

    [TestMethod]
    public void Turning_on_workload_recording_is_reviewed_as_a_widening_change()
    {
        var on = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings(), new McpAccessSettings { AllowWorkloadClassification = true });
        var off = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings { AllowWorkloadClassification = true }, new McpAccessSettings());

        Assert.AreEqual("Let workloads record failure classifications", on.Single().Text);
        Assert.IsTrue(on.Single().Widens);
        Assert.IsFalse(off.Single().Widens);
        Assert.IsFalse(new McpAccessSettings().AllowWorkloadClassification);
    }

    private static Dictionary<string, object?> Answers() => new()
    {
        ["endpointId"] = Endpoint,
        ["eventId"] = EventId,
        ["messageId"] = Attempt,
        ["idempotencyKey"] = Guid.NewGuid().ToString(),
        ["questionSetVersion"] = FailureClassificationQuestionSet.Version,
        ["category"] = "business_rule",
        ["categoryConfidence"] = 0.9,
        ["retryLikelihood"] = 0.1,
        ["changeRequiredLikelihood"] = 0.9,
        ["externalDependencyLikelihood"] = 0.1,
        ["faultOrigin"] = "source_data",
        ["faultOriginConfidence"] = 0.8,
        ["model"] = "test-model",
        ["rationale"] = $"Customer {Sensitive} has no address; the address field is required.",
    };

    private static Task<McpTestHost> StartAsync(McpTestHost.StubAccess access, IFailureClassificationStore? store = null, bool recorder = true,
        Action<McpAccessSettings>? policy = null, bool entra = false)
    {
        var messages = new InMemoryMessageStore();
        var events = InterfaceFake<IEventApiController>.Create();
        events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args => SearchAsync(messages, (string)args[1]!));
        events.On(nameof(IEventApiController.GetEventDetailsIdAsync),
            _ => Task.FromResult<ActionResult<EventDetails>>(new OkObjectResult(new EventDetails())));
        store ??= new MemoryStore();
        if (policy is not null)
        {
            var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
            policy(settings);
            Assert.IsTrue(messages.TrySetMcpAccessSettings(settings, null).GetAwaiter().GetResult());
        }

        void Configure(IServiceCollection services)
        {
            var options = new FailureClassificationOptions { Enabled = true, IntelligenceEnabled = true, Provider = FailureClassificationOptions.NoProvider };
            services.AddSingleton(access);
            services.AddSingleton(messages);
            services.AddSingleton<IMessageTrackingStore>(messages);
            services.AddSingleton<IEndpointMetadataStore>(messages);
            services.AddSingleton(events.Instance);
            services.AddSingleton(options);
            services.AddSingleton<IIntegrationIntelligenceHost, AllowingHost>();
            services.AddSingleton(store);
            services.AddScoped(sp => new FailureClassificationReader(sp.GetRequiredService<IIntegrationIntelligenceHost>(), store, messages));
            if (recorder)
            {
                services.AddScoped(sp => new FailureClassificationRecorder(options, sp.GetRequiredService<IIntegrationIntelligenceHost>(), store,
                    new FailureEvidenceBuilder(messages, new FixedMasker(), null, new IntelligenceDataRedactor(options), options), messages));
            }
        }

        return entra ? McpTestHost.StartEntraAsync(Configure) : McpTestHost.StartLocalDevelopmentAsync(Configure);
    }

    private static async Task<ActionResult<SearchResponse>> SearchAsync(InMemoryMessageStore store, string endpointId)
    {
        var row = await store.GetEvent(endpointId, EventId);
        var events = row is null
            ? new List<Event>()
            : [new Event { EventId = row.EventId, ResolutionStatus = row.ResolutionStatus.ToString(), LastMessageId = row.LastMessageId, SessionId = row.SessionId }];
        return new OkObjectResult(new SearchResponse { Events = events });
    }

    private static async Task SeedFailureAsync(McpTestHost host, ResolutionStatus status = ResolutionStatus.Failed)
    {
        var store = host.Get<InMemoryMessageStore>();
        await store.StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = Attempt,
            EndpointId = Endpoint,
            SessionId = Session,
            MessageType = MessageType.ErrorResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            EventTypeId = "CustomerChanged",
            From = "PartnerPortal",
            To = Endpoint,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = $"{{\"name\":\"{Sensitive}\"}}", EventTypeId = "CustomerChanged" },
                ErrorContent = new ErrorContent { ErrorType = "BusinessRuleException", ErrorText = $"Customer {Sensitive} has no address." },
            },
        });
        var row = await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, EventId, Session, Attempt, status);
        Assert.IsNotNull(row);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError == true, "Expected an error result.");
        StringAssert.Contains(ErrorText(result), $"[{code}]", StringComparison.Ordinal);
    }

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    /// <summary>The ACL and audit boundary, answering from the stub access the MCP host also uses.</summary>
    private sealed class AllowingHost(McpTestHost.StubAccess access, McpTestHost.RecordingAuditLog audit) : IIntegrationIntelligenceHost
    {
        public string? CurrentActor => "Agent Operator";
        public Task<bool> EndpointExistsAsync(string endpointId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> HasReaderAsync(string endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult(McpTestHost.ReadableEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase));
        public Task<bool> HasContributorAsync(string endpointId, CancellationToken cancellationToken = default)
            => Task.FromResult(access.Contributor && McpTestHost.ReadableEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase));
        public Task AuditAsync(MessageAuditType type, string? eventId, string? endpointId, string data, bool accessDenied, CancellationToken cancellationToken = default)
            => audit.LogAuditAsync(type, new Microsoft.AspNetCore.Http.DefaultHttpContext(), accessDenied, data, eventId, endpointId, cancellationToken: cancellationToken);
    }

    /// <summary>Marks the customer name sensitive, as event metadata would.</summary>
    private sealed class FixedMasker : IEventJsonMasker
    {
        public string Mask(string eventTypeId, string eventJson) => eventJson;
        public bool ContainsRedactPlaceholder(string eventTypeId, string eventJson) => false;
        public string StripMaskedMarker(string eventJson) => eventJson;
        public bool TryCollectSensitiveValues(string eventTypeId, string eventJson, out IReadOnlyCollection<string> values)
        {
            values = [Sensitive];
            return true;
        }
    }

    /// <summary>An in-memory classification store over the shipped atomic store logic.</summary>
    private class MemoryStore : AtomicClassificationStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (string Json, int Version)> _documents = new(StringComparer.Ordinal);

        protected override Task<(ClassificationDocument Document, string? Version)> ReadAsync(string failureId, CancellationToken ct)
        {
            lock (_gate)
            {
                return Task.FromResult(_documents.TryGetValue(failureId, out var stored)
                    ? (JsonConvert.DeserializeObject<ClassificationDocument>(stored.Json)!, (string?)stored.Version.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    : (new ClassificationDocument { FailureMessageId = failureId }, (string?)null));
            }
        }

        protected override Task<bool> TryWriteAsync(ClassificationDocument document, string? version, CancellationToken ct)
        {
            lock (_gate)
            {
                _documents.TryGetValue(document.FailureMessageId, out var stored);
                var current = stored.Version == 0 ? null : stored.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (current != version) return Task.FromResult(false);
                _documents[document.FailureMessageId] = (JsonConvert.SerializeObject(document), stored.Version + 1);
                return Task.FromResult(true);
            }
        }

        protected override async IAsyncEnumerable<ClassificationDocument> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>A store whose bulk read fails, as a missing table or container does.</summary>
    private sealed class UnreadableStore : MemoryStore
    {
        protected override Task<IReadOnlyList<ClassificationDocument>> ReadManyAsync(IReadOnlyList<string> failureIds, CancellationToken ct)
            => throw new InvalidOperationException("The container does not exist.");
    }
}
