namespace Erp.Api.Endpoints;

public static class AlertsEndpoints
{
    public static void MapAlertsEndpoints(this IEndpointRouteBuilder app)
    {
        // Inbound webhook target for NimBus notifications fired by the ERP adapter.
        // Unauthenticated by design — this is a local demo sink (the WebhookChannel cannot
        // send a custom auth header). Do not expose this pattern as-is in production.
        app.MapPost("/api/webhooks/notifications", (NotificationWebhook payload, AlertsState state) =>
        {
            state.Add(new Alert(
                Severity: payload.Severity ?? "Information",
                Title: payload.Title ?? string.Empty,
                Message: payload.Message ?? string.Empty,
                EventId: payload.EventId ?? string.Empty,
                EventTypeId: payload.EventTypeId ?? string.Empty,
                MessageId: payload.MessageId ?? string.Empty,
                CorrelationId: payload.CorrelationId ?? string.Empty,
                ErrorDetails: payload.ErrorDetails ?? string.Empty,
                ReceivedAt: DateTimeOffset.UtcNow));

            return Results.Accepted();
        });

        // Receiver for NimBus endpoint-alert webhooks (an Alerts-tab subscription on an endpoint,
        // delivered by the Resolver). It records the raw body and the X-NimBus-* headers without
        // checking the signature, so the end-to-end test can check it with the secret it got when
        // it subscribed. A local demo sink only.
        app.MapPost("/api/webhooks/endpoint-alerts", async (HttpRequest request, EndpointAlertWebhookState state) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(request.HttpContext.RequestAborted);
            state.Add(new ReceivedEndpointAlert(
                Body: body,
                Subscription: request.Headers["X-NimBus-Subscription"].ToString(),
                Delivery: request.Headers["X-NimBus-Delivery"].ToString(),
                Timestamp: request.Headers["X-NimBus-Timestamp"].ToString(),
                Signature: request.Headers["X-NimBus-Signature"].ToString(),
                ReceivedAt: DateTimeOffset.UtcNow));
            return Results.Accepted();
        });

        var admin = app.MapGroup("/api/admin");

        admin.MapGet("/endpoint-alerts", (EndpointAlertWebhookState state) => Results.Ok(state.Snapshot()));

        admin.MapGet("/alerts", (AlertsState state) => Results.Ok(state.Snapshot()));

        admin.MapDelete("/alerts", (AlertsState state) =>
        {
            state.Clear();
            return Results.NoContent();
        });
    }
}

/// <summary>
/// Shape of the notification webhook body posted by the ERP adapter's NimBus
/// <c>WebhookChannel</c> (driven by the JSON <c>Template</c> configured there).
/// </summary>
public record NotificationWebhook(
    string? Severity,
    string? Title,
    string? Message,
    string? EventId,
    string? EventTypeId,
    string? MessageId,
    string? CorrelationId,
    string? ErrorDetails);
