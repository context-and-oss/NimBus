#pragma warning disable CA1707, CA2007

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.ApplicationInsights;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class ApplicationInsightsServiceTests
{
    [TestMethod]
    public async Task GetLogs_escapes_untrusted_event_id_as_one_kql_string_literal()
    {
        const string maliciousEventId = "evt' \" | take 1 //\\\r\n\t\u0001";
        var handler = new CapturingHandler(HttpStatusCode.OK, EmptyTraceResponse);
        var sut = CreateService(handler);

        _ = (await sut.GetLogs(new Filter { EventId = maliciousEventId })).ToList();

        var query = HttpUtility.ParseQueryString(handler.RequestUri!.Query)["query"]!;
        const string expectedLiteral = "'evt\\' \" | take 1 //\\\\\\r\\n\\t\\u0001'";
        StringAssert.Contains(
            query,
            $"tostring(customDimensions['NimBus.EventId']) == {expectedLiteral}", StringComparison.Ordinal);
        Assert.IsFalse(query.Any(char.IsControl), "Control characters must be escaped before sending KQL.");
        Assert.AreEqual(1, CountOccurrences(query, " | top 1000"));
    }

    [TestMethod]
    public async Task GetLogs_throws_http_request_exception_for_non_success_response()
    {
        var handler = new CapturingHandler(HttpStatusCode.BadRequest, "not-json");
        var sut = CreateService(handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => sut.GetLogs(new Filter { EventId = "event-1" }));
    }

    [TestMethod]
    public async Task GetLatencyMetrics_throws_http_request_exception_for_non_success_response()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "not-json");
        var sut = CreateService(handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => sut.GetLatencyMetrics(TimeSpan.FromHours(1)));
    }

    private const string EmptyTraceResponse =
        "{\"tables\":[{\"columns\":[" +
        "{\"name\":\"timestamp\",\"type\":\"datetime\"}," +
        "{\"name\":\"message\",\"type\":\"string\"}," +
        "{\"name\":\"severityLevel\",\"type\":\"int\"}," +
        "{\"name\":\"customDimensions\",\"type\":\"dynamic\"}]," +
        "\"rows\":[]}]}";

    private static ApplicationInsightsService CreateService(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        return new ApplicationInsightsService(client);
    }

    private static int CountOccurrences(string value, string expected)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(expected, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += expected.Length;
        }

        return count;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public CapturingHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content),
            });
        }
    }
}
