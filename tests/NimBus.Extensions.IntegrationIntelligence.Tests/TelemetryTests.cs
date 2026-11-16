#pragma warning disable CA1707, CA2007, CS0618 // CS0618: these tests cover the deprecated provider (Spec 044 §6.10).
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Extensions.IntegrationIntelligence.Controllers;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class AuditMiddlewareAndTelemetryTests
{
    [TestMethod]
    [DataRow(400)]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(429)]
    public async Task PreController_Rejection_Is_Audited_Exactly_Once_And_Preserves_Response(int statusCode)
    {
        var host = new TestIntelligenceHost();
        var context = CreateContext();
        var middleware = new IntegrationIntelligenceAuditMiddleware(next =>
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, host);

        Assert.AreEqual(statusCode, context.Response.StatusCode);
        Assert.HasCount(1, host.Audits);
        StringAssert.Contains(host.Audits[0], "\"outcome\":\"rejected\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Controller_Entry_Marker_Prevents_Double_Audit_And_Audit_Failure_Is_Harmless()
    {
        var host = new TestIntelligenceHost { AuditThrows = true };
        var context = CreateContext();
        context.Items[IntegrationIntelligenceRequestMarkers.ControllerEntered] = true;
        var middleware = new IntegrationIntelligenceAuditMiddleware(next =>
        {
            context.Response.StatusCode = 400;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, host);

        Assert.AreEqual(400, context.Response.StatusCode);
        Assert.HasCount(0, host.Audits);
    }

    [TestMethod]
    public void Telemetry_Records_All_Outcomes_With_LowCardinality_Allowed_Tags()
    {
        var outcomes = new[] { "rejected", "busy", "unknown", "provider_error", "store_error", "ok", "cached" };
        var measurements = new List<(string Name, string Outcome, IReadOnlyList<string> Tags)>();
        var activities = new List<Activity>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == NimBusIntelligenceTelemetry.Meter) listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var tagArray = tags.ToArray();
            var outcome = tagArray.FirstOrDefault(tag => tag.Key == "nimbus.intelligence.outcome").Value?.ToString() ?? string.Empty;
            measurements.Add((instrument.Name, outcome, tagArray.Select(tag => tag.Key).ToArray()));
        });
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            var tagArray = tags.ToArray();
            var outcome = tagArray.FirstOrDefault(tag => tag.Key == "nimbus.intelligence.outcome").Value?.ToString() ?? string.Empty;
            measurements.Add((instrument.Name, outcome, tagArray.Select(tag => tag.Key).ToArray()));
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == NimBusIntelligenceTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Add(activity),
        };
        ActivitySource.AddActivityListener(activityListener);

        foreach (var outcome in outcomes)
        {
            using var activity = NimBusIntelligenceTelemetry.ActivitySource.StartActivity("NimBus.Intelligence.FailureClassification");
            Assert.IsNotNull(activity);
            NimBusIntelligenceTelemetry.RecordRequest(
                Stopwatch.GetTimestamp(), activity, outcome, "TypeSafe", "jev-1.13.0", "unknown", "orders", "orders.created");
        }

        Assert.IsTrue(outcomes.All(outcome => measurements.Any(measurement => measurement.Outcome == outcome)));
        var allowedTags = new HashSet<string>(StringComparer.Ordinal)
        {
            "nimbus.intelligence.provider", "nimbus.intelligence.model", "nimbus.intelligence.outcome",
            "nimbus.intelligence.category", "nimbus.endpoint", "nimbus.event_type",
        };
        Assert.IsTrue(measurements.All(measurement => measurement.Tags.All(allowedTags.Contains)));
        Assert.HasCount(outcomes.Length, activities);
        Assert.IsTrue(outcomes.All(outcome => activities.Any(activity =>
            activity.Tags.Any(tag => tag.Key == "nimbus.intelligence.outcome" && tag.Value == outcome))));
        Assert.IsTrue(activities.All(activity => activity.Tags.All(tag => allowedTags.Contains(tag.Key))));
        var forbiddenTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "event.id", "message.id", "session.id", "evidence", "credential", "api.key",
        };
        Assert.IsFalse(measurements.Any(measurement => measurement.Tags.Any(forbiddenTags.Contains)));
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = HttpMethods.Post;
        context.Request.RouteValues["eventId"] = "event";
        context.Request.RouteValues["messageId"] = "failure";
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new ControllerActionDescriptor
            {
                ControllerTypeInfo = typeof(IntegrationIntelligenceController).GetTypeInfo(),
                ActionName = nameof(IntegrationIntelligenceController.PostClassification),
            }),
            "classification"));
        return context;
    }
}
