#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Core.Messages;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using Event = NimBus.WebApp.ManagementApi.Event;
using EventDetails = NimBus.WebApp.ManagementApi.EventDetails;
using IEndpointApiController = NimBus.WebApp.ManagementApi.IEndpointApiController;
using IEventApiController = NimBus.WebApp.ManagementApi.IEventApiController;
using SearchResponse = NimBus.WebApp.ManagementApi.SearchResponse;
using SessionStatus = NimBus.WebApp.ManagementApi.SessionStatus;
using NimBus.WebApp.Mcp.Operations;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 035 Phase 2a write tools over the real MCP pipeline: preview with
/// <c>nimbus_prepare_action</c>, run with the returned token, and the guards that make a
/// token single-use, caller-bound and state-bound.
/// </summary>
[TestClass]
public class OperatorActionToolsTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string EventId = "evt-mcp-1";
    private const string Session = "sess-1";
    private const string Attempt = "err-1";

    [TestMethod]
    public async Task Resubmit_runs_once_from_a_prepared_token_and_is_audited()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var message = await CallAsync(client, "nimbus_get_message", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        CollectionAssert.AreEqual(new[] { "resubmit", "skip", "report" }, Strings(message.GetProperty("eligibleActions")));
        var version = message.GetProperty("messageVersion").GetString();

        var prepared = await CallAsync(client, "nimbus_prepare_action", new()
        {
            ["action"] = "resubmit", ["endpointId"] = Endpoint, ["eventId"] = EventId, ["messageVersion"] = version,
        });
        Assert.AreEqual("Failed", prepared.GetProperty("expectedStatus").GetString());
        Assert.AreEqual(120, prepared.GetProperty("expiresInSeconds").GetInt32());
        Assert.AreEqual(1, prepared.GetProperty("deferredInSession").GetInt32());
        var token = prepared.GetProperty("actionToken").GetString();

        var executed = await CallAsync(client, "nimbus_resubmit_message", Execute(token, "Downstream fixed"));
        Assert.IsTrue(executed.GetProperty("commandSent").GetBoolean());

        CollectionAssert.AreEqual(new[] { $"resubmit:{Endpoint}:{EventId}" }, host.Get<McpTestHost.RecordingManagerClient>().Sent.ToArray());
        var audit = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Required);
        Assert.AreEqual(MessageAuditType.Resubmit, audit.Type);
        StringAssert.Contains(audit.Data, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"reason\":\"Downstream fixed\"", StringComparison.Ordinal);

        // The row moved on, so the same token can never send a second command.
        var replay = await client.CallToolAsync("nimbus_resubmit_message", Execute(token, "Again"));
        AssertError(replay, "StaleMessage");
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_message_that_changed_after_preparing_is_stale_and_nothing_is_sent()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareAsync(client, "skip");

        // The endpoint fails again before the agent runs the skip.
        await OperatorCommandTestRows.SeedFailedRowAsync(host.Get<InMemoryMessageStore>(), Endpoint, EventId, Session, "err-2");

        var result = await client.CallToolAsync("nimbus_skip_message", Execute(token, "Skip it"));

        AssertError(result, "StaleMessage");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_token_is_bound_to_its_action()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareAsync(client, "resubmit");

        var result = await client.CallToolAsync("nimbus_skip_message", Execute(token, "Wrong tool"));

        AssertError(result, "StaleMessage");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task An_expired_or_forged_token_is_refused()
    {
        var clock = new MutableTime();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true },
            extra: services => services.AddSingleton<TimeProvider>(clock));
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareAsync(client, "resubmit");

        AssertError(await client.CallToolAsync("nimbus_resubmit_message", Execute(token + "x", "Forged")), "StaleMessage");
        clock.Advance(TimeSpan.FromMinutes(3));
        AssertError(await client.CallToolAsync("nimbus_resubmit_message", Execute(token, "Too late")), "StaleMessage");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_reader_cannot_prepare_or_see_actions()
    {
        await using var host = await StartLocalAsync(contributor: false);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var message = await CallAsync(client, "nimbus_get_message", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        Assert.AreEqual(0, message.GetProperty("eligibleActions").GetArrayLength());
        var capabilities = await CallAsync(client, "nimbus_get_capabilities", new());
        Assert.AreEqual(0, capabilities.GetProperty("permittedActions").GetArrayLength());

        var result = await client.CallToolAsync("nimbus_prepare_action", new Dictionary<string, object?>
        {
            ["action"] = "resubmit", ["endpointId"] = Endpoint, ["eventId"] = EventId,
            ["messageVersion"] = message.GetProperty("messageVersion").GetString(),
        });
        AssertError(result, "PermissionDenied");
    }

    [TestMethod]
    public async Task A_role_revoked_after_preparing_is_refused_by_the_fresh_check()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true, ContributorFresh = false });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareAsync(client, "resubmit");

        var result = await client.CallToolAsync("nimbus_resubmit_message", Execute(token, "Revoked meanwhile"));

        AssertError(result, "PermissionDenied");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
        Assert.IsTrue(host.Get<McpTestHost.RecordingAuditLog>().Rows.Single().Denied);
    }

    [TestMethod]
    public async Task Deferred_messages_cannot_be_resubmitted_through_MCP()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await SeedFailureAsync(host, ResolutionStatus.Deferred);
        await using var client = await host.CreateClientAsync();
        var message = await CallAsync(client, "nimbus_get_message", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });

        var result = await client.CallToolAsync("nimbus_prepare_action", new Dictionary<string, object?>
        {
            ["action"] = "resubmit", ["endpointId"] = Endpoint, ["eventId"] = EventId,
            ["messageVersion"] = message.GetProperty("messageVersion").GetString(),
        });

        AssertError(result, "ActionNotAllowed");
        CollectionAssert.AreEqual(new[] { "report" }, Strings(message.GetProperty("eligibleActions")));
    }

    [TestMethod]
    [DataRow("nimbus.observe", null, DisplayName = "Observe scope only")]
    [DataRow(null, "Nimbus.Observe", DisplayName = "Workload app role")]
    public async Task Entra_callers_need_the_delegated_action_scope(string? scopes, string? roles)
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: scopes, roles: roles));

        var result = await client.CallToolAsync("nimbus_set_message_reported", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["reported"] = true,
            ["reason"] = "Ticket raised", ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });

        AssertError(result, "PermissionDenied");
        StringAssert.Contains(ErrorText(result), "nimbus.annotate", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Entra_tokens_are_bound_to_the_caller_they_were_issued_to()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true);
        await SeedFailureAsync(host);
        await using var alice = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe nimbus.skip"));
        await using var mallory = await host.CreateClientAsync(McpTestHost.CreateToken(
            scopes: "nimbus.observe nimbus.skip", oid: "55555555-5555-5555-5555-555555555555"));
        var token = await PrepareAsync(alice, "skip");

        var stolen = await mallory.CallToolAsync("nimbus_skip_message", Execute(token, "Not mine"));
        AssertError(stolen, "StaleMessage");

        var own = await CallAsync(alice, "nimbus_skip_message", Execute(token, "Business owner approved"));
        Assert.IsTrue(own.GetProperty("commandSent").GetBoolean());
        CollectionAssert.AreEqual(new[] { $"skip:{Endpoint}:{EventId}" }, host.Get<McpTestHost.RecordingManagerClient>().Sent.ToArray());
    }

    [TestMethod]
    public async Task Changes_are_rate_limited_per_caller()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await using var client = await host.CreateClientAsync();

        CallToolResult? last = null;
        for (var i = 0; i < 6; i++)
        {
            last = await client.CallToolAsync("nimbus_set_message_reported", new Dictionary<string, object?>
            {
                ["endpointId"] = Endpoint, ["eventId"] = $"evt-{i}", ["reported"] = true,
                ["reason"] = "Ticket raised", ["idempotencyKey"] = Guid.NewGuid().ToString(), ["ticketId"] = "INC-1",
            });
            if (i < 5)
                Assert.IsFalse(last.IsError == true, ErrorText(last));
        }

        AssertError(last!, "RateLimited");
    }

    [TestMethod]
    public async Task Report_requires_a_reason_and_stores_the_marker()
    {
        await using var host = await StartLocalAsync(contributor: true);
        await using var client = await host.CreateClientAsync();

        var missing = await client.CallToolAsync("nimbus_set_message_reported", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["reported"] = true,
            ["reason"] = " ", ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });
        AssertError(missing, "InvalidArgument");

        var result = await CallAsync(client, "nimbus_set_message_reported", new()
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["reported"] = true,
            ["reason"] = "Ticket raised", ["idempotencyKey"] = Guid.NewGuid().ToString(), ["ticketId"] = "INC-42",
        });

        Assert.IsFalse(result.GetProperty("commandSent").GetBoolean());
        var reports = await host.Get<InMemoryMessageStore>().GetEventReports(Endpoint, new[] { EventId });
        Assert.AreEqual("INC-42", reports[EventId].TicketId);
    }

    [TestMethod]
    public async Task Classify_requests_an_analysis_of_the_latest_attempt()
    {
        var classifications = new FakeClassifications();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true },
            extra: services => services.AddSingleton<IOperatorClassificationSource>(classifications));
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var key = Guid.NewGuid().ToString();

        var result = await CallAsync(client, "nimbus_classify_failure", new()
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["idempotencyKey"] = key,
        });

        Assert.AreEqual((EventId, Attempt, key, false), classifications.LastAnalyze);
        Assert.IsTrue(result.GetProperty("advisory").GetBoolean());
        Assert.AreEqual("Transient", result.GetProperty("classification").GetProperty("category").GetString());
    }

    [TestMethod]
    public async Task Classify_rejects_an_attempt_from_another_endpoint_before_analysis()
    {
        var classifications = new FakeClassifications();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true },
            extra: services => services.AddSingleton<IOperatorClassificationSource>(classifications));
        await SeedFailureAsync(host);
        await host.Get<InMemoryMessageStore>().StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = "err-other",
            EndpointId = "ErpEndpoint",
            SessionId = Session,
            MessageType = MessageType.ErrorResponse,
        });
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_classify_failure", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId,
            ["messageId"] = "err-other", ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });

        AssertError(result, "MessageNotFound");
        Assert.IsNull(classifications.LastAnalyze);
    }

    [TestMethod]
    public async Task Classify_in_read_only_mode_says_no_provider_is_configured_and_does_not_analyze()
    {
        // Spec 043 read-only mode: saved classifications are readable, new analyses are not.
        var classifications = new FakeClassifications { Analyze = false };
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true },
            extra: services => services.AddSingleton<IOperatorClassificationSource>(classifications));
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_classify_failure", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });

        AssertError(result, "FeatureUnavailable");
        StringAssert.Contains(ErrorText(result), "no provider is configured", StringComparison.Ordinal);
        Assert.IsNull(classifications.LastAnalyze);
    }

    [TestMethod]
    public void Classification_source_reads_in_read_only_mode_and_analyzes_only_when_ready()
    {
        var reader = new FailureClassificationReader(InterfaceFake<IIntegrationIntelligenceHost>.Create().Instance,
            InterfaceFake<IFailureClassificationStore>.Create().Instance, new InMemoryMessageStore());

        var readOnly = new OperatorClassificationSource([reader], []);
        Assert.IsTrue(readOnly.IsAvailable);
        Assert.IsFalse(readOnly.CanAnalyze);

        var off = new OperatorClassificationSource([], []);
        Assert.IsFalse(off.IsAvailable);
        Assert.IsFalse(off.CanAnalyze);
    }

    [TestMethod]
    public async Task Classify_rejects_a_revoked_contributor_before_analysis()
    {
        var classifications = new FakeClassifications();
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true, ContributorFresh = false },
            extra: services => services.AddSingleton<IOperatorClassificationSource>(classifications));
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_classify_failure", new Dictionary<string, object?>
        {
            ["endpointId"] = Endpoint, ["eventId"] = EventId, ["idempotencyKey"] = Guid.NewGuid().ToString(),
        });

        AssertError(result, "PermissionDenied");
        Assert.IsNull(classifications.LastAnalyze);
    }

    private static Task<McpTestHost> StartLocalAsync(bool contributor)
        => StartAsync(new McpTestHost.StubAccess { Contributor = contributor });

    // The read tools look messages up through the REST implementation; answer from the same
    // in-memory store the coordinator writes, so reads and commands see one state.
    private static Task<McpTestHost> StartAsync(McpTestHost.StubAccess access, bool entra = false, Action<IServiceCollection>? extra = null)
    {
        var store = new InMemoryMessageStore();
        var events = InterfaceFake<IEventApiController>.Create();
        events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args => SearchAsync(store, (string)args[1]!));
        events.On(nameof(IEventApiController.GetEventDetailsIdAsync),
            _ => Task.FromResult<ActionResult<EventDetails>>(new OkObjectResult(new EventDetails())));
        var endpoints = InterfaceFake<IEndpointApiController>.Create();
        endpoints.On(nameof(IEndpointApiController.GetEndpointSessionIdAsync), _ => Task.FromResult<ActionResult<SessionStatus>>(
            new OkObjectResult(new SessionStatus { SessionId = Session, PendingEvents = [], DeferredEvents = ["evt-later"] })));

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(access);
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton(events.Instance);
            services.AddSingleton(endpoints.Instance);
            extra?.Invoke(services);
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
            From = Endpoint,
            To = "Resolver",
            OriginatingMessageId = "req-1",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = "{\"v\":1}", EventTypeId = "CustomerChanged" },
            },
        });
        await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, EventId, Session, Attempt, status);
    }

    private static async Task<string?> PrepareAsync(McpClient client, string action)
    {
        var message = await CallAsync(client, "nimbus_get_message", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId });
        var prepared = await CallAsync(client, "nimbus_prepare_action", new()
        {
            ["action"] = action, ["endpointId"] = Endpoint, ["eventId"] = EventId,
            ["messageVersion"] = message.GetProperty("messageVersion").GetString(),
        });
        return prepared.GetProperty("actionToken").GetString();
    }

    private static Dictionary<string, object?> Execute(string? token, string reason) => new()
    {
        ["actionToken"] = token, ["idempotencyKey"] = Guid.NewGuid().ToString(), ["reason"] = reason,
    };

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

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToArray();

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeClassifications : IOperatorClassificationSource
    {
        public (string EventId, string MessageId, string Key, bool Force)? LastAnalyze { get; private set; }

        public bool Analyze { get; init; } = true;

        public bool IsAvailable => true;

        public bool CanAnalyze => Analyze;

        public Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
            => Task.FromResult<FailureClassification?>(null);

        public Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
            string idempotencyKey, bool force, CancellationToken cancellationToken)
        {
            LastAnalyze = (eventId, messageId, idempotencyKey, force);
            return Task.FromResult((new FailureClassification
            {
                Id = "c-1",
                FailureMessageId = messageId,
                Revision = 1,
                EventId = eventId,
                EventTypeId = "CustomerChanged",
                EndpointId = endpointId,
                Provider = "test",
                Model = "test-model",
                QuestionSetVersion = 1,
                Category = "Transient",
                CategoryConfidence = 0.9,
                CategoryProbabilities = new Dictionary<string, double> { ["Transient"] = 0.9 },
                RetryLikelihood = 0.8,
                ChangeRequiredLikelihood = 0.1,
                ExternalDependencyLikelihood = 0.6,
                Guidance = FailureGuidance.RetryMayHelp,
                EventPayloadIncluded = false,
                RequestedBy = "Agent Operator",
                CreatedAtUtc = DateTimeOffset.UtcNow,
            }, false));
        }
    }
}
