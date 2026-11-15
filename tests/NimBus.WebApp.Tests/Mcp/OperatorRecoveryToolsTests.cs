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
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Services.Operations;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 035 Phase 3 over the real MCP pipeline: settling a pending handoff, reprocessing a
/// session's deferred messages, skipping stale deferred tracking and resubmitting with an edited
/// payload. Each is off until a site Owner switches it on, takes a preview and a token like
/// resubmit and skip, and never runs for a workload.
/// </summary>
[TestClass]
public class OperatorRecoveryToolsTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string EventId = "evt-rec-1";
    private const string Session = "sess-1";
    private const string Attempt = "msg-1";

    private static readonly string[] RecoveryTools =
    [
        "nimbus_settle_handoff", "nimbus_reprocess_deferred", "nimbus_skip_deferred_tracking", "nimbus_resubmit_with_changes",
    ];

    // ── The Owner's switches and the scopes ─────────────────────────────────

    [TestMethod]
    public async Task The_recovery_tools_are_hidden_and_refused_until_an_Owner_switches_them_on()
    {
        await using var off = await StartAsync(_ => { });
        await using var offClient = await off.CreateClientAsync();
        await SeedHandoffAsync(off);

        var hidden = (await offClient.ListToolsAsync()).Select(t => t.Name).ToList();
        var refused = await offClient.CallToolAsync("nimbus_prepare_action", Prepare("settle_handoff", await VersionAsync(off)));
        var direct = await offClient.CallToolAsync("nimbus_settle_handoff", Settle("token", "complete", "Done"));

        Assert.IsFalse(hidden.Intersect(RecoveryTools).Any(), string.Join(", ", hidden));
        AssertError(refused, "PermissionDenied", "settle_handoff is turned off by an administrator");
        AssertError(direct, "PermissionDenied", "turned off by an administrator");

        await using var on = await StartAsync(AllOn);
        await using var onClient = await on.CreateClientAsync();
        var listed = (await onClient.ListToolsAsync()).ToDictionary(t => t.Name);

        foreach (var tool in RecoveryTools)
            Assert.IsTrue(listed.ContainsKey(tool), tool);
        Assert.IsTrue(listed["nimbus_skip_deferred_tracking"].ProtocolTool.Annotations?.DestructiveHint == true);
        Assert.IsTrue(listed["nimbus_resubmit_with_changes"].ProtocolTool.Annotations?.DestructiveHint == true);
        Assert.IsTrue(RecoveryTools.All(tool => listed[tool].ProtocolTool.Annotations?.ReadOnlyHint == false));
    }

    [TestMethod]
    [DataRow("nimbus.observe nimbus.resubmit nimbus.skip", null)]
    [DataRow(null, "Nimbus.Workload.Observe Nimbus.Workload.Resubmit Nimbus.Workload.Annotate Nimbus.Workload.Classify")]
    public async Task A_caller_without_the_delegated_scope_is_refused_and_a_workload_always_is(string? scopes, string? roles)
    {
        await using var host = await StartAsync(
            p =>
            {
                AllOn(p);
                p.AllowWorkloads = true;
                p.AllowWorkloadResubmit = true;
            },
            entra: true);
        await SeedHandoffAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: scopes, roles: roles));

        foreach (var action in new[] { "settle_handoff", "reprocess_deferred", "skip_deferred_tracking", "resubmit_with_changes" })
        {
            var result = await client.CallToolAsync("nimbus_prepare_action", Prepare(action, await VersionAsync(host)));
            AssertError(result, "PermissionDenied", $"{action} requires the delegated");
        }

        Assert.AreEqual(0, host.Get<McpTestHost.RecordingHandoffClients>().Sent.Count);
    }

    [TestMethod]
    public async Task The_delegated_scope_admits_its_action()
    {
        await using var host = await StartAsync(AllOn, entra: true);
        await SeedHandoffAsync(host);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe nimbus.settle"));

        var token = await PrepareTokenAsync(client, host, "settle_handoff");
        var result = await CallAsync(client, "nimbus_settle_handoff", Settle(token, "complete", "ERP job finished"));

        Assert.IsTrue(result.GetProperty("commandSent").GetBoolean());
        Assert.AreEqual(1, host.Get<McpTestHost.RecordingHandoffClients>().Sent.Count);
    }

    [TestMethod]
    public async Task A_message_lists_a_recovery_action_only_while_it_is_switched_on_and_its_state_fits()
    {
        await using var on = await StartAsync(AllOn, piiReader: true);
        await using var off = await StartAsync(_ => { }, piiReader: true);
        await using var onClient = await on.CreateClientAsync();
        await using var offClient = await off.CreateClientAsync();
        var read = new Dictionary<string, object?> { ["endpointId"] = Endpoint, ["eventId"] = EventId };

        await SeedRowAsync(on, ResolutionStatus.Deferred);
        await SeedRowAsync(off, ResolutionStatus.Deferred);
        var deferred = await CallAsync(onClient, "nimbus_get_message", read);
        var deferredOff = await CallAsync(offClient, "nimbus_get_message", read);

        CollectionAssert.AreEqual(new[] { "reprocess_deferred", "skip_deferred_tracking", "report" }, Strings(deferred.GetProperty("eligibleActions")));
        CollectionAssert.AreEqual(new[] { "report" }, Strings(deferredOff.GetProperty("eligibleActions")));
        var options = deferred.GetProperty("nextActions").EnumerateArray().Select(o => o.GetProperty("id").GetString()).ToList();
        CollectionAssert.Contains(options, "reprocess_deferred");
        CollectionAssert.DoesNotContain(options, "open_web_ui");

        await using var handoffHost = await StartAsync(AllOn, piiReader: true);
        await using var handoffClient = await handoffHost.CreateClientAsync();
        await SeedHandoffAsync(handoffHost);
        var handoff = await CallAsync(handoffClient, "nimbus_get_message", read);
        CollectionAssert.AreEqual(new[] { "settle_handoff", "report" }, Strings(handoff.GetProperty("eligibleActions")));

        await using var failedHost = await StartAsync(AllOn, piiReader: true);
        await using var failedClient = await failedHost.CreateClientAsync();
        await SeedRowAsync(failedHost, ResolutionStatus.Failed);
        var failed = await CallAsync(failedClient, "nimbus_get_message", read);
        CollectionAssert.AreEqual(new[] { "resubmit", "skip", "resubmit_with_changes", "report" }, Strings(failed.GetProperty("eligibleActions")));
        var capabilities = await CallAsync(failedClient, "nimbus_get_capabilities", new());
        CollectionAssert.IsSubsetOf(
            new[] { "settle_handoff", "reprocess_deferred", "skip_deferred_tracking", "resubmit_with_changes" },
            Strings(capabilities.GetProperty("permittedActions")));
    }

    // ── Handoff settlement ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Settling_a_handoff_runs_from_a_preview_and_is_audited_before_it_is_sent()
    {
        await using var host = await StartAsync(AllOn);
        await SeedHandoffAsync(host);
        await using var client = await host.CreateClientAsync();

        var prepared = await CallAsync(client, "nimbus_prepare_action", Prepare("settle_handoff", await VersionAsync(host)));
        Assert.AreEqual("settle_handoff", prepared.GetProperty("action").GetString());
        Assert.AreEqual("Pending", prepared.GetProperty("expectedStatus").GetString());
        var next = prepared.GetProperty("nextActions")[0];
        Assert.AreEqual("nimbus_settle_handoff", next.GetProperty("tool").GetString());
        Assert.IsTrue(next.GetProperty("confirm").GetBoolean());
        CollectionAssert.Contains(Strings(next.GetProperty("needs")), "outcome");

        var result = await CallAsync(client, "nimbus_settle_handoff",
            Settle(prepared.GetProperty("actionToken").GetString(), "fail", "ERP rejected the order", errorType: "ErpRejected"));

        Assert.AreEqual("settle_handoff", result.GetProperty("action").GetString());
        Assert.IsTrue(result.GetProperty("commandSent").GetBoolean());
        var sent = host.Get<McpTestHost.RecordingHandoffClients>().Sent.Single();
        Assert.IsFalse(sent.Completed);
        Assert.AreEqual(Endpoint, sent.EndpointId);
        Assert.AreEqual(Attempt, sent.Coordinates.MessageId);
        Assert.AreEqual("ERP rejected the order", sent.ErrorText);
        Assert.AreEqual("ErpRejected", sent.ErrorType);
        var audit = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Required);
        Assert.AreEqual(MessageAuditType.FailHandoff, audit.Type);
        StringAssert.Contains(audit.Data, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"reason\":\"ERP rejected the order\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_message_that_is_not_a_pending_handoff_cannot_be_settled()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("settle_handoff", await VersionAsync(host)));

        AssertError(result, "ActionNotAllowed", "not a pending handoff");
    }

    [TestMethod]
    public async Task A_handoff_that_changed_after_the_preview_is_stale_and_nothing_is_sent()
    {
        await using var host = await StartAsync(AllOn);
        await SeedHandoffAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareTokenAsync(client, host, "settle_handoff");

        // The external system settled it meanwhile and the endpoint failed on a later attempt.
        await SeedRowAsync(host, ResolutionStatus.Failed, lastMessageId: "msg-2");

        var result = await client.CallToolAsync("nimbus_settle_handoff", Settle(token, "complete", "Done"));

        AssertError(result, "StaleMessage", "changed since you prepared");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingHandoffClients>().Sent.Count);
    }

    [TestMethod]
    public async Task A_settle_token_runs_no_other_tool_and_an_unknown_outcome_is_refused()
    {
        await using var host = await StartAsync(AllOn);
        await SeedHandoffAsync(host);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareTokenAsync(client, host, "settle_handoff");

        var wrongTool = await client.CallToolAsync("nimbus_reprocess_deferred", Run(token, "Wrong tool"));
        var wrongOutcome = await client.CallToolAsync("nimbus_settle_handoff", Settle(token, "maybe", "Unsure"));

        AssertError(wrongTool, "StaleMessage", "not issued to you for reprocess_deferred");
        AssertError(wrongOutcome, "InvalidArgument", "outcome must be complete or fail");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingHandoffClients>().Sent.Count);
    }

    // ── Deferred reprocess ──────────────────────────────────────────────────

    [TestMethod]
    public async Task Reprocess_previews_the_deferred_count_and_requests_the_drain_after_the_audit()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred);
        await SeedRowAsync(host, ResolutionStatus.Deferred, eventId: "evt-rec-2", lastMessageId: "msg-b");
        await using var client = await host.CreateClientAsync();

        var prepared = await CallAsync(client, "nimbus_prepare_action", Prepare("reprocess_deferred", await VersionAsync(host)));
        Assert.AreEqual(2, prepared.GetProperty("deferredInSession").GetInt32());
        Assert.AreEqual(2, prepared.GetProperty("affectedMessages").GetInt32());

        var result = await CallAsync(client, "nimbus_reprocess_deferred", Run(prepared.GetProperty("actionToken").GetString(), "Blocker was resolved by hand"));

        Assert.IsTrue(result.GetProperty("commandSent").GetBoolean());
        Assert.IsTrue(result.GetProperty("sessionStateCleared").GetBoolean());
        CollectionAssert.AreEqual(new[] { $"{Endpoint}/{Session}" }, host.Get<McpTestHost.RecordingReprocess>().Requests.ToArray());
        var audit = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Required);
        Assert.AreEqual(MessageAuditType.ReprocessDeferred, audit.Type);
        StringAssert.Contains(audit.Data, "\"deferredCount\":2", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Reprocess_is_refused_while_a_failed_message_still_blocks_the_session()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred);
        await SeedRowAsync(host, ResolutionStatus.Failed, eventId: "evt-blocker", lastMessageId: "msg-blocker");
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("reprocess_deferred", await VersionAsync(host)));

        AssertError(result, "ActionNotAllowed", "still blocked by failed message 'evt-blocker'");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingReprocess>().Requests.Count);
    }

    [TestMethod]
    public async Task Reprocess_refuses_a_session_over_the_server_bound_instead_of_reprocessing_part_of_it()
    {
        await using var host = await StartAsync(AllOn, extra: services => services.AddSingleton(
            new McpOperatorRuntime(McpAuthenticationMode.LocalDevelopment, new McpOperatorOptions { Enabled = true, MaxDeferredReprocess = 1 })));
        await SeedRowAsync(host, ResolutionStatus.Deferred);
        await SeedRowAsync(host, ResolutionStatus.Deferred, eventId: "evt-rec-2", lastMessageId: "msg-b");
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("reprocess_deferred", await VersionAsync(host)));

        AssertError(result, "ActionNotAllowed", "more than the 1 one reprocess may request");
    }

    [TestMethod]
    public async Task Reprocess_checks_the_session_again_when_it_runs()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred);
        await using var client = await host.CreateClientAsync();
        var token = await PrepareTokenAsync(client, host, "reprocess_deferred");

        // A message in the session fails between the preview and the run.
        await SeedRowAsync(host, ResolutionStatus.Failed, eventId: "evt-blocker", lastMessageId: "msg-blocker");

        var result = await client.CallToolAsync("nimbus_reprocess_deferred", Run(token, "Go"));

        AssertError(result, "ActionNotAllowed", "still blocked");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingReprocess>().Requests.Count);
        Assert.IsFalse(host.Get<McpTestHost.RecordingAuditLog>().Rows.Any(r => r.Type == MessageAuditType.ReprocessDeferred));
    }

    // ── Stale deferred tracking ─────────────────────────────────────────────

    [TestMethod]
    public async Task Skipping_stale_tracking_previews_the_broker_checks_and_reports_the_audit()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred, updatedAt: DateTime.UtcNow.AddHours(-1));
        await using var client = await host.CreateClientAsync();

        var prepared = await CallAsync(client, "nimbus_prepare_action", Prepare("skip_deferred_tracking", await VersionAsync(host)));
        var checks = prepared.GetProperty("inspection").GetProperty("brokerChecks").EnumerateArray().ToList();
        Assert.IsNotEmpty(checks);
        Assert.IsTrue(checks.All(c => c.GetProperty("status").GetString() == "NotFound"));
        StringAssert.Contains(prepared.GetProperty("impact").GetString(), "nothing is removed", StringComparison.Ordinal);
        Assert.AreEqual("destructive", prepared.GetProperty("nextActions")[0].GetProperty("kind").GetString());

        var result = await CallAsync(client, "nimbus_skip_deferred_tracking", Run(prepared.GetProperty("actionToken").GetString(), "Completed in CRM; verified by hand"));

        Assert.IsFalse(result.GetProperty("commandSent").GetBoolean());
        Assert.IsTrue(result.GetProperty("auditRecorded").GetBoolean());
        var store = host.Get<InMemoryMessageStore>();
        Assert.AreEqual(ResolutionStatus.Skipped, (await store.GetEvent(Endpoint, EventId))!.ResolutionStatus);
        var audit = (await store.GetMessageAudits(EventId)).Single();
        Assert.AreEqual(MessageAuditType.Skip, audit.AuditType);
        Assert.AreEqual("Completed in CRM; verified by hand", audit.Comment);
        StringAssert.Contains(audit.Data!, "\"channel\":\"Mcp\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_tracking_record_that_is_not_stale_is_refused_at_the_preview()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("skip_deferred_tracking", await VersionAsync(host)));

        AssertError(result, "ActionNotAllowed", "unchanged for 15 minutes");
        Assert.AreEqual(ResolutionStatus.Deferred, (await host.Get<InMemoryMessageStore>().GetEvent(Endpoint, EventId))!.ResolutionStatus);
    }

    [TestMethod]
    public async Task A_broker_message_that_appears_after_the_preview_stops_the_skip()
    {
        await using var host = await StartAsync(AllOn);
        await SeedRowAsync(host, ResolutionStatus.Deferred, updatedAt: DateTime.UtcNow.AddHours(-1));
        await using var client = await host.CreateClientAsync();
        var token = await PrepareTokenAsync(client, host, "skip_deferred_tracking");

        host.Get<DeferredMessageInspectorTests.FakeClient>().Error = new Azure.Messaging.ServiceBus.ServiceBusException(
            "unavailable", Azure.Messaging.ServiceBus.ServiceBusFailureReason.ServiceCommunicationProblem);

        var result = await client.CallToolAsync("nimbus_skip_deferred_tracking", Run(token, "Stale"));

        AssertError(result, "ActionNotAllowed", "Broker inspection is incomplete");
        Assert.AreEqual(ResolutionStatus.Deferred, (await host.Get<InMemoryMessageStore>().GetEvent(Endpoint, EventId))!.ResolutionStatus);
    }

    // ── Resubmit with changes ───────────────────────────────────────────────

    [TestMethod]
    public async Task An_edited_resubmit_previews_the_changed_paths_and_runs_as_a_journaled_operation()
    {
        await using var host = await StartAsync(AllOn, piiReader: true);
        await SeedRowAsync(host, ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();
        const string edited = "{\"amount\":250,\"customer\":{\"name\":\"Acme AB\"},\"note\":\"corrected\"}";

        var prepared = await CallAsync(client, "nimbus_prepare_action", Prepare("resubmit_with_changes", await VersionAsync(host), edited));
        CollectionAssert.AreEquivalent(
            new[] { "$.amount (changed)", "$.note (added)" },
            Strings(prepared.GetProperty("changedPaths")));
        var next = prepared.GetProperty("nextActions")[0];
        Assert.AreEqual("nimbus_resubmit_with_changes", next.GetProperty("tool").GetString());
        Assert.AreEqual("destructive", next.GetProperty("kind").GetString());

        var arguments = Run(prepared.GetProperty("actionToken").GetString(), "Amount was wrong at the source");
        arguments["eventContent"] = edited;
        var result = await CallAsync(client, "nimbus_resubmit_with_changes", arguments);

        Assert.AreEqual("resubmit_with_changes", result.GetProperty("action").GetString());
        var operationId = result.GetProperty("operationId").GetString();
        var manager = host.Get<McpTestHost.RecordingManagerClient>();
        CollectionAssert.AreEqual(new[] { $"resubmit:{Endpoint}:{EventId}" }, manager.Sent.ToArray());
        CollectionAssert.AreEqual(new[] { operationId }, manager.CommandIds.ToArray(), "The command is published under the operation id.");
        var audit = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Required);
        Assert.AreEqual(MessageAuditType.ResubmitWithChanges, audit.Type);
        StringAssert.Contains(audit.Data, "$.amount (changed)", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"contentHash\":", StringComparison.Ordinal);
        Assert.IsFalse(audit.Data!.Contains("Acme AB", StringComparison.Ordinal), "The audit row names what changed, never the values.");

        var operation = await CallAsync(client, "nimbus_get_operation", new() { ["operationId"] = operationId });
        Assert.AreEqual("resubmit_with_changes", operation.GetProperty("action").GetString());
    }

    [TestMethod]
    public async Task A_payload_other_than_the_one_previewed_is_refused_and_nothing_is_sent()
    {
        await using var host = await StartAsync(AllOn, piiReader: true);
        await SeedRowAsync(host, ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();
        var prepared = await CallAsync(client, "nimbus_prepare_action",
            Prepare("resubmit_with_changes", await VersionAsync(host), "{\"amount\":250,\"customer\":{\"name\":\"Acme AB\"}}"));

        var arguments = Run(prepared.GetProperty("actionToken").GetString(), "Changed my mind");
        arguments["eventContent"] = "{\"amount\":999,\"customer\":{\"name\":\"Acme AB\"}}";
        var result = await client.CallToolAsync("nimbus_resubmit_with_changes", arguments);

        AssertError(result, "StaleMessage", "not the payload you previewed");
        Assert.AreEqual(0, host.Get<McpTestHost.RecordingManagerClient>().Sent.Count);
    }

    [TestMethod]
    [DataRow(false, "{\"amount\":250}", "PermissionDenied", "requires the PiiReader role")]
    [DataRow(true, "{\"amount\":250,\"$piiMasked\":true}", "InvalidArgument", "masked placeholders")]
    [DataRow(true, "[1,2]", "InvalidArgument", "must be one JSON object")]
    [DataRow(true, "{\"amount\":100,\"customer\":{\"name\":\"Acme AB\"}}", "InvalidArgument", "does not differ")]
    [DataRow(true, null, "InvalidArgument", "eventContent is required")]
    public async Task An_edited_payload_is_refused(bool piiReader, string? content, string code, string detail)
    {
        // A real masker: a PiiReader without the payload scope reads masked values over MCP, and
        // must not be able to write them back.
        await using var host = await StartAsync(AllOn, piiReader: piiReader, extra: services =>
            services.AddSingleton<IEventJsonMasker>(sp => new EventJsonMasker(sp.GetRequiredService<IPlatform>())));
        await SeedRowAsync(host, ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("resubmit_with_changes", await VersionAsync(host), content));

        AssertError(result, code, detail);
    }

    [TestMethod]
    public async Task Event_content_is_refused_for_every_other_action()
    {
        await using var host = await StartAsync(AllOn, piiReader: true);
        await SeedRowAsync(host, ResolutionStatus.Failed);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", Prepare("resubmit", await VersionAsync(host), "{\"amount\":1}"));

        AssertError(result, "InvalidArgument", "eventContent is only for resubmit_with_changes");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static void AllOn(McpAccessSettings policy)
    {
        policy.Capabilities.SettleHandoffs = true;
        policy.Capabilities.RecoverDeferred = true;
        policy.Capabilities.EditPayloads = true;
    }

    private static async Task<McpTestHost> StartAsync(Action<McpAccessSettings> policy, bool entra = false, bool piiReader = false,
        Action<IServiceCollection>? extra = null)
    {
        var store = new InMemoryMessageStore();
        var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
        policy(settings);
        Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, null));

        // What nimbus_get_message reads through the REST implementation: the tracked row.
        var events = InterfaceFake<Api.IEventApiController>.Create();
        events.On(nameof(Api.IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args => SearchAsync(store, (string)args[1]!));
        events.On(nameof(Api.IEventApiController.GetEventDetailsIdAsync),
            _ => Task.FromResult<Microsoft.AspNetCore.Mvc.ActionResult<Api.EventDetails>>(new Microsoft.AspNetCore.Mvc.OkObjectResult(new Api.EventDetails())));

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(events.Instance);
            services.AddSingleton(new McpTestHost.StubAccess { Contributor = true, SiteReader = true, PiiReader = piiReader });
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
            services.AddSingleton<IOperatorOperationStore>(store);
            extra?.Invoke(services);
        }

        return entra ? await McpTestHost.StartEntraAsync(Configure) : await McpTestHost.StartLocalDevelopmentAsync(Configure);
    }

    private static async Task<Microsoft.AspNetCore.Mvc.ActionResult<Api.SearchResponse>> SearchAsync(InMemoryMessageStore store, string endpointId)
    {
        var row = await store.GetEvent(endpointId, EventId);
        var found = row is null
            ? new List<Api.Event>()
            : [new Api.Event
            {
                EventId = row.EventId, ResolutionStatus = row.ResolutionStatus.ToString(), LastMessageId = row.LastMessageId,
                SessionId = row.SessionId, PendingSubStatus = row.PendingSubStatus,
            }];
        return new Microsoft.AspNetCore.Mvc.OkObjectResult(new Api.SearchResponse { Events = found });
    }

    private static async Task SeedHandoffAsync(McpTestHost host)
    {
        var row = await SeedRowAsync(host, ResolutionStatus.Pending);
        row.PendingSubStatus = "Handoff";
    }

    /// <summary>Seeds the latest message and its tracked row; returns the stored row.</summary>
    private static async Task<UnresolvedEvent> SeedRowAsync(McpTestHost host, ResolutionStatus status, string eventId = EventId,
        string lastMessageId = Attempt, DateTime? updatedAt = null)
    {
        var store = host.Get<InMemoryMessageStore>();
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = lastMessageId,
            EndpointId = Endpoint,
            SessionId = Session,
            MessageType = status == ResolutionStatus.Failed ? MessageType.ErrorResponse : MessageType.EventRequest,
            EnqueuedTimeUtc = DateTime.UtcNow,
            EventTypeId = "CustomerChanged",
            From = Endpoint,
            To = "Resolver",
            OriginatingMessageId = "req-1",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = "{\"amount\":100,\"customer\":{\"name\":\"Acme AB\"}}", EventTypeId = "CustomerChanged" },
            },
        });
        var row = await OperatorCommandTestRows.SeedFailedRowAsync(store, Endpoint, eventId, Session, lastMessageId, status);
        row.EventTypeId = "CustomerChanged";
        if (updatedAt is { } at)
            row.UpdatedAt = at;
        return row;
    }

    private static async Task<string> VersionAsync(McpTestHost host, string eventId = EventId)
        => OperatorMessageVersion.From((await host.Get<InMemoryMessageStore>().GetEvent(Endpoint, eventId))!).Encode();

    private static async Task<string?> PrepareTokenAsync(McpClient client, McpTestHost host, string action)
        => (await CallAsync(client, "nimbus_prepare_action", Prepare(action, await VersionAsync(host)))).GetProperty("actionToken").GetString();

    private static Dictionary<string, object?> Prepare(string action, string version, string? eventContent = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["action"] = action, ["endpointId"] = Endpoint, ["eventId"] = EventId, ["messageVersion"] = version,
        };
        if (eventContent is not null)
            arguments["eventContent"] = eventContent;
        return arguments;
    }

    private static Dictionary<string, object?> Run(string? token, string reason) => new()
    {
        ["actionToken"] = token, ["idempotencyKey"] = Guid.NewGuid().ToString(), ["reason"] = reason,
    };

    private static Dictionary<string, object?> Settle(string? token, string outcome, string reason, string? errorType = null)
    {
        var arguments = Run(token, reason);
        arguments["outcome"] = outcome;
        if (errorType is not null)
            arguments["errorType"] = errorType;
        return arguments;
    }

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static void AssertError(CallToolResult result, string code, string detail)
    {
        Assert.IsTrue(result.IsError == true, "Expected an error result.");
        StringAssert.Contains(ErrorText(result), $"[{code}]", StringComparison.Ordinal);
        StringAssert.Contains(ErrorText(result), detail, StringComparison.Ordinal);
    }

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToArray();
}
