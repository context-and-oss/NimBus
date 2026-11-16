#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NimBus.Extensions.IntegrationIntelligence.Tests;

[TestClass]
public sealed class TelemetryTests
{
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
                Stopwatch.GetTimestamp(), activity, outcome, FailureClassificationProviders.Agent, "agent-model", "unknown", "orders", "orders.created");
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
}
