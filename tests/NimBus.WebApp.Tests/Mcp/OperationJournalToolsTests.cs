#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.Operations;
using Event = NimBus.WebApp.ManagementApi.Event;
using EventDetails = NimBus.WebApp.ManagementApi.EventDetails;
using IEndpointApiController = NimBus.WebApp.ManagementApi.IEndpointApiController;
using IEventApiController = NimBus.WebApp.ManagementApi.IEventApiController;
using SearchResponse = NimBus.WebApp.ManagementApi.SearchResponse;
using SessionStatus = NimBus.WebApp.ManagementApi.SessionStatus;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 044 Phase 2 over the real MCP pipeline: resubmit and skip are journaled operations, a
/// retry with the same idempotency key runs nothing again, <c>nimbus_get_operation</c> reads the
/// outcome from the Resolver's history, and workloads with the <c>Nimbus.Resubmit</c> role
/// resubmit unattended within a budget and a per-message cap.
/// </summary>
[TestClass]
public class OperationJournalToolsTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string EventId = "evt-journal-1";
    private const string Session = "sess-1";
    private const string Attempt = "err-1";
    private const string WorkloadRoles = "Nimbus.Observe Nimbus.Resubmit";

    [TestMethod]
    public async Task A_resubmit_is_an_operation_whose_outcome_comes_from_the_resolver_history()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();

        var executed = await CallAsync(client, "nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), Guid.NewGuid()));
        var operationId = executed.GetProperty("operationId").GetString()!;

        // Published under the operation id, so the response can be correlated.
        CollectionAssert.AreEqual(new[] { operationId }, host.Get<McpTestHost.RecordingManagerClient>().CommandIds.ToArray());
        Assert.AreEqual("Dispatched", executed.GetProperty("operationStatus").GetString());
        Assert.AreEqual("Dispatched", (await GetOperationAsync(client, operationId)).GetProperty("status").GetString());

        // The endpoint answers the command: the Resolver stores a response parented to it.
        await StoreResponseAsync(host, operationId, MessageType.ResolutionResponse);

        var after = await GetOperationAsync(client, operationId);
        Assert.AreEqual("Succeeded", after.GetProperty("status").GetString());
        Assert.AreEqual("ResolutionResponse", after.GetProperty("observedResponse").GetString());
        Assert.IsTrue(after.GetProperty("terminal").GetBoolean());
    }

    [TestMethod]
    public async Task An_error_response_marks_the_operation_failed()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var executed = await CallAsync(client, "nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), Guid.NewGuid()));
        var operationId = executed.GetProperty("operationId").GetString()!;

        await StoreResponseAsync(host, operationId, MessageType.ErrorResponse);

        Assert.AreEqual("Failed", (await GetOperationAsync(client, operationId)).GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Repeating_the_idempotency_key_returns_the_operation_and_sends_nothing_again()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var key = Guid.NewGuid();
        var token = await PrepareAsync(client, "resubmit");

        var first = await CallAsync(client, "nimbus_resubmit_message", Execute(token, key));
        // The message has moved on and the token's state is gone; the journal still recognizes the retry.
        var retry = await CallAsync(client, "nimbus_resubmit_message", Execute("not-a-token", key));

        Assert.AreEqual(first.GetProperty("operationId").GetString(), retry.GetProperty("operationId").GetString());
        Assert.IsTrue(retry.GetProperty("replayed").GetBoolean());
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_key_used_for_a_resubmit_cannot_be_reused_for_a_skip()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var key = Guid.NewGuid();
        await CallAsync(client, "nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), key));

        var reused = await client.CallToolAsync("nimbus_skip_message", Execute("any", key));

        AssertError(reused, "InvalidArgument");
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_failed_send_is_outcome_unknown_and_a_retry_never_publishes_again()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var key = Guid.NewGuid();
        var manager = host.Get<McpTestHost.RecordingManagerClient>();
        manager.FailNextSend = new TimeoutException("broker did not answer");

        var failed = await client.CallToolAsync("nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), key));
        AssertError(failed, "OutcomeUnknown");

        var retry = await CallAsync(client, "nimbus_resubmit_message", Execute("not-a-token", key));
        Assert.AreEqual("OutcomeUnknown", retry.GetProperty("operationStatus").GetString());
        Assert.AreEqual(0, manager.Sent.Count);
        var operation = await GetOperationAsync(client, retry.GetProperty("operationId").GetString()!);
        Assert.IsFalse(operation.GetProperty("safeToResubmitAutomatically").GetBoolean());
    }

    [TestMethod]
    public async Task A_command_the_coordinator_refuses_is_journaled_as_refused()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true, ContributorFresh = false });
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync();
        var key = Guid.NewGuid();

        // The fresh role check fails inside the coordinator, after the operation was created.
        var refused = await client.CallToolAsync("nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), key));
        AssertError(refused, "PermissionDenied");

        var refusedOperations = await host.Get<InMemoryMessageStore>().CountOperatorOperations(new OperatorOperationQuery
        {
            EventId = EventId,
            ExcludedStatuses = Enum.GetValues<OperatorOperationStatus>().Where(s => s != OperatorOperationStatus.Refused).ToList(),
        });
        Assert.AreEqual(1, refusedOperations);
    }

    [TestMethod]
    public async Task Another_tenants_operation_cannot_be_read()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true);
        var store = host.Get<InMemoryMessageStore>();
        await store.TryCreateOperatorOperation(Operation("other-tenant-op".PadRight(32, '0'), o => o.TenantId = "another-tenant"));
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe"));

        var result = await client.CallToolAsync("nimbus_get_operation", new Dictionary<string, object?> { ["operationId"] = "other-tenant-op".PadRight(32, '0') });

        AssertError(result, "InvalidArgument");
    }

    [TestMethod]
    [DataRow(WorkloadRoles, true, true, DisplayName = "workload role, switch on")]
    [DataRow(WorkloadRoles, false, false, DisplayName = "workload role, switch off")]
    [DataRow("Nimbus.Observe", true, false, DisplayName = "workload without the role")]
    public async Task A_workload_resubmits_only_with_the_role_and_the_switch(string roles, bool workloadSwitch, bool allowed)
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => p.AllowWorkloadResubmit = workloadSwitch);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: roles));

        var prepared = await client.CallToolAsync("nimbus_prepare_action", PrepareArguments(await VersionAsync(client), "resubmit"));

        if (!allowed)
        {
            AssertError(prepared, "PermissionDenied");
            Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
            return;
        }

        var token = JsonSerializer.SerializeToElement(prepared.StructuredContent).GetProperty("actionToken").GetString();
        var executed = await CallAsync(client, "nimbus_resubmit_message", Execute(token, Guid.NewGuid()));
        var operation = await host.Get<InMemoryMessageStore>().GetOperatorOperation(executed.GetProperty("operationId").GetString()!);
        Assert.IsTrue(operation!.IsWorkload);
        Assert.AreEqual(McpTestHost.AgentClientId, operation.ClientId);
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    public async Task A_workload_can_never_skip()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => p.AllowWorkloadResubmit = true);
        await SeedFailureAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: WorkloadRoles + " Nimbus.Skip"));

        var prepared = await client.CallToolAsync("nimbus_prepare_action", PrepareArguments(await VersionAsync(client), "skip"));

        AssertError(prepared, "PermissionDenied");
    }

    [TestMethod]
    public async Task A_workload_over_its_budget_is_refused_and_nothing_is_sent()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => { p.AllowWorkloadResubmit = true; p.WorkloadResubmitsPerDay = 1; });
        await SeedFailureAsync(host);
        // One earlier unattended resubmit by the same client today, on another message.
        await host.Get<InMemoryMessageStore>().TryCreateOperatorOperation(Operation("earlier-op".PadRight(32, '0'), o => o.EventId = "evt-other"));
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: WorkloadRoles));

        var result = await client.CallToolAsync("nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), Guid.NewGuid()));

        AssertError(result, "ActionNotAllowed");
        StringAssert.Contains(ErrorText(result), "budget", StringComparison.Ordinal);
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    [DataRow(OperatorOperationStatus.Failed, false, DisplayName = "two failed agent resubmits: refused")]
    [DataRow(OperatorOperationStatus.Succeeded, true, DisplayName = "earlier ones succeeded: allowed")]
    public async Task The_per_message_cap_counts_unsuccessful_agent_resubmits_across_agents(OperatorOperationStatus earlier, bool allowed)
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => { p.AllowWorkloadResubmit = true; p.WorkloadResubmitsPerMessage = 2; });
        await SeedFailureAsync(host);
        var store = host.Get<InMemoryMessageStore>();
        // By other agents, on this message, outside the budget window so only the cap applies.
        foreach (var n in new[] { "1", "2" })
        {
            await store.TryCreateOperatorOperation(Operation($"cap-op-{n}".PadRight(32, '0'), o =>
            {
                o.ClientId = "another-agent-" + n;
                o.Status = earlier;
                o.CreatedAtUtc = DateTime.UtcNow.AddDays(-2);
            }));
        }

        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: WorkloadRoles));
        var result = await client.CallToolAsync("nimbus_resubmit_message", Execute(await PrepareAsync(client, "resubmit"), Guid.NewGuid()));

        if (allowed)
        {
            Assert.IsFalse(result.IsError == true, ErrorText(result));
        }
        else
        {
            AssertError(result, "ActionNotAllowed");
            StringAssert.Contains(ErrorText(result), "Hand it to a person", StringComparison.Ordinal);
            Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
        }
    }

    [TestMethod]
    public async Task Capabilities_report_the_budget_to_a_workload_and_nothing_to_a_person()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => { p.AllowWorkloadResubmit = true; p.WorkloadResubmitsPerDay = 7; p.WorkloadResubmitsPerMessage = 3; });
        await host.Get<InMemoryMessageStore>().TryCreateOperatorOperation(Operation("used-op".PadRight(32, '0')));

        await using var workload = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: WorkloadRoles));
        var unattended = (await CallAsync(workload, "nimbus_get_capabilities", new())).GetProperty("unattended");
        Assert.AreEqual(7, unattended.GetProperty("resubmitsPerWindow").GetInt32());
        Assert.AreEqual(1, unattended.GetProperty("resubmitsUsed").GetInt32());
        Assert.AreEqual(3, unattended.GetProperty("unsuccessfulResubmitsPerMessage").GetInt32());

        await using var person = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe nimbus.resubmit"));
        var capabilities = await CallAsync(person, "nimbus_get_capabilities", new());
        Assert.IsFalse(capabilities.TryGetProperty("unattended", out var none) && none.ValueKind != JsonValueKind.Null);
        Assert.IsFalse(capabilities.GetProperty("caller").GetProperty("isWorkload").GetBoolean());
    }

    [TestMethod]
    public void Turning_on_unattended_resubmits_is_a_widening_change()
    {
        var on = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings(), new McpAccessSettings { AllowWorkloadResubmit = true });
        var raised = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings(), new McpAccessSettings { WorkloadResubmitsPerDay = 50 });
        var lowered = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings(), new McpAccessSettings { WorkloadResubmitsPerMessage = 1 });

        Assert.IsTrue(on.Single().Widens);
        Assert.IsTrue(raised.Single().Widens);
        Assert.IsFalse(lowered.Single().Widens);
        Assert.IsFalse(new McpAccessSettings().AllowWorkloadResubmit);
    }

    private static OperatorOperation Operation(string id, Action<OperatorOperation>? configure = null)
    {
        var now = DateTime.UtcNow;
        var operation = new OperatorOperation
        {
            OperationId = id,
            TenantId = McpTestHost.TenantId,
            Caller = "agent-" + id,
            ClientId = McpTestHost.AgentClientId,
            IsWorkload = true,
            IdempotencyKey = Guid.NewGuid().ToString(),
            Action = OperatorOperationAction.Resubmit,
            EndpointId = Endpoint,
            EventId = EventId,
            Status = OperatorOperationStatus.Dispatched,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ExpiresAtUtc = now.AddDays(30),
        };
        configure?.Invoke(operation);
        return operation;
    }

    private static Task<McpTestHost> StartAsync(McpTestHost.StubAccess access, bool entra = false, Action<McpAccessSettings>? policy = null)
    {
        var store = new InMemoryMessageStore();
        if (policy is not null)
        {
            var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
            policy(settings);
            Assert.IsTrue(store.TrySetMcpAccessSettings(settings, null).GetAwaiter().GetResult());
        }

        var events = InterfaceFake<IEventApiController>.Create();
        events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args => SearchAsync(store, (string)args[1]!));
        events.On(nameof(IEventApiController.GetEventDetailsIdAsync),
            _ => Task.FromResult<ActionResult<EventDetails>>(new OkObjectResult(new EventDetails())));
        var endpoints = InterfaceFake<IEndpointApiController>.Create();
        endpoints.On(nameof(IEndpointApiController.GetEndpointSessionIdAsync), _ => Task.FromResult<ActionResult<SessionStatus>>(
            new OkObjectResult(new SessionStatus { SessionId = Session, PendingEvents = [], DeferredEvents = [] })));

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(access);
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
            services.AddSingleton<IOperatorOperationStore>(store);
            services.AddSingleton(events.Instance);
            services.AddSingleton(endpoints.Instance);
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

    private static async Task SeedFailureAsync(McpTestHost host)
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
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"v\":1}", EventTypeId = "CustomerChanged" } },
        });
        await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, EventId, Session, Attempt);
    }

    // As the Resolver stores the endpoint's answer: parented to the command's message id.
    private static Task StoreResponseAsync(McpTestHost host, string operationId, MessageType type)
        => host.Get<InMemoryMessageStore>().StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = "response-" + type,
            ParentMessageId = operationId,
            EndpointId = Endpoint,
            SessionId = Session,
            MessageType = type,
            EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(1),
            From = Endpoint,
            To = "Resolver",
        });

    private static async Task<string?> VersionAsync(McpClient client)
        => (await CallAsync(client, "nimbus_get_message", new() { ["endpointId"] = Endpoint, ["eventId"] = EventId }))
            .GetProperty("messageVersion").GetString();

    private static Dictionary<string, object?> PrepareArguments(string? version, string action) => new()
    {
        ["action"] = action, ["endpointId"] = Endpoint, ["eventId"] = EventId, ["messageVersion"] = version,
    };

    private static async Task<string?> PrepareAsync(McpClient client, string action)
        => (await CallAsync(client, "nimbus_prepare_action", PrepareArguments(await VersionAsync(client), action)))
            .GetProperty("actionToken").GetString();

    private static Task<JsonElement> GetOperationAsync(McpClient client, string operationId)
        => CallAsync(client, "nimbus_get_operation", new() { ["operationId"] = operationId });

    private static Dictionary<string, object?> Execute(string? token, Guid key) => new()
    {
        ["actionToken"] = token, ["idempotencyKey"] = key.ToString(), ["reason"] = "Downstream is back",
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
}
