#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.Insights;
using EndpointStatusCount = NimBus.WebApp.ManagementApi.EndpointStatusCount;
using IEndpointApiController = NimBus.WebApp.ManagementApi.IEndpointApiController;
using IMonitorApiController = NimBus.WebApp.ManagementApi.IMonitorApiController;
using MonitorAcknowledgement = NimBus.WebApp.ManagementApi.MonitorAcknowledgement;
using SessionStatus = NimBus.WebApp.ManagementApi.SessionStatus;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 046 §4: <c>nimbus_get_worklist</c> over the real MCP pipeline, with the real Insights
/// service over the in-memory store, so the per-caller endpoint filter is what runs (plan D7).
/// The stub authorization grants Reader on CrmEndpoint and ErpEndpoint, never BillingEndpoint,
/// and never a site-wide role.
/// </summary>
[TestClass]
public class WorklistToolsTests
{
    private const string Crm = "CrmEndpoint";
    private const string Erp = "ErpEndpoint";
    private const string Billing = "BillingEndpoint";

    [TestMethod]
    public async Task Two_causes_on_one_endpoint_are_two_groups_largest_first_with_an_actionable_example()
    {
        await using var host = await StartAsync(contributor: true);
        var store = host.Get<InMemoryMessageStore>();
        await FailAsync(store, Crm, "evt-t1", "s1", "Timeout calling ERP for order 100234", minutesAgo: 30);
        await FailAsync(store, Crm, "evt-t2", "s2", "Timeout calling ERP for order 100991", minutesAgo: 20);
        await FailAsync(store, Crm, "evt-c1", "s3", "Customer 4711 not found", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        var groups = Groups(json);
        Assert.HasCount(2, groups);
        Assert.AreEqual(2, groups[0].GetProperty("open").GetInt32());
        Assert.AreEqual(2, groups[0].GetProperty("blockedSessions").GetInt32());
        Assert.AreEqual("evt-t2", groups[0].GetProperty("exampleEventId").GetString(), "The newest open message is the example.");
        Assert.AreEqual(JsonValueKind.String, groups[0].GetProperty("exampleMessageVersion").ValueKind);
        Assert.AreEqual("evt-c1", groups[1].GetProperty("exampleEventId").GetString());

        var options = groups[0].GetProperty("nextActions").EnumerateArray().ToDictionary(o => o.GetProperty("id").GetString()!);
        Assert.AreEqual("investigate", groups[0].GetProperty("nextActions")[0].GetProperty("id").GetString());
        var resubmit = options["resubmit"].GetProperty("arguments");
        Assert.AreEqual("evt-t2", resubmit.GetProperty("eventId").GetString());
        Assert.AreEqual(groups[0].GetProperty("exampleMessageVersion").GetString(), resubmit.GetProperty("messageVersion").GetString());

        // The version the worklist hands over is accepted by the prepare step.
        var prepared = await client.CallToolAsync("nimbus_prepare_action", new Dictionary<string, object?>
        {
            ["action"] = "resubmit", ["endpointId"] = Crm, ["eventId"] = "evt-t2", ["messageVersion"] = resubmit.GetProperty("messageVersion").GetString(),
        });
        Assert.IsFalse(prepared.IsError == true, Text(prepared));
    }

    [TestMethod]
    public async Task A_group_whose_failures_were_resolved_is_left_out_and_counted()
    {
        await using var host = await StartAsync(contributor: true);
        var store = host.Get<InMemoryMessageStore>();
        await FailAsync(store, Crm, "evt-done", "s1", "Timeout calling ERP", minutesAgo: 30, status: ResolutionStatus.Completed);
        await FailAsync(store, Crm, "evt-open", "s2", "Customer 4711 not found", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        Assert.AreEqual("evt-open", Groups(json).Single().GetProperty("exampleEventId").GetString());
        Assert.AreEqual(1, json.GetProperty("resolvedGroups").GetInt32());
    }

    [TestMethod]
    public async Task An_endpoint_the_caller_cannot_read_never_reaches_the_result()
    {
        await using var host = await StartAsync(contributor: false);
        var store = host.Get<InMemoryMessageStore>();
        await FailAsync(store, Billing, "evt-secret", "s1", "Invoice 7 for ACME failed", minutesAgo: 10);
        await FailAsync(store, Crm, "evt-crm", "s2", "Timeout", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());
        var direct = await client.CallToolAsync("nimbus_get_worklist", new Dictionary<string, object?> { ["endpointId"] = Billing });

        Assert.AreEqual(Crm, Groups(json).Single().GetProperty("endpointId").GetString());
        Assert.IsFalse(json.GetRawText().Contains("ACME", StringComparison.Ordinal));
        Assert.IsTrue(direct.IsError == true);
        StringAssert.Contains(Text(direct), "[EndpointNotFound]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_hidden_endpoint_is_left_out_and_a_reader_is_offered_only_reads()
    {
        await using var host = await StartAsync(contributor: false, policy: p =>
        {
            p.Endpoints.Visibility = McpEndpointVisibility.AllExcept;
            p.Endpoints.Hidden = [Erp];
        });
        var store = host.Get<InMemoryMessageStore>();
        await FailAsync(store, Erp, "evt-hidden", "s1", "Timeout", minutesAgo: 10);
        await FailAsync(store, Crm, "evt-crm", "s2", "Timeout", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        var group = Groups(json).Single();
        Assert.AreEqual(Crm, group.GetProperty("endpointId").GetString());
        Assert.IsTrue(group.GetProperty("nextActions").EnumerateArray().All(o => o.GetProperty("kind").GetString() == "read"));
    }

    [TestMethod]
    public async Task An_endpoint_where_changes_are_not_allowed_offers_only_reads()
    {
        await using var host = await StartAsync(contributor: true, policy: p =>
        {
            p.Endpoints.Changes = McpChangeScope.Listed;
            p.Endpoints.ChangeOn = [Erp];
        });
        await FailAsync(host.Get<InMemoryMessageStore>(), Crm, "evt-crm", "s1", "Timeout", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        Assert.IsTrue(Groups(json).Single().GetProperty("nextActions").EnumerateArray().All(o => o.GetProperty("kind").GetString() == "read"));
    }

    [TestMethod]
    public async Task A_group_reported_under_one_ticket_offers_the_resubmit_by_ticket()
    {
        await using var host = await StartAsync(contributor: true);
        var store = host.Get<InMemoryMessageStore>();
        await FailAsync(store, Crm, "evt-1", "s1", "Customer 4711 not found", minutesAgo: 20);
        await FailAsync(store, Crm, "evt-2", "s2", "Customer 4712 not found", minutesAgo: 10);
        await store.SetEventReport(Crm, "evt-1", true, "ops", "INC-7");
        await store.SetEventReport(Crm, "evt-2", true, "ops", "INC-7");
        await using var client = await host.CreateClientAsync();

        var group = Groups(await CallAsync(client, new())).Single();

        Assert.AreEqual("INC-7", group.GetProperty("ticketId").GetString());
        var byTicket = group.GetProperty("nextActions").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "resubmit_ticket");
        Assert.AreEqual("INC-7", byTicket.GetProperty("arguments").GetProperty("ticketId").GetString());
    }

    [TestMethod]
    public async Task Open_failures_older_than_the_period_are_flagged()
    {
        await using var host = await StartAsync(contributor: false, failedCounts: new() { [Crm] = 3 });
        await FailAsync(host.Get<InMemoryMessageStore>(), Crm, "evt-new", "s1", "Timeout", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new() { ["period"] = "1h" });

        Assert.IsTrue(json.GetProperty("olderFailuresLikely").GetBoolean());
        Assert.AreEqual(3, json.GetProperty("totals").GetProperty("failed").GetInt32());
    }

    [TestMethod]
    public async Task An_unknown_period_is_refused()
    {
        await using var host = await StartAsync(contributor: false);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_get_worklist", new Dictionary<string, object?> { ["period"] = "2d" });

        Assert.IsTrue(result.IsError == true);
        StringAssert.Contains(Text(result), "[InvalidArgument]", StringComparison.Ordinal);
    }

    private static async Task FailAsync(InMemoryMessageStore store, string endpoint, string eventId, string session, string error, int minutesAgo,
        ResolutionStatus status = ResolutionStatus.Failed)
    {
        var attempt = $"{eventId}-err";
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId,
            MessageId = attempt,
            EndpointId = endpoint,
            SessionId = session,
            MessageType = MessageType.ErrorResponse,
            EnqueuedTimeUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
            EventTypeId = "CustomerChanged",
            From = endpoint,
            To = "Resolver",
            OriginatingMessageId = $"{eventId}-req",
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventJson = "{}", EventTypeId = "CustomerChanged" },
                ErrorContent = new ErrorContent { ErrorText = error },
            },
        });
        await OperatorCommandTestRows.SeedFailedRowAsync(store, endpoint, eventId, session, attempt, status);
    }

    private static async Task<McpTestHost> StartAsync(bool contributor, Action<McpAccessSettings>? policy = null,
        Dictionary<string, int>? failedCounts = null, IAlertRuleStore? alertRules = null)
    {
        var store = new InMemoryMessageStore();
        if (policy is not null)
        {
            var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
            policy(settings);
            Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, null));
        }

        var endpoints = InterfaceFake<IEndpointApiController>.Create();
        endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountAllAsync), _ => Task.FromResult<ActionResult<IEnumerable<EndpointStatusCount>>>(
            new OkObjectResult(McpTestHost.ReadableEndpoints.Select(id => new EndpointStatusCount
            {
                EndpointId = id, FailedCount = failedCounts?.GetValueOrDefault(id) ?? 0,
            }).ToList())));
        endpoints.On(nameof(IEndpointApiController.GetEndpointSessionIdAsync), args => Task.FromResult<ActionResult<SessionStatus>>(
            new OkObjectResult(new SessionStatus { SessionId = (string)args[1]!, PendingEvents = [], DeferredEvents = [] })));
        var monitor = InterfaceFake<IMonitorApiController>.Create();
        monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync),
            _ => Task.FromResult<ActionResult<IEnumerable<MonitorAcknowledgement>>>(new OkObjectResult(new List<MonitorAcknowledgement>())));

        // The real Insights service; its site-Reader check is denied, which the worklist must not need.
        var insights = new FailedInsightsService(store, store, FailedInsightsServiceTests.NewCache(),
            new FailedInsightsServiceTests.Authorization(siteReader: false), new FailedInsightsServiceTests.FakeEnrichment { IsEnabled = false },
            Options.Create(new FailedInsightsOptions()));

        return await McpTestHost.StartLocalDevelopmentAsync(services =>
        {
            services.AddSingleton(new McpTestHost.StubAccess { Contributor = contributor });
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
            services.AddSingleton<IOperatorOperationStore>(store);
            services.AddSingleton(endpoints.Instance);
            services.AddSingleton(monitor.Instance);
            services.AddSingleton<IFailedInsightsQuery>(insights);
            services.AddSingleton<IAlertRuleStore>(alertRules ?? store);
        });
    }

    private static async Task FireRuleAsync(InMemoryMessageStore store, string endpointId, string ruleId, string name, int minutesAgo)
    {
        var at = DateTime.UtcNow.AddMinutes(-minutesAgo);
        var rule = (await store.TrySaveAlertRuleDefinition(new AlertRule
        {
            RuleId = ruleId,
            EndpointId = endpointId,
            Name = name,
            Enabled = true,
            Condition = new AlertRuleCondition { Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 5, WindowMinutes = 15 },
            ResolveAfterMinutes = 5,
            Targets = [new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Mail, Address = "team@example.com" }],
            CreatedBy = "owner",
            CreatedAtUtc = at,
            UpdatedAtUtc = at,
        }, expectedVersion: null))!;
        await store.TrySaveAlertRuleState(endpointId, ruleId, rule.Version,
            new AlertRuleState { Status = AlertRuleStatus.Firing, SinceUtc = at, LastValue = 5 });
    }

    [TestMethod]
    public async Task Firing_alert_rules_on_readable_endpoints_are_listed_longest_firing_first()
    {
        await using var host = await StartAsync(contributor: false, policy: p =>
        {
            p.Endpoints.Visibility = McpEndpointVisibility.AllExcept;
            p.Endpoints.Hidden = [Erp];
        });
        var store = host.Get<InMemoryMessageStore>();
        await FireRuleAsync(store, Crm, "r-new", "CRM spike", minutesAgo: 5);
        await FireRuleAsync(store, Crm, "r-old", "CRM backlog", minutesAgo: 40);
        await FireRuleAsync(store, Erp, "r-hidden", "ERP hidden rule", minutesAgo: 10);
        await FireRuleAsync(store, Billing, "r-secret", "Billing secret rule", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        var rules = json.GetProperty("firingRules").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "r-old", "r-new" }, rules.Select(r => r.GetProperty("ruleId").GetString()).ToArray());
        Assert.AreEqual("5 or more failures in 15 min", rules[0].GetProperty("condition").GetString());
        Assert.AreEqual("failure_occurrences", rules[0].GetProperty("kind").GetString());
        Assert.AreEqual(5, rules[0].GetProperty("value").GetInt32());
        var action = rules[0].GetProperty("nextActions").EnumerateArray().Single();
        Assert.AreEqual("open_web_ui", action.GetProperty("id").GetString());
        StringAssert.EndsWith(action.GetProperty("url").GetString(), "/Endpoints/Details/CrmEndpoint?tab=alerts");
        Assert.IsFalse(json.GetRawText().Contains("secret", StringComparison.OrdinalIgnoreCase), "Unreadable endpoints' rules never appear.");
        Assert.IsFalse(json.GetRawText().Contains("ERP hidden rule", StringComparison.Ordinal), "Hidden endpoints' rules never appear.");
        Assert.IsFalse(json.GetRawText().Contains("team@example.com", StringComparison.Ordinal), "Targets never appear.");
    }

    [TestMethod]
    public async Task Firing_rules_are_always_emitted_and_empty_when_the_store_fails()
    {
        await using var host = await StartAsync(contributor: false, alertRules: new FailingAlertRules());
        await FailAsync(host.Get<InMemoryMessageStore>(), Crm, "evt-crm", "s1", "Timeout", minutesAgo: 10);
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, new());

        Assert.AreEqual(0, json.GetProperty("firingRules").GetArrayLength());
        Assert.HasCount(1, Groups(json), "The groups stand without the rules.");
    }

    private sealed class FailingAlertRules : IAlertRuleStore
    {
        private static Exception Down() => new InvalidOperationException("store down");

        public Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId) => throw Down();
        public Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit) => throw Down();
        public Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules() => throw Down();
        public Task<AlertRule?> GetAlertRule(string endpointId, string ruleId) => throw Down();
        public Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion) => throw Down();
        public Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state) => throw Down();
        public Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion) => throw Down();
        public Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId) => throw Down();
        public Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc) => throw Down();
        public Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus() => throw Down();
    }

    private static async Task<JsonElement> CallAsync(McpClient client, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync("nimbus_get_worklist", arguments);
        Assert.IsFalse(result.IsError == true, Text(result));
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static JsonElement[] Groups(JsonElement json) => json.GetProperty("groups").EnumerateArray().ToArray();

    private static string Text(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
