using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Observes classification POSTs rejected before the controller action runs.</summary>
public sealed class IntegrationIntelligenceAuditMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    public IntegrationIntelligenceAuditMiddleware(RequestDelegate next) => _next = next;

    /// <summary>Audits authentication, authorization, rate-limit and request-validation rejections.</summary>
    public async Task InvokeAsync(HttpContext context, IIntegrationIntelligenceHost host)
    {
        if (!IsClassificationPost(context))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        using var activity = NimBusIntelligenceTelemetry.ActivitySource.StartActivity("NimBus.Intelligence.FailureClassification");
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            if (!context.Items.ContainsKey(IntegrationIntelligenceRequestMarkers.ControllerEntered)
                && IsPreControllerRejection(context.Response.StatusCode))
            {
                var eventId = RouteValue(context, "eventId");
                var messageId = RouteValue(context, "messageId");
                var source = await TryGetSourceAsync(context, messageId).ConfigureAwait(false);
                NimBusIntelligenceTelemetry.RecordRequest(
                    started, activity, "rejected", "TypeSafe", null, null, source.EndpointId, source.EventTypeId);
                await AuditRejectedAsync(host, eventId, source.EndpointId, messageId, context.Response.StatusCode, cancellationToken: context.RequestAborted)
                    .ConfigureAwait(false);
            }
        }
    }

#pragma warning disable CS0618 // The deprecated analyze route stays audited until v5.0.0 (Spec 044 §6.10).
    private static bool IsClassificationPost(HttpContext context)
        => HttpMethods.IsPost(context.Request.Method)
            && context.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } descriptor
            && descriptor.ControllerTypeInfo == typeof(Controllers.IntegrationIntelligenceController).GetTypeInfo()
            && descriptor.ActionName == nameof(Controllers.IntegrationIntelligenceController.PostClassification);
#pragma warning restore CS0618

    private static bool IsPreControllerRejection(int statusCode)
        => statusCode is StatusCodes.Status400BadRequest
            or StatusCodes.Status401Unauthorized
            or StatusCodes.Status403Forbidden
            or StatusCodes.Status429TooManyRequests;

    private static string? RouteValue(HttpContext context, string name)
        => context.Request.RouteValues.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static async Task<(string? EndpointId, string? EventTypeId)> TryGetSourceAsync(HttpContext context, string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId) || !context.Request.RouteValues.TryGetValue("eventId", out var eventValue)) return (null, null);
        var messages = context.RequestServices.GetService<IMessageTrackingStore>();
        if (messages is null) return (null, null);
        try
        {
            var message = await messages.GetMessage(eventValue?.ToString() ?? string.Empty, messageId).ConfigureAwait(false);
            return message is null ? (null, null) : (message.EndpointId, message.EventTypeId);
        }
        catch { return (null, null); }
    }

    private static async Task AuditRejectedAsync(
        IIntegrationIntelligenceHost host,
        string? eventId,
        string? endpointId,
        string? messageId,
        int statusCode,
        CancellationToken cancellationToken)
    {
        var data = JsonSerializer.Serialize(new
        {
            messageId,
            outcome = "rejected",
            statusCode,
        });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await host.AuditAsync(MessageStore.MessageAuditType.FailureClassified, eventId, endpointId, data, statusCode is 401 or 403, timeout.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // Pre-controller audit is best effort and must not change the response.
        }
    }
}

internal static class IntegrationIntelligenceRequestMarkers
{
    internal static readonly object ControllerEntered = new();
}
