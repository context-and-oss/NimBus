#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 044 Phase 3, linking failures to a report: <c>nimbus_set_message_reported</c> marks a
/// batch under one ticket, <c>nimbus_find_reports</c> finds a ticket to reuse, and workloads with
/// the <c>Nimbus.Annotate</c> app role may mark messages while an Owner allows it.
/// </summary>
[TestClass]
public class ReportToolsTests
{
    private const string Endpoint = "CrmEndpoint";

    [TestMethod]
    public async Task A_batch_is_marked_under_one_ticket_and_each_message_is_audited()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await using var client = await host.CreateClientAsync();

        var result = await CallAsync(client, "nimbus_set_message_reported", Report(eventIds: ["evt-1", "evt-2", "evt-2", "evt-3"], ticketId: "DQ-17"));

        Assert.AreEqual("Accepted", result.GetProperty("status").GetString());
        Assert.AreEqual(3, result.GetProperty("items").GetArrayLength(), "Duplicates are marked once.");
        var reports = await host.Get<InMemoryMessageStore>().FindEventReports(Endpoint, "DQ-17", 50);
        CollectionAssert.AreEquivalent(new[] { "evt-1", "evt-2", "evt-3" }, reports.Select(r => r.EventId).ToArray());
        Assert.AreEqual(3, host.Get<McpTestHost.RecordingAuditLog>().Rows.Count(r => r.Type == MessageAuditType.ReportEvent && r.Required));
    }

    [TestMethod]
    public async Task One_event_id_keeps_the_single_message_result()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await using var client = await host.CreateClientAsync();

        var result = await CallAsync(client, "nimbus_set_message_reported", Report(eventId: "evt-1", ticketId: "DQ-1"));

        Assert.AreEqual("evt-1", result.GetProperty("eventId").GetString());
        Assert.IsTrue(!result.TryGetProperty("items", out var items) || items.ValueKind == JsonValueKind.Null);
    }

    [TestMethod]
    [DataRow(true, false, DisplayName = "both eventId and eventIds")]
    [DataRow(false, false, DisplayName = "neither")]
    [DataRow(false, true, DisplayName = "more than 50")]
    public async Task A_batch_must_name_its_messages_once_and_within_the_limit(bool both, bool tooMany)
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await using var client = await host.CreateClientAsync();
        var arguments = tooMany
            ? Report(eventIds: Enumerable.Range(0, 51).Select(i => $"evt-{i}").ToArray())
            : Report(eventId: both ? "evt-1" : null, eventIds: both ? ["evt-2"] : null);

        var result = await client.CallToolAsync("nimbus_set_message_reported", arguments);

        AssertError(result, "InvalidArgument");
        Assert.AreEqual(0, (await host.Get<InMemoryMessageStore>().FindEventReports(Endpoint, null, 100)).Count);
    }

    [TestMethod]
    public async Task Reports_are_found_by_ticket_with_the_messages_status_and_error_pattern()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        var store = host.Get<InMemoryMessageStore>();
        await SeedFailureAsync(store, "evt-1", "Customer C-100245 not found in ERP");
        await SeedFailureAsync(store, "evt-2", "Customer C-900001 not found in ERP");
        await store.SetEventReport(Endpoint, "evt-1", true, "agent", "DQ-5");
        await store.SetEventReport(Endpoint, "evt-2", true, "agent", "DQ-6");
        await using var client = await host.CreateClientAsync();

        var byTicket = await CallAsync(client, "nimbus_find_reports", new() { ["endpointId"] = Endpoint, ["ticketId"] = "DQ-5" });
        var all = await CallAsync(client, "nimbus_find_reports", new() { ["endpointId"] = Endpoint });

        var row = byTicket.GetProperty("reports").EnumerateArray().Single();
        Assert.AreEqual("evt-1", row.GetProperty("eventId").GetString());
        Assert.AreEqual("Failed", row.GetProperty("currentStatus").GetString());
        var patterns = all.GetProperty("reports").EnumerateArray().Select(r => r.GetProperty("errorPattern").GetString()).Distinct().ToList();
        Assert.AreEqual(1, patterns.Count, "Both failures share one normalized pattern, so the agent can reuse a ticket.");
        Assert.IsFalse(patterns[0]!.Contains("C-100245", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(true, true, DisplayName = "role and switch")]
    [DataRow(false, false, DisplayName = "switch off")]
    public async Task A_workload_marks_messages_only_with_the_annotate_role_and_the_switch(bool workloadSwitch, bool allowed)
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => p.AllowWorkloadAnnotation = workloadSwitch);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: "Nimbus.Observe Nimbus.Annotate", name: null));

        var result = await client.CallToolAsync("nimbus_set_message_reported", Report(eventIds: ["evt-1"], ticketId: "DQ-9"));

        if (allowed)
            Assert.IsFalse(result.IsError == true, ErrorText(result));
        else
            AssertError(result, "PermissionDenied");
        Assert.AreEqual(allowed ? 1 : 0, (await host.Get<InMemoryMessageStore>().FindEventReports(Endpoint, "DQ-9", 10)).Count);
    }

    [TestMethod]
    public async Task Resubmit_by_ticket_runs_each_eligible_message_as_its_own_operation()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        var store = host.Get<InMemoryMessageStore>();
        foreach (var id in new[] { "evt-1", "evt-2", "evt-3" })
        {
            await SeedFailureAsync(store, id, "Address was null");
            await store.SetEventReport(Endpoint, id, true, "agent", "BUG-12");
        }
        await store.SetEventReport(Endpoint, "evt-gone", true, "agent", "BUG-12");
        await using var client = await host.CreateClientAsync();

        var prepared = await CallAsync(client, "nimbus_prepare_action", new() { ["action"] = "resubmit", ["endpointId"] = Endpoint, ["ticketId"] = "BUG-12" });
        Assert.AreEqual(3, prepared.GetProperty("affectedMessages").GetInt32());
        Assert.AreEqual("evt-gone", prepared.GetProperty("notEligible")[0].GetProperty("eventId").GetString());

        // One message fails again with a new attempt before the batch runs.
        await SeedFailureAsync(store, "evt-3", "Address was null", attempt: "err-evt-3-b");
        var key = Guid.NewGuid().ToString();
        var executed = await CallAsync(client, "nimbus_resubmit_message", new()
        {
            ["actionToken"] = prepared.GetProperty("actionToken").GetString(), ["idempotencyKey"] = key, ["reason"] = "Fix deployed",
        });

        var items = executed.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("eventId").GetString()!);
        Assert.AreEqual("Dispatched", items["evt-1"].GetProperty("status").GetString());
        Assert.AreEqual("Dispatched", items["evt-2"].GetProperty("status").GetString());
        Assert.AreEqual("StaleMessage", items["evt-3"].GetProperty("status").GetString());
        Assert.AreEqual("Partial", executed.GetProperty("status").GetString());
        Assert.AreEqual(2, host.Get<McpTestHost.RecordingManagerClient>().CommandIds.Count);
        Assert.AreNotEqual(items["evt-1"].GetProperty("operationId").GetString(), items["evt-2"].GetProperty("operationId").GetString());

        // A retry with the same key sends nothing again.
        var retry = await CallAsync(client, "nimbus_resubmit_message", new()
        {
            ["actionToken"] = prepared.GetProperty("actionToken").GetString(), ["idempotencyKey"] = key, ["reason"] = "Fix deployed",
        });
        Assert.AreEqual(2, host.Get<McpTestHost.RecordingManagerClient>().CommandIds.Count);
        var replayed = retry.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("eventId").GetString() == "evt-1");
        Assert.AreEqual("Already run with this idempotencyKey.", replayed.GetProperty("detail").GetString());
    }

    [TestMethod]
    public async Task A_workload_resubmit_by_ticket_stops_at_its_budget()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true }, entra: true,
            policy: p => { p.AllowWorkloadResubmit = true; p.WorkloadResubmitsPerDay = 1; });
        var store = host.Get<InMemoryMessageStore>();
        foreach (var id in new[] { "evt-1", "evt-2" })
        {
            await SeedFailureAsync(store, id, "Address was null");
            await store.SetEventReport(Endpoint, id, true, "agent", "BUG-13");
        }
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: null, roles: "Nimbus.Observe Nimbus.Resubmit", name: null));

        var prepared = await CallAsync(client, "nimbus_prepare_action", new() { ["action"] = "resubmit", ["endpointId"] = Endpoint, ["ticketId"] = "BUG-13" });
        var executed = await CallAsync(client, "nimbus_resubmit_message", new()
        {
            ["actionToken"] = prepared.GetProperty("actionToken").GetString(), ["idempotencyKey"] = Guid.NewGuid().ToString(), ["reason"] = "Fix deployed",
        });

        var statuses = executed.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("status").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "Dispatched", "Refused" }, statuses);
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingManagerClient>().CommandIds.Count);
    }

    [TestMethod]
    public async Task Skip_cannot_be_prepared_by_ticket()
    {
        await using var host = await StartAsync(new McpTestHost.StubAccess { Contributor = true });
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", new Dictionary<string, object?> { ["action"] = "skip", ["endpointId"] = Endpoint, ["ticketId"] = "BUG-1" });

        AssertError(result, "InvalidArgument");
    }

    [TestMethod]
    public void Letting_workloads_mark_messages_reported_is_a_widening_change()
    {
        var on = NimBus.WebApp.Mcp.Access.McpAccessRules.Changes(new McpAccessSettings(), new McpAccessSettings { AllowWorkloadAnnotation = true });

        Assert.AreEqual("Let workloads mark messages reported", on.Single().Text);
        Assert.IsTrue(on.Single().Widens);
        Assert.IsFalse(new McpAccessSettings().AllowWorkloadAnnotation);
    }

    private static Dictionary<string, object?> Report(string? eventId = null, string[]? eventIds = null, string? ticketId = null) => new()
    {
        ["endpointId"] = Endpoint,
        ["reported"] = true,
        ["reason"] = "Filed with the source owner",
        ["idempotencyKey"] = Guid.NewGuid().ToString(),
        ["eventId"] = eventId,
        ["eventIds"] = eventIds,
        ["ticketId"] = ticketId,
    };

    private static async Task SeedFailureAsync(InMemoryMessageStore store, string eventId, string errorText, string? attempt = null)
    {
        attempt ??= "err-" + eventId;
        // The failed attempt, as the Resolver stores it, with the payload a resubmit replays.
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = attempt,
            EndpointId = Endpoint,
            SessionId = "sess-" + eventId,
            MessageType = MessageType.ErrorResponse,
            EnqueuedTimeUtc = DateTime.UtcNow,
            EventTypeId = "CrmAccountCreated",
            From = Endpoint,
            To = "Resolver",
            OriginatingMessageId = "req-" + eventId,
            MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{}", EventTypeId = "CrmAccountCreated" } },
        });
        var row = await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, eventId, "sess-" + eventId, attempt);
        row.MessageContent = new MessageContent { ErrorContent = new ErrorContent { ErrorText = errorText, ErrorType = "KeyNotFoundException" } };
        row.EventTypeId = "CrmAccountCreated";
        await store.UploadFailedMessage(eventId, row.SessionId!, Endpoint, row);
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

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(access);
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
            services.AddSingleton<IOperatorOperationStore>(store);
        }

        return entra ? McpTestHost.StartEntraAsync(Configure) : McpTestHost.StartLocalDevelopmentAsync(Configure);
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
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
