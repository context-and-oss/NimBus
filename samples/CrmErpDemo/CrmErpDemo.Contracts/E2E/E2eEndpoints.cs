using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrmErpDemo.Contracts.E2E;

/// <summary>Authenticated, Development-only control routes for live integration tests.</summary>
public static class E2eEndpoints
{
    /// <summary>Maps nothing unless the explicit test profile is active.</summary>
    public static RouteGroupBuilder? MapE2eControls(this WebApplication app)
    {
        if (!E2eSettings.IsEnabled(app.Configuration, app.Environment)) return null;
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(app.Configuration["E2E:Key"]!));
        var group = app.MapGroup("/api/e2e");
        group.AddEndpointFilter(async (context, next) =>
        {
            var supplied = context.HttpContext.Request.Headers[E2eSettings.Header].ToString();
            if (supplied.Length > 256 || !CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                return Results.Unauthorized();
            return await next(context);
        });
        group.MapGet("/ready", () => Results.Ok(new { enabled = true }));
        group.MapPut("/sessions/{id:guid}", (Guid id, E2eScript script, E2eRegistry registry) =>
        {
            try { registry.Configure(id, script); return Results.NoContent(); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
        });
        group.MapGet("/sessions/{id:guid}", (Guid id, E2eRegistry registry) => Results.Ok(registry.Snapshot(id)));
        group.MapDelete("/sessions/{id:guid}", (Guid id, E2eRegistry registry) =>
        {
            registry.Remove(id);
            return Results.NoContent();
        });
        group.MapPost("/sessions/{id:guid}/attempt", (Guid id, AttemptRequest request, E2eRegistry registry) =>
            Results.Ok(registry.Attempt(id, request.EventType, request.Stage, request.MessageId, request.EventId, request.OriginatingMessageId)));
        return group;
    }

    /// <summary>Coordinates supplied by a real subscriber invocation.</summary>
    public sealed record AttemptRequest(string EventType, string Stage, string MessageId, string EventId, string? OriginatingMessageId);
}
