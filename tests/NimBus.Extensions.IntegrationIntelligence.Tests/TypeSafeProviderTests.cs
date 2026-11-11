#pragma warning disable CA1707, CA2007, CS0618 // CS0618: these tests cover the deprecated provider (Spec 044 §6.10).
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.Extensions.IntegrationIntelligence.Providers;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class TypeSafeProviderTests
{
    [TestMethod]
    public async Task Provider_Parses_Validated_Response_And_Sends_One_Bounded_Request()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"jev-1.13.0","answers":{"failure_category":{"choice":"transient_dependency","confidence":0.91,"probabilities":{"transient_dependency":0.91,"authentication_configuration":0,"contract_schema":0,"business_rule":0.09,"missing_reference_data":0,"application_defect":0,"messaging_platform":0,"unknown":0}},"retry_likely_to_succeed_unchanged":{"noul":0.82},"change_required_before_success":{"noul":0.12},"external_dependency_involved":{"noul":0.97}},"usage":{"input_tokens":123,"output_tokens":45}}
                    """, Encoding.UTF8, "application/json"),
            });
        var options = new FailureClassificationOptions { ApiKey = "test-key", TimeoutSeconds = 2 };
        var provider = CreateProvider(handler, options);

        var result = await provider.ClassifyAsync(CreateInput());

        Assert.AreEqual("transient_dependency", result.Category);
        Assert.AreEqual(0.82, result.RetryLikelihood, 0.001);
        Assert.AreEqual(123, result.InputTokens);
        Assert.AreEqual(1, handler.Requests);
        Assert.AreEqual("Bearer test-key", handler.LastAuthorization);
        var state = JsonNode.Parse(handler.LastBody!)!["state"]!;
        if (state is JsonValue) state = JsonNode.Parse(state.GetValue<string>())!;
        Assert.IsTrue(JsonNode.DeepEquals(JsonNode.Parse("""
            {"failure":{"eventType":"orders.created","endpoint":"orders","status":"Failed","retryCount":null,"retryLimit":null,"exception":{"type":"HttpRequestException","message":"temporary outage","source":"client"},"deadLetterReason":null},"recentHistory":[],"eventPayload":null}
            """), state), state.ToJsonString());
    }

    [TestMethod]
    public async Task Provider_Retries_429_Twice_Then_Parses_The_Answer()
    {
        var handler = new QueueHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.TooManyRequests),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"model":"jev-1.13.0","answers":{"failure_category":{"choice":"unknown","confidence":0.60,"probabilities":{"transient_dependency":0,"authentication_configuration":0,"contract_schema":0,"business_rule":0,"missing_reference_data":0,"application_defect":0,"messaging_platform":0,"unknown":1.0}},"retry_likely_to_succeed_unchanged":{"noul":0.1},"change_required_before_success":{"noul":0.1},"external_dependency_involved":{"noul":0.1}}}
                    """, Encoding.UTF8, "application/json"),
            });
        var provider = CreateProvider(handler, new FailureClassificationOptions { ApiKey = "test-key", TimeoutSeconds = 5 });

        var result = await provider.ClassifyAsync(CreateInput());

        Assert.AreEqual("unknown", result.Category);
        Assert.AreEqual(3, handler.Requests);
    }

    [TestMethod]
    [DataRow("{\"transient_dependency\":1}")]
    [DataRow("{\"transient_dependency\":0.5,\"authentication_configuration\":0.5,\"contract_schema\":0,\"business_rule\":0,\"missing_reference_data\":0,\"application_defect\":0,\"messaging_platform\":0,\"unknown\":0,\"new_category\":0}")]
    [DataRow("{\"transient_dependency\":0.4,\"authentication_configuration\":0,\"contract_schema\":0,\"business_rule\":0,\"missing_reference_data\":0,\"application_defect\":0,\"messaging_platform\":0,\"unknown\":0}")]
    public async Task Provider_Rejects_Missing_Unknown_And_Inconsistent_Category_Maps(string probabilities)
    {
        var handler = new QueueHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"model\":\"jev-1.13.0\",\"answers\":{{\"failure_category\":{{\"choice\":\"transient_dependency\",\"confidence\":0.91,\"probabilities\":{probabilities}}},\"retry_likely_to_succeed_unchanged\":{{\"noul\":0.82}},\"change_required_before_success\":{{\"noul\":0.12}},\"external_dependency_involved\":{{\"noul\":0.97}}}}}}", Encoding.UTF8, "application/json"),
        });
        var provider = CreateProvider(handler, new FailureClassificationOptions { ApiKey = "test-key", TimeoutSeconds = 2 });

        var error = await Assert.ThrowsExactlyAsync<IntelligenceProviderException>(() => provider.ClassifyAsync(CreateInput()));

        Assert.AreEqual("ProviderInvalidResponse", error.Code);
        Assert.IsFalse(error.Message.Contains("new_category", StringComparison.Ordinal));
        Assert.AreEqual(1, handler.Requests);
    }

    private static TypeSafeFailureIntelligenceProvider CreateProvider(HttpMessageHandler handler, FailureClassificationOptions options)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient("NimBus.TypeSafe")
            .ConfigureHttpClient(client => client.BaseAddress = new Uri("https://test.invalid/"))
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();
        return new TypeSafeFailureIntelligenceProvider(
            provider.GetRequiredService<IHttpClientFactory>(),
            options,
            provider.GetRequiredService<ILogger<TypeSafeFailureIntelligenceProvider>>());
    }

    private static FailureClassificationInput CreateInput() => new()
    {
        MessageId = "message-1", EventId = "event-1", EventTypeId = "orders.created",
        EndpointId = "orders", ResolutionStatus = "Failed",
        Exception = new FailureExceptionInfo("HttpRequestException", "temporary outage", "client"),
    };

    private sealed class QueueHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Requests { get; private set; }
        public string? LastAuthorization { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastAuthorization = request.Headers.Authorization?.ToString();
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return _responses.Dequeue();
        }
    }
}
