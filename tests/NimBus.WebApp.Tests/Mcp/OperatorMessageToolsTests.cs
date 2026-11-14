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
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// The endpoint and message read tools, over the real MCP pipeline in local development mode.
/// The REST implementations they delegate to are faked, so these tests pin the MCP contract:
/// which endpoint is asked for, what is projected, what is withheld and how errors surface.
/// The stub authorization grants Reader on CrmEndpoint and ErpEndpoint only.
/// </summary>
[TestClass]
public class OperatorMessageToolsTests
{
    private InterfaceFake<IEndpointApiController> _endpoints = null!;
    private InterfaceFake<IEventApiController> _events = null!;
    private InterfaceFake<IMonitorApiController> _monitor = null!;
    private readonly NimBus.Testing.Conformance.InMemoryMessageStore _metadata = new();

    [TestInitialize]
    public void CreateFakes()
    {
        _endpoints = InterfaceFake<IEndpointApiController>.Create();
        _events = InterfaceFake<IEventApiController>.Create();
        _monitor = InterfaceFake<IMonitorApiController>.Create();
    }

    [TestMethod]
    public async Task Overview_totals_available_endpoints_and_never_reports_an_unavailable_one_as_zero()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountAllAsync), _ => Ok<IEnumerable<EndpointStatusCount>>(
        [
            new EndpointStatusCount { EndpointId = "CrmEndpoint", FailedCount = 3, PendingCount = 1, OldestFailureAt = new DateTime(2026, 9, 25, 8, 0, 0, DateTimeKind.Utc) },
            new EndpointStatusCount { EndpointId = "ErpEndpoint", StorageStatus = "unavailable" },
        ]));
        _monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync), _ => Ok<IEnumerable<MonitorAcknowledgement>>(
        [
            new MonitorAcknowledgement { EndpointId = "CrmEndpoint", Reason = "Known outage", AcknowledgedBy = "ops@example.com", ExpiresAt = DateTime.UtcNow.AddHours(1) },
        ]));

        var json = await CallAsync("nimbus_get_overview");

        Assert.AreEqual("authorizedEndpoints", json.GetProperty("scope").GetString());
        Assert.AreEqual(3, json.GetProperty("totals").GetProperty("failed").GetInt32());
        Assert.IsTrue(json.GetProperty("partial").GetBoolean());
        CollectionAssert.AreEqual(new[] { "ErpEndpoint" }, Strings(json.GetProperty("unavailableEndpoints")));

        var endpoints = json.GetProperty("endpoints").EnumerateArray().ToDictionary(e => e.GetProperty("endpointId").GetString()!);
        Assert.AreEqual("Known outage", endpoints["CrmEndpoint"].GetProperty("acknowledgement").GetProperty("reason").GetString());
        Assert.AreEqual(JsonValueKind.Null, endpoints["ErpEndpoint"].GetProperty("counts").ValueKind, "An unavailable endpoint must not report counts.");
    }

    [TestMethod]
    public async Task Overview_writes_null_members_because_the_output_schema_requires_them()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountAllAsync), _ => Ok<IEnumerable<EndpointStatusCount>>(
        [
            new EndpointStatusCount { EndpointId = "CrmEndpoint" },
        ]));
        _monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync), _ => Ok<IEnumerable<MonitorAcknowledgement>>([]));

        var json = await CallAsync("nimbus_get_overview");

        var crm = json.GetProperty("endpoints").EnumerateArray().Single();
        foreach (var member in new[] { "oldestFailureAt", "subscriptionStatus", "acknowledgement" })
        {
            Assert.IsTrue(crm.TryGetProperty(member, out var value), $"'{member}' must be written, not omitted.");
            Assert.AreEqual(JsonValueKind.Null, value.ValueKind, member);
        }
    }

    [TestMethod]
    public async Task Get_endpoint_returns_the_recorded_ownership_and_code_location()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountIdAsync), args => Ok(new EndpointStatusCount { EndpointId = (string)args[0]! }));
        _monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync), _ => Ok<IEnumerable<MonitorAcknowledgement>>([]));
        await _metadata.SetEndpointMetadata(new NimBus.MessageStore.States.EndpointMetadata
        {
            EndpointId = "CrmEndpoint",
            EndpointOwner = "Alice",
            EndpointOwnerTeam = "CRM team",
            TechnicalContacts = [new NimBus.MessageStore.States.TechnicalContact { Name = "Ops", Email = "ops@example.com" }],
            SourceSystem = "Dynamics 365 Sales",
            RepositoryUrl = "https://github.com/example/crm-adapter",
            TicketLinkTemplate = "https://github.com/example/crm-adapter/issues/{ticketId}",
        });

        var ownership = (await CallAsync("nimbus_get_endpoint", new() { ["endpointId"] = "CrmEndpoint" })).GetProperty("ownership");

        Assert.AreEqual("Alice", ownership.GetProperty("owner").GetString());
        Assert.AreEqual("Dynamics 365 Sales", ownership.GetProperty("sourceSystem").GetString());
        Assert.AreEqual("https://github.com/example/crm-adapter", ownership.GetProperty("repositoryUrl").GetString());
        Assert.AreEqual("Ops", ownership.GetProperty("technicalContacts")[0].GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task Get_endpoint_without_recorded_ownership_returns_null_not_an_error()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountIdAsync), args => Ok(new EndpointStatusCount { EndpointId = (string)args[0]! }));
        _monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync), _ => Ok<IEnumerable<MonitorAcknowledgement>>([]));

        var json = await CallAsync("nimbus_get_endpoint", new() { ["endpointId"] = "CrmEndpoint" });

        Assert.AreEqual(JsonValueKind.Null, json.GetProperty("ownership").ValueKind);
    }

    [TestMethod]
    public async Task Get_endpoint_resolves_the_endpoint_id_case_insensitively()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointStatusCountIdAsync), args =>
            Ok(new EndpointStatusCount { EndpointId = (string)args[0]!, DeferredCount = 2 }));
        _monitor.On(nameof(IMonitorApiController.GetMonitorAcknowledgementsAsync), _ => Ok<IEnumerable<MonitorAcknowledgement>>([]));

        var json = await CallAsync("nimbus_get_endpoint", new() { ["endpointId"] = "crmendpoint" });

        Assert.AreEqual("CrmEndpoint", _endpoints.Calls.Single().Args[0]);
        Assert.AreEqual(2, json.GetProperty("endpoint").GetProperty("counts").GetProperty("deferred").GetInt32());
    }

    [TestMethod]
    [DataRow("nimbus_get_endpoint")]
    [DataRow("nimbus_find_messages")]
    public async Task Tools_refuse_an_endpoint_the_caller_cannot_read_without_calling_the_store(string tool)
    {
        var result = await CallRawAsync(tool, new() { ["endpointId"] = "BillingEndpoint" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "EndpointNotFound", StringComparison.Ordinal);
        Assert.IsTrue(_endpoints.Calls.IsEmpty && _events.Calls.IsEmpty);
    }

    [TestMethod]
    public async Task Find_messages_passes_typed_filters_and_caps_the_page_size()
    {
        SearchRequest? request = null;
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args =>
        {
            request = (SearchRequest)args[0]!;
            return Ok(new SearchResponse { Events = [], ContinuationToken = null });
        });

        await CallAsync("nimbus_find_messages", new()
        {
            ["endpointId"] = "CrmEndpoint",
            ["statuses"] = new[] { "Failed", "DeadLettered" },
            ["eventTypeId"] = "CustomerChanged",
            ["sessionId"] = "customer-42",
            ["limit"] = 5000,
        });

        Assert.IsNotNull(request);
        Assert.AreEqual(200, request.MaxSearchItemsCount);
        Assert.AreEqual("CrmEndpoint", request.EventFilter.EndpointId);
        CollectionAssert.AreEqual(new[] { ResolutionStatus.Failed, ResolutionStatus.DeadLettered }, request.EventFilter.ResolutionStatus.ToArray());
        CollectionAssert.AreEqual(new[] { "CustomerChanged" }, request.EventFilter.EventTypeId.ToArray());
        Assert.AreEqual("customer-42", request.EventFilter.SessionId);
    }

    [TestMethod]
    public async Task Find_messages_rejects_an_unknown_status()
    {
        var result = await CallRawAsync("nimbus_find_messages", new() { ["endpointId"] = "CrmEndpoint", ["statuses"] = new[] { "Exploded" } });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "InvalidArgument", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Find_messages_projects_metadata_without_payloads()
    {
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), _ => Ok(new SearchResponse
        {
            Events =
            [
                new Event
                {
                    EventId = "event-1042", SessionId = "customer-42", EventTypeId = "CustomerChanged",
                    ResolutionStatus = "Failed", LastMessageId = "attempt-3", RetryCount = 3,
                    // The SQL provider returns UTC values with an unspecified kind.
                    UpdatedAt = new DateTime(2026, 9, 25, 20, 46, 47, DateTimeKind.Unspecified),
                    MessageContent = new MessageContent { EventContent = new EventContent { EventJson = "{\"ssn\":\"123-45-6789\"}" } },
                },
            ],
        }));

        var raw = await CallRawAsync("nimbus_find_messages", new() { ["endpointId"] = "CrmEndpoint" });
        var json = StructuredContent(raw);

        var item = json.GetProperty("messages").EnumerateArray().Single();
        Assert.AreEqual("event-1042", item.GetProperty("eventId").GetString());
        Assert.AreEqual("Failed", item.GetProperty("status").GetString());
        Assert.AreEqual("attempt-3", item.GetProperty("latestMessageId").GetString());
        Assert.AreEqual("2026-09-25T20:46:47Z", item.GetProperty("updatedAt").GetString(), "Timestamps must be marked as UTC.");
        Assert.IsFalse(json.GetRawText().Contains("123-45-6789", StringComparison.Ordinal), "Payloads must not be returned.");
    }

    [TestMethod]
    public async Task Find_messages_cursor_round_trips_and_is_bound_to_the_query()
    {
        var tokens = new List<string?>();
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), args =>
        {
            tokens.Add(((SearchRequest)args[0]!).ContinuationToken);
            return Ok(new SearchResponse { Events = [], ContinuationToken = "store-token-2" });
        });

        var first = await CallAsync("nimbus_find_messages", new() { ["endpointId"] = "CrmEndpoint", ["statuses"] = new[] { "Failed" } });
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.IsFalse(string.IsNullOrEmpty(cursor));
        Assert.IsFalse(cursor.Contains("store-token-2", StringComparison.Ordinal), "The cursor must be opaque.");

        await CallAsync("nimbus_find_messages", new() { ["endpointId"] = "CrmEndpoint", ["statuses"] = new[] { "Failed" }, ["cursor"] = cursor });
        var reused = await CallRawAsync("nimbus_find_messages", new() { ["endpointId"] = "CrmEndpoint", ["statuses"] = new[] { "Pending" }, ["cursor"] = cursor });

        CollectionAssert.AreEqual(new string?[] { null, "store-token-2" }, tokens);
        Assert.IsTrue(reused.IsError);
        StringAssert.Contains(ErrorText(reused), "InvalidCursor", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Get_message_combines_status_and_the_latest_error_with_a_web_ui_link()
    {
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), _ => Ok(new SearchResponse
        {
            Events = [new Event { EventId = "event-1042", ResolutionStatus = "Failed", LastMessageId = "attempt-3", SessionId = "customer-42" }],
        }));
        _events.On(nameof(IEventApiController.GetEventDetailsIdAsync), _ => Ok(new EventDetails
        {
            FailedMessage = new Message
            {
                MessageId = "attempt-3",
                ErrorContent = new MessageErrorContent { ErrorType = "HttpRequestException", ErrorText = new string('x', 5000), ExceptionStackTrace = "at Secret.Internals()" },
            },
        }));

        var json = await CallAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1042" });

        Assert.AreEqual("Failed", json.GetProperty("status").GetString());
        var error = json.GetProperty("latestError");
        Assert.AreEqual("HttpRequestException", error.GetProperty("errorType").GetString());
        Assert.AreEqual(2000, error.GetProperty("errorText").GetString()!.Length);
        Assert.IsTrue(error.GetProperty("errorTextTruncated").GetBoolean());
        Assert.IsFalse(json.GetRawText().Contains("Secret.Internals", StringComparison.Ordinal), "Stack traces must not be returned.");
        StringAssert.EndsWith(json.GetProperty("webUiUrl").GetString(), "/Message/Index/CrmEndpoint/event-1042", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow(403)]
    [DataRow(404)]
    public async Task Get_message_reports_forbidden_and_missing_messages_the_same_way(int status)
    {
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), _ =>
            Task.FromResult<ActionResult<SearchResponse>>(new StatusCodeResult(status)));

        var result = await CallRawAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-9" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "MessageNotFound", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Get_message_reports_a_message_the_search_does_not_find()
    {
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), _ => Ok(new SearchResponse { Events = [] }));

        var result = await CallRawAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-9" });

        StringAssert.Contains(ErrorText(result), "MessageNotFound", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task History_keeps_only_the_requested_endpoint_and_omits_payloads()
    {
        _events.On(nameof(IEventApiController.GetEventDetailsHistoryIdAsync), _ => Ok<IEnumerable<Message>>(
        [
            new Message { MessageId = "attempt-2", EndpointId = "CrmEndpoint", MessageType = MessageType.ErrorResponse, EventContent = "{\"ssn\":\"123-45-6789\"}" },
            new Message { MessageId = "other-endpoint", EndpointId = "ErpEndpoint", MessageType = MessageType.EventRequest },
        ]));
        _events.On(nameof(IEventApiController.GetEventDetailsLogsIdAsync), _ => Ok<IEnumerable<EventLogEntry>>(
        [
            new EventLogEntry { Text = "Handler failed", MessageId = "attempt-2", Payload = "{\"ssn\":\"123-45-6789\"}" },
        ]));

        var json = await CallAsync("nimbus_get_message_history", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1042" });

        CollectionAssert.AreEqual(new[] { "attempt-2" }, json.GetProperty("attempts").EnumerateArray().Select(a => a.GetProperty("messageId").GetString()).ToArray());
        Assert.AreEqual("Handler failed", json.GetProperty("logs").EnumerateArray().Single().GetProperty("text").GetString());
        Assert.IsFalse(json.GetRawText().Contains("123-45-6789", StringComparison.Ordinal), "Payloads must not be returned.");
    }

    [TestMethod]
    public async Task Session_lists_pending_and_deferred_events()
    {
        _endpoints.On(nameof(IEndpointApiController.GetEndpointSessionIdAsync), args => Ok(new SessionStatus
        {
            SessionId = (string)args[1]!,
            PendingEvents = ["event-2", "event-3"],
            DeferredEvents = ["event-4"],
        }));

        var json = await CallAsync("nimbus_get_session", new() { ["endpointId"] = "ErpEndpoint", ["sessionId"] = "customer-42" });

        Assert.AreEqual("customer-42", json.GetProperty("sessionId").GetString());
        CollectionAssert.AreEqual(new[] { "event-2", "event-3" }, Strings(json.GetProperty("pendingEventIds")));
        CollectionAssert.AreEqual(new[] { "event-4" }, Strings(json.GetProperty("deferredEventIds")));
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var result = await CallRawAsync(tool, arguments);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
        return StructuredContent(result);
    }

    private async Task<CallToolResult> CallRawAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync(services =>
        {
            services.AddSingleton(_endpoints.Instance);
            services.AddSingleton(_events.Instance);
            services.AddSingleton(_monitor.Instance);
            services.AddSingleton<NimBus.MessageStore.Abstractions.IEndpointMetadataStore>(_metadata);
        });
        await using var client = await host.CreateClientAsync();
        return await client.CallToolAsync(tool, arguments ?? []);
    }

    private static Task<ActionResult<T>> Ok<T>(T value) => Task.FromResult<ActionResult<T>>(new OkObjectResult(value));

    private static JsonElement StructuredContent(CallToolResult result)
    {
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static string?[] Strings(JsonElement array) => array.EnumerateArray().Select(e => e.GetString()).ToArray();
}
