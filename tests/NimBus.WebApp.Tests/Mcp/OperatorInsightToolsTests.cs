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
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Operations;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Cross-endpoint search, metrics, failure classification and the payload reveal, over the
/// real MCP pipeline with the REST implementations faked. Site-wide reads keep the site
/// Reader floor; payloads need PiiReader and, for Entra callers, the payload scope.
/// </summary>
[TestClass]
public class OperatorInsightToolsTests
{
    private const string PayloadJson = "{\"orderId\":\"o-1\",\"ssn\":\"123-45-6789\"}";

    private InterfaceFake<IMessageApiController> _messages = null!;
    private InterfaceFake<IMetricsApiController> _metrics = null!;
    private InterfaceFake<NimBus.WebApp.Services.Insights.IFailedInsightsQuery> _failedInsights = null!;
    private InterfaceFake<IEventApiController> _events = null!;
    private FakeClassifications _classifications = null!;

    [TestInitialize]
    public void CreateFakes()
    {
        _messages = InterfaceFake<IMessageApiController>.Create();
        _metrics = InterfaceFake<IMetricsApiController>.Create();
        _failedInsights = InterfaceFake<NimBus.WebApp.Services.Insights.IFailedInsightsQuery>.Create();
        _events = InterfaceFake<IEventApiController>.Create();
        _classifications = new FakeClassifications();
    }

    [TestMethod]
    public async Task Search_messages_requires_the_site_reader_floor()
    {
        _messages.On(nameof(IMessageApiController.PostMessagesSearchAsync), _ =>
            Task.FromResult<ActionResult<MessageSearchResponse>>(new ForbidResult()));

        var result = await CallRawAsync("nimbus_search_messages", new() { ["eventId"] = "event-1" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "PermissionDenied");
    }

    [TestMethod]
    public async Task Search_messages_passes_typed_filters_caps_the_page_and_omits_payloads()
    {
        MessageSearchRequest? request = null;
        _messages.On(nameof(IMessageApiController.PostMessagesSearchAsync), args =>
        {
            request = (MessageSearchRequest)args[0]!;
            return Ok(new MessageSearchResponse
            {
                Messages = [new Message { EventId = "event-1", MessageId = "m-1", EndpointId = "ErpEndpoint", MessageType = MessageType.EventRequest, EventContent = PayloadJson }],
                ContinuationToken = "next",
            });
        });

        var json = await CallAsync("nimbus_search_messages", new()
        {
            ["sessionId"] = "customer-42",
            ["eventTypeId"] = "CustomerChanged",
            ["senderEndpoint"] = "CrmEndpoint",
            ["messageType"] = "EventRequest",
            ["limit"] = 999,
        }, siteReader: true);

        Assert.IsNotNull(request);
        Assert.AreEqual(200, request.MaxItemCount);
        Assert.AreEqual("customer-42", request.Filter.SessionId);
        Assert.AreEqual("CrmEndpoint", request.Filter.SenderEndpoint);
        CollectionAssert.AreEqual(new[] { "CustomerChanged" }, request.Filter.EventTypeId.ToArray());
        Assert.AreEqual(MessageSearchFilterMessageType.EventRequest, request.Filter.MessageType);
        Assert.AreEqual("m-1", json.GetProperty("messages").EnumerateArray().Single().GetProperty("messageId").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(json.GetProperty("nextCursor").GetString()));
        Assert.IsFalse(json.GetRawText().Contains("123-45-6789", StringComparison.Ordinal), "Payloads must not be returned.");
    }

    [TestMethod]
    public async Task Metrics_throughput_maps_the_period_and_requires_site_reader()
    {
        Period? period = null;
        _metrics.On(nameof(IMetricsApiController.GetMetricsOverviewAsync), args =>
        {
            period = (Period)args[0]!;
            return Ok(new MetricsOverview
            {
                Published = [new EndpointEventTypeMessageCount { EndpointId = "CrmEndpoint", EventTypeId = "CustomerChanged", Count = 12 }],
                Handled = [],
                Failed = [new EndpointEventTypeMessageCount { EndpointId = "ErpEndpoint", EventTypeId = "CustomerChanged", Count = 2 }],
            });
        });

        var json = await CallAsync("nimbus_get_metrics", new() { ["view"] = "throughput", ["period"] = "7d" }, siteReader: true);

        Assert.AreEqual(Period._7d, period);
        Assert.AreEqual(12, json.GetProperty("throughput").GetProperty("published").EnumerateArray().Single().GetProperty("count").GetInt32());
        Assert.AreEqual(2, json.GetProperty("throughput").GetProperty("failed").EnumerateArray().Single().GetProperty("count").GetInt32());
    }

    [TestMethod]
    public async Task Metrics_failures_truncate_example_error_text()
    {
        NimBus.WebApp.Services.Insights.FailedInsightsRequest? request = null;
        _failedInsights.On(nameof(NimBus.WebApp.Services.Insights.IFailedInsightsQuery.GetOverviewAsync), args =>
        {
            request = (NimBus.WebApp.Services.Insights.FailedInsightsRequest)args[0]!;
            return Task.FromResult(new NimBus.WebApp.Services.Insights.FailedInsightsQueryResult(
                NimBus.WebApp.Services.Insights.FailedInsightsQueryStatus.Ok, new FailedInsightsOverview
                {
                    TotalFailed = 5,
                    Groups = [new ErrorPatternGroup { ErrorCategory = "Timeout", Count = 5, Endpoints = ["ErpEndpoint"], EventTypes = ["CustomerChanged"], ExampleErrorText = new string('e', 3000) }],
                }));
        });

        var json = await CallAsync("nimbus_get_metrics", new() { ["view"] = "failures" }, siteReader: true);

        var group = json.GetProperty("failures").GetProperty("groups").EnumerateArray().Single();
        Assert.AreEqual("Timeout", group.GetProperty("errorCategory").GetString());
        Assert.AreEqual(500, group.GetProperty("exampleErrorText").GetString()!.Length);
        Assert.AreEqual(5, json.GetProperty("failures").GetProperty("totalFailed").GetInt32());
        // Spec 043 §8.4: the MCP failures view never reads classification storage.
        Assert.IsFalse(request!.Enrich);
    }

    [TestMethod]
    public async Task Metrics_failures_without_site_reader_report_the_role_requirement()
    {
        _failedInsights.On(nameof(NimBus.WebApp.Services.Insights.IFailedInsightsQuery.GetOverviewAsync), _ =>
            Task.FromResult(new NimBus.WebApp.Services.Insights.FailedInsightsQueryResult(
                NimBus.WebApp.Services.Insights.FailedInsightsQueryStatus.Forbidden, null)));

        var result = await CallRawAsync("nimbus_get_metrics", new() { ["view"] = "failures" }, siteReader: true);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "PermissionDenied");
    }

    [TestMethod]
    [DataRow("view", "everything")]
    [DataRow("period", "2w")]
    public async Task Metrics_reject_unknown_views_and_periods(string argument, string value)
    {
        var result = await CallRawAsync("nimbus_get_metrics", new() { [argument] = value }, siteReader: true);

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "InvalidArgument");
    }

    [TestMethod]
    public async Task Metrics_without_site_reader_are_denied()
    {
        _metrics.On(nameof(IMetricsApiController.GetMetricsOverviewAsync), _ =>
            Task.FromResult<ActionResult<MetricsOverview>>(new ForbidResult()));

        var result = await CallRawAsync("nimbus_get_metrics", new());

        StringAssert.Contains(ErrorText(result), "PermissionDenied");
    }

    [TestMethod]
    public async Task Classification_is_unavailable_when_the_feature_is_off()
    {
        _classifications.Available = false;

        var result = await CallRawAsync("nimbus_get_classification", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["messageId"] = "m-1" });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "FeatureUnavailable");
    }

    [TestMethod]
    public async Task Classification_defaults_to_the_latest_attempt_and_is_marked_advisory()
    {
        TrackedFailure("event-1", lastMessageId: "attempt-3");
        _classifications.Result = Classification("CrmEndpoint", "event-1", "attempt-3");

        var json = await CallAsync("nimbus_get_classification", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1" });

        Assert.AreEqual(("event-1", "attempt-3"), _classifications.LastRequest);
        Assert.IsTrue(json.GetProperty("classified").GetBoolean());
        Assert.IsTrue(json.GetProperty("advisory").GetBoolean());
        var classification = json.GetProperty("classification");
        Assert.AreEqual("Transient", classification.GetProperty("category").GetString());
        Assert.AreEqual("RetryMayHelp", classification.GetProperty("guidance").GetString());
        Assert.AreEqual("attempt-3", classification.GetProperty("sourceMessageId").GetString());
    }

    [TestMethod]
    public async Task Classification_reports_an_unclassified_failure()
    {
        var json = await CallAsync("nimbus_get_classification", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["messageId"] = "m-1" });

        Assert.IsFalse(json.GetProperty("classified").GetBoolean());
    }

    [TestMethod]
    public async Task Classification_for_another_endpoint_is_not_found()
    {
        _classifications.Result = Classification("BillingEndpoint", "event-1", "m-1");

        var result = await CallRawAsync("nimbus_get_classification", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["messageId"] = "m-1" });

        StringAssert.Contains(ErrorText(result), "MessageNotFound");
    }

    [TestMethod]
    public async Task Payload_requires_pii_reader()
    {
        TrackedFailure("event-1", lastMessageId: "attempt-3");

        var result = await CallRawAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["includePayload"] = true });

        Assert.IsTrue(result.IsError);
        StringAssert.Contains(ErrorText(result), "PermissionDenied");
    }

    [TestMethod]
    public async Task Payload_is_returned_to_a_local_pii_reader_on_request_only()
    {
        TrackedFailure("event-1", lastMessageId: "attempt-3");

        var withPayload = await CallAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["includePayload"] = true }, piiReader: true);
        var withoutPayload = await CallAsync("nimbus_get_message", new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1" }, piiReader: true);

        Assert.AreEqual(PayloadJson, withPayload.GetProperty("payload").GetProperty("json").GetString());
        Assert.IsFalse(withoutPayload.GetRawText().Contains("123-45-6789", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("nimbus.observe", null, false)]
    [DataRow(null, "Nimbus.Observe", false)]
    [DataRow("nimbus.observe nimbus.payload.read", null, true)]
    public async Task Entra_payload_needs_the_delegated_payload_scope(string? scopes, string? roles, bool allowed)
    {
        TrackedFailure("event-1", lastMessageId: "attempt-3");
        await using var host = await McpTestHost.StartEntraAsync(services => Register(services, piiReader: true, siteReader: false));
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: scopes, roles: roles));

        var result = await client.CallToolAsync("nimbus_get_message", new Dictionary<string, object?> { ["endpointId"] = "CrmEndpoint", ["eventId"] = "event-1", ["includePayload"] = true });
        var capabilities = JsonSerializer.SerializeToElement((await client.CallToolAsync("nimbus_get_capabilities")).StructuredContent);

        Assert.AreEqual(!allowed, result.IsError == true, ErrorText(result));
        Assert.AreEqual(allowed, capabilities.GetProperty("caller").GetProperty("canReadPayloads").GetBoolean());
    }

    [TestMethod]
    public async Task Entra_metadata_advertises_the_payload_scope()
    {
        await using var host = await McpTestHost.StartEntraAsync();

        using var response = await host.Server.CreateClient().GetAsync("/.well-known/oauth-protected-resource/mcp");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var scopes = document.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.Contains(scopes, $"api://{McpTestHost.ClientId}/nimbus.payload.read");
    }

    private void TrackedFailure(string eventId, string lastMessageId)
    {
        _events.On(nameof(IEventApiController.PostApiEventEndpointIdGetByFilterAsync), _ => Ok(new SearchResponse
        {
            Events = [new Event { EventId = eventId, ResolutionStatus = "Failed", LastMessageId = lastMessageId, EventTypeId = "CustomerChanged" }],
        }));
        _events.On(nameof(IEventApiController.GetEventDetailsIdAsync), _ => Ok(new EventDetails
        {
            FailedMessage = new Message { MessageId = lastMessageId, ErrorContent = new MessageErrorContent { ErrorText = "boom" } },
            OriginatingMessage = new Message { MessageId = "original", EventTypeId = "CustomerChanged", EventContent = PayloadJson },
        }));
    }

    private static FailureClassification Classification(string endpointId, string eventId, string messageId) => new()
    {
        Id = "c-1",
        FailureMessageId = messageId,
        Revision = 1,
        EventId = eventId,
        EventTypeId = "CustomerChanged",
        EndpointId = endpointId,
        Provider = "anthropic",
        Model = "claude-test",
        QuestionSetVersion = 1,
        Category = "Transient",
        CategoryConfidence = 0.9,
        CategoryProbabilities = new Dictionary<string, double> { ["Transient"] = 0.9 },
        RetryLikelihood = 0.8,
        ChangeRequiredLikelihood = 0.1,
        ExternalDependencyLikelihood = 0.6,
        Guidance = FailureGuidance.RetryMayHelp,
        EventPayloadIncluded = false,
        RequestedBy = "ops@example.com",
        CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    private void Register(IServiceCollection services, bool piiReader, bool siteReader)
    {
        services.AddSingleton(new McpTestHost.StubAccess { PiiReader = piiReader, SiteReader = siteReader });
        services.AddSingleton(_messages.Instance);
        services.AddSingleton(_metrics.Instance);
        services.AddSingleton(_failedInsights.Instance);
        services.AddSingleton(_events.Instance);
        services.AddSingleton<IOperatorClassificationSource>(_classifications);
    }

    private async Task<JsonElement> CallAsync(string tool, Dictionary<string, object?> arguments, bool siteReader = false, bool piiReader = false)
    {
        var result = await CallRawAsync(tool, arguments, siteReader, piiReader);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private async Task<CallToolResult> CallRawAsync(string tool, Dictionary<string, object?> arguments, bool siteReader = false, bool piiReader = false)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync(services => Register(services, piiReader, siteReader));
        await using var client = await host.CreateClientAsync();
        return await client.CallToolAsync(tool, arguments);
    }

    private static Task<ActionResult<T>> Ok<T>(T value) => Task.FromResult<ActionResult<T>>(new OkObjectResult(value));

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class FakeClassifications : IOperatorClassificationSource
    {
        public bool Available { get; set; } = true;

        public FailureClassification? Result { get; set; }

        public (string EventId, string MessageId)? LastRequest { get; private set; }

        public bool IsAvailable => Available;

        public Task<FailureClassification?> GetLatestAsync(string eventId, string messageId, CancellationToken cancellationToken)
        {
            LastRequest = (eventId, messageId);
            return Task.FromResult(Result);
        }

        public Task<(FailureClassification Result, bool Cached)> AnalyzeAsync(string endpointId, string eventId, string messageId,
            string idempotencyKey, bool force, CancellationToken cancellationToken)
            => throw new NotSupportedException("The insight tools only read classifications.");
    }
}
