using System.Net;
using System.Text;
using Newtonsoft.Json;

namespace NimBus.Agents.Internal;

/// <summary>
/// <see cref="IAgentBusGateway"/> backed by the NimBus agent REST API. Registered as a typed
/// <see cref="HttpClient"/> whose base address resolves the nimbus-ops resource via service
/// discovery and whose default headers carry the agent identity (<c>X-Agent-Id</c>). All bodies
/// are camelCase JSON (Newtonsoft) to match the API contract.
/// <para>
/// It calls the unversioned <c>/api/agent/*</c> routes on purpose: they are deprecated in
/// favour of <c>/api/v1/agent/*</c>, but a WebApp older than API 1.0.0 serves only them, and
/// this client must keep working against those. Move to <c>/api/v1</c> in the next major, when
/// the unversioned routes are removed.
/// </para>
/// </summary>
internal sealed class RestAgentBusGateway : IAgentBusGateway
{
    private readonly HttpClient _http;

    public RestAgentBusGateway(HttpClient http) => _http = http;

    public async Task SubscribeAsync(string eventTypeId, CancellationToken ct)
    {
        using var response = await _http.PostAsync("/api/agent/subscribe", JsonBody(new { eventTypeId }), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"Subscribe to '{eventTypeId}'", ct).ConfigureAwait(false);
    }

    public async Task DefineEventTypeAsync(string eventTypeId, string jsonSchema, string? name, string? description, string? sessionKeyPath, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            "/api/agent/event-types",
            JsonBody(new { eventTypeId, jsonSchema, name, description, sessionKeyPath }),
            ct).ConfigureAwait(false);

        // 409 = already defined with a DIFFERENT schema. An identical redefinition returns
        // 200 with the stored schema, so a Conflict always means a real contract mismatch
        // (e.g. an upgraded agent shipping a changed schema against the immutable stored
        // one). Swallowing it would let the agent keep running against a stale contract —
        // surface it as a fatal configuration error instead.
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new HttpRequestException(
                $"Define event type '{eventTypeId}' failed: it is already defined with a different schema (409 Conflict). " +
                "Schemas are immutable — align the agent's schema with the stored definition, or publish under a new event type id.",
                inner: null,
                statusCode: HttpStatusCode.Conflict);
        }

        await EnsureSuccessAsync(response, $"Define event type '{eventTypeId}'", ct).ConfigureAwait(false);
    }

    public async Task<AgentReceivedMessage?> ReceiveAsync(string? eventTypeId, int waitSeconds, CancellationToken ct)
    {
        var query = $"/api/agent/receive?waitSeconds={waitSeconds}";
        if (!string.IsNullOrWhiteSpace(eventTypeId))
            query += $"&eventTypeId={Uri.EscapeDataString(eventTypeId)}";

        using var response = await _http.GetAsync(query, ct).ConfigureAwait(false);

        // 204 = nothing parked within the long-poll window.
        if (response.StatusCode == HttpStatusCode.NoContent)
            return null;

        await EnsureSuccessAsync(response, "Receive", ct).ConfigureAwait(false);

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return JsonConvert.DeserializeObject<AgentReceivedMessage>(json)
            ?? throw new HttpRequestException("Receive returned 200 but an empty/unparseable body.");
    }

    public async Task PublishAsync(string eventTypeId, string payloadJson, string? sessionId, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            "/api/agent/publish",
            JsonBody(new { eventTypeId, payload = payloadJson, sessionId }),
            ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"Publish '{eventTypeId}'", ct).ConfigureAwait(false);
    }

    public async Task SettleAsync(HandoffCoordinates coordinates, bool success, string? result, string? errorText, string? errorType, CancellationToken ct)
    {
        var body = new
        {
            coordinates = new
            {
                eventId = coordinates.EventId,
                sessionId = coordinates.SessionId,
                messageId = coordinates.MessageId,
                eventTypeId = coordinates.EventTypeId,
                correlationId = coordinates.CorrelationId,
                originatingMessageId = coordinates.OriginatingMessageId,
            },
            outcome = success ? "complete" : "fail",
            result,
            errorText,
            errorType,
        };

        using var response = await _http.PostAsync("/api/agent/settle", JsonBody(body), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, $"Settle '{coordinates.EventId}' ({(success ? "complete" : "fail")})", ct).ConfigureAwait(false);
    }

    private static StringContent JsonBody(object body) =>
        new(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var responseBody = string.Empty;
        try { responseBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch (HttpRequestException) { /* best-effort diagnostics — never crash over a body-read failure */ }

        var detail = string.IsNullOrWhiteSpace(responseBody) ? string.Empty : $" Body: {responseBody}";
        throw new HttpRequestException(
            $"{operation} failed: agent API -> {(int)response.StatusCode} {response.ReasonPhrase}.{detail}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
