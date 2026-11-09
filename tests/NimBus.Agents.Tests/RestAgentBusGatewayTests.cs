#pragma warning disable CA1707, CA2007

using System.Net;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Agents;
using NimBus.Agents.Internal;
using Newtonsoft.Json.Linq;

namespace NimBus.Agents.Tests;

[TestClass]
public class RestAgentBusGatewayTests
{
    private static readonly HandoffCoordinates Coords = new(
        EventId: "e1", SessionId: "s1", MessageId: "m1",
        EventTypeId: "evt", CorrelationId: "c1", OriginatingMessageId: "o1");

    private static (RestAgentBusGateway Gateway, FakeHandler Handler) NewGateway(params HttpResponseMessage[] responses)
    {
        var handler = new FakeHandler(responses);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://nimbus-ops.test") };
        client.DefaultRequestHeaders.Add("X-Agent-Id", "test-agent");
        return (new RestAgentBusGateway(client), handler);
    }

    private static HttpResponseMessage Resp(HttpStatusCode code, string? json = null) =>
        new(code) { Content = json is null ? null : new StringContent(json, Encoding.UTF8, "application/json") };

    [TestMethod]
    public async Task SubscribeAsync_PostsEventTypeId_WithAgentHeader()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.OK));

        await gw.SubscribeAsync("CrmContactCreated", CancellationToken.None);

        Assert.AreEqual(HttpMethod.Post, h.Requests[0].Method);
        Assert.AreEqual("/api/agent/subscribe", h.Requests[0].RequestUri!.AbsolutePath);
        Assert.AreEqual("CrmContactCreated", (string?)JObject.Parse(h.Bodies[0])["eventTypeId"]);
        Assert.IsTrue(h.Requests[0].Headers.Contains("X-Agent-Id"), "X-Agent-Id header should flow from the client.");
    }

    [TestMethod]
    public async Task DefineEventTypeAsync_PostsSchema()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.OK));

        await gw.DefineEventTypeAsync("out.v1", "{\"type\":\"object\"}", "Out", "desc", null, CancellationToken.None);

        Assert.AreEqual("/api/agent/event-types", h.Requests[0].RequestUri!.AbsolutePath);
        var body = JObject.Parse(h.Bodies[0]);
        Assert.AreEqual("out.v1", (string?)body["eventTypeId"]);
        Assert.AreEqual("Out", (string?)body["name"]);
    }

    [TestMethod]
    public async Task DefineEventTypeAsync_409Conflict_SurfacesAsConfigurationError()
    {
        var (gw, _) = NewGateway(Resp(HttpStatusCode.Conflict, "{\"detail\":\"schema conflict\"}"));

        // 409 means "already defined with a DIFFERENT schema" (identical redefinitions
        // return 200), so it must throw — swallowing it would let an upgraded agent keep
        // running against a stale output contract.
        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => gw.DefineEventTypeAsync("out.v1", "{}", null, null, null, CancellationToken.None));

        Assert.AreEqual(HttpStatusCode.Conflict, ex.StatusCode);
        StringAssert.Contains(ex.Message, "different schema", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "out.v1", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReceiveAsync_204_ReturnsNull()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.NoContent));

        var msg = await gw.ReceiveAsync("CrmContactCreated", 5, CancellationToken.None);

        Assert.IsNull(msg);
        var uri = h.Requests[0].RequestUri!;
        Assert.AreEqual("/api/agent/receive", uri.AbsolutePath);
        StringAssert.Contains(uri.Query, "waitSeconds=5", StringComparison.Ordinal);
        StringAssert.Contains(uri.Query, "eventTypeId=CrmContactCreated", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ReceiveAsync_200_DeserializesMessageAndCoordinates()
    {
        const string json =
            "{\"eventTypeId\":\"CrmContactCreated\",\"payload\":\"{\\\"a\\\":1}\"," +
            "\"coordinates\":{\"eventId\":\"e1\",\"sessionId\":\"s1\",\"messageId\":\"m1\"," +
            "\"eventTypeId\":\"CrmContactCreated\",\"correlationId\":\"c1\",\"originatingMessageId\":\"o1\"}}";
        var (gw, _) = NewGateway(Resp(HttpStatusCode.OK, json));

        var msg = await gw.ReceiveAsync(null, 5, CancellationToken.None);

        Assert.IsNotNull(msg);
        Assert.AreEqual("CrmContactCreated", msg!.EventTypeId);
        Assert.AreEqual("{\"a\":1}", msg.Payload);
        Assert.AreEqual("e1", msg.Coordinates.EventId);
        Assert.AreEqual("s1", msg.Coordinates.SessionId);
        Assert.AreEqual("o1", msg.Coordinates.OriginatingMessageId);
    }

    [TestMethod]
    public async Task PublishAsync_PostsCamelCaseBody()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.OK));

        await gw.PublishAsync("out.v1", "{\"x\":1}", "sess-9", CancellationToken.None);

        Assert.AreEqual("/api/agent/publish", h.Requests[0].RequestUri!.AbsolutePath);
        var body = JObject.Parse(h.Bodies[0]);
        Assert.AreEqual("out.v1", (string?)body["eventTypeId"]);
        Assert.AreEqual("{\"x\":1}", (string?)body["payload"]);
        Assert.AreEqual("sess-9", (string?)body["sessionId"]);
    }

    [TestMethod]
    public async Task PublishAsync_NonSuccess_ThrowsWithStatusCode()
    {
        var (gw, _) = NewGateway(Resp(HttpStatusCode.BadRequest, "{\"detail\":\"schema invalid\"}"));

        var ex = await Assert.ThrowsExactlyAsync<HttpRequestException>(
            () => gw.PublishAsync("out.v1", "{}", null, CancellationToken.None));
        Assert.AreEqual(HttpStatusCode.BadRequest, ex.StatusCode);
    }

    [TestMethod]
    public async Task SettleAsync_Complete_SendsCompleteOutcome()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.OK));

        await gw.SettleAsync(Coords, success: true, result: "ok", errorText: null, errorType: null, CancellationToken.None);

        Assert.AreEqual("/api/agent/settle", h.Requests[0].RequestUri!.AbsolutePath);
        var body = JObject.Parse(h.Bodies[0]);
        Assert.AreEqual("complete", (string?)body["outcome"]);
        Assert.AreEqual("ok", (string?)body["result"]);
        Assert.AreEqual("e1", (string?)body["coordinates"]!["eventId"]);
    }

    [TestMethod]
    public async Task SettleAsync_Fail_SendsErrorText()
    {
        var (gw, h) = NewGateway(Resp(HttpStatusCode.OK));

        await gw.SettleAsync(Coords, success: false, result: null, errorText: "boom", errorType: "Bang", CancellationToken.None);

        var body = JObject.Parse(h.Bodies[0]);
        Assert.AreEqual("fail", (string?)body["outcome"]);
        Assert.AreEqual("boom", (string?)body["errorText"]);
        Assert.AreEqual("Bang", (string?)body["errorType"]);
    }

    /// <summary>Records each request (and its body) and replays a queue of canned responses.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public FakeHandler(params HttpResponseMessage[] responses) => _responses = new Queue<HttpResponseMessage>(responses);

        public List<HttpRequestMessage> Requests { get; } = new();
        public List<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            return _responses.Count > 0 ? _responses.Dequeue() : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
