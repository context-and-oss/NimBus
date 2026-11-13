#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Insights;

namespace NimBus.WebApp.Tests;

/// <summary>Spec 043: failed-message insights enriched with saved classifications.</summary>
[TestClass]
public sealed partial class FailedInsightsServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime From = Now.AddHours(-6);
    private static readonly DateTime To = Now;

    [TestMethod]
    public async Task Three_failed_attempts_of_one_event_count_three_and_each_joins_its_own_result()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "evt-1", "msg-1", "Timeout calling CRM", minutesAgo: 30);
        await Fail(store, "orders", "evt-1", "msg-2", "Timeout calling CRM", minutesAgo: 20);
        await Fail(store, "orders", "evt-1", "msg-3", "Customer 4711 not found", minutesAgo: 10);
        var enrichment = new FakeEnrichment();
        enrichment.Save(Result("msg-1", "evt-1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        enrichment.Save(Result("msg-3", "evt-1", "orders", "missing_reference_data", FailureGuidance.ChangeLikelyRequired));

        var overview = await Build(store, enrichment);

        Assert.AreEqual(3, overview.TotalOccurrences);
        Assert.AreEqual(3, overview.TotalFailed);
        Assert.AreEqual(InsightsEnrichmentStatus.Available, overview.Enrichment);
        Assert.AreEqual(1, Group(overview.CauseGroups, "transient_dependency").Count);
        Assert.AreEqual(1, Group(overview.CauseGroups, "missing_reference_data").Count);
        Assert.AreEqual(1, Group(overview.CauseGroups, FailedInsightsService.NotAnalyzed).Count, "msg-2 has no result of its own.");
    }

    [TestMethod]
    public async Task Agent_and_provider_results_count_alike_and_occurrences_show_who_made_them()
    {
        // Spec 044 §6.8: coverage, causes and guidance group agent recordings like provider results;
        // group detail shows the provider and fault origin per occurrence.
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "by-agent", "Customer 4711 not found", minutesAgo: 30);
        await Fail(store, "orders", "e2", "by-provider", "Customer 4712 not found", minutesAgo: 20);
        var enrichment = new FakeEnrichment();
        enrichment.Save(Result("by-agent", "e1", "orders", "missing_reference_data", FailureGuidance.ChangeLikelyRequired) with
        {
            Provider = FailureClassificationProviders.Agent, QuestionSetVersion = 2, FaultOrigin = "source_data", FaultOriginConfidence = 0.8,
        });
        enrichment.Save(Result("by-provider", "e2", "orders", "missing_reference_data", FailureGuidance.ChangeLikelyRequired));

        var overview = await Build(store, enrichment);
        var page = await Service(store, enrichment).GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause,
            "missing_reference_data", 10, null));

        Assert.AreEqual(2, overview.Coverage!.Classified);
        Assert.AreEqual(2, Group(overview.CauseGroups, "missing_reference_data").Count);
        Assert.AreEqual(2, Group(overview.GuidanceGroups, nameof(FailureGuidance.ChangeLikelyRequired)).Count);
        Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
        var occurrences = page.Page!.Occurrences!.ToDictionary(o => o.MessageId!);
        Assert.AreEqual("Agent", occurrences["by-agent"].Provider);
        Assert.AreEqual("source_data", occurrences["by-agent"].FaultOrigin);
        Assert.AreEqual("TypeSafe", occurrences["by-provider"].Provider);
        Assert.IsNull(occurrences["by-provider"].FaultOrigin);
    }

    [TestMethod]
    public async Task Failures_group_by_fault_origin_with_their_sources_and_version_1_results_are_not_assessed()
    {
        // Spec 044 Phase 3 §8.5: the data-quality view for a source's owner.
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Country 'Denmark' is not ISO alpha-2", minutesAgo: 30, publisher: "crm");
        await Fail(store, "orders", "e2", "m2", "Country 'Sweden' is not ISO alpha-2", minutesAgo: 25, publisher: "crm");
        await Fail(store, "orders", "e3", "m3", "Customer 4712 not found", minutesAgo: 20, publisher: "partner");
        var enrichment = new FakeEnrichment();
        foreach (var (message, eventId) in new[] { ("m1", "e1"), ("m2", "e2") })
        {
            enrichment.Save(Result(message, eventId, "orders", "contract_schema", FailureGuidance.ChangeLikelyRequired) with
            {
                Provider = FailureClassificationProviders.Agent, QuestionSetVersion = 2, FaultOrigin = "source_data", FaultOriginConfidence = 0.9,
            });
        }
        enrichment.Save(Result("m3", "e3", "orders", "missing_reference_data", FailureGuidance.Investigate));

        var overview = await Build(store, enrichment);
        var page = await Service(store, enrichment).GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.FaultOrigin,
            "source_data", 10, null));

        var sourceData = Group(overview.FaultOriginGroups, "source_data");
        Assert.AreEqual(2, sourceData.Count);
        Assert.AreEqual("Source data", sourceData.Label);
        Assert.AreEqual("crm", sourceData.Sources!.Single().EndpointId);
        Assert.AreEqual(2, sourceData.Sources!.Single().Count);
        Assert.AreEqual(1, Group(overview.FaultOriginGroups, "notAssessed").Count, "A version 1 result has no origin.");
        Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
        Assert.AreEqual(2, page.Page!.Occurrences!.Count);
    }

    [TestMethod]
    public async Task An_unknown_fault_origin_group_key_is_refused()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout", minutesAgo: 30);
        var enrichment = new FakeEnrichment();

        var page = await Service(store, enrichment).GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.FaultOrigin,
            "cosmic_rays", 10, null));

        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, page.Status);
    }

    [TestMethod]
    public async Task Coverage_partitions_and_every_perspective_reconcile_for_a_mixed_scope()
    {
        var store = new CountingStore();
        var enrichment = new FakeEnrichment();
        enrichment.Disallowed.Add("billing");
        await Fail(store, "orders", "e1", "classified", "Timeout calling CRM");
        enrichment.Save(Result("classified", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        await Fail(store, "orders", "e2", "unknown-cause", "Something odd");
        enrichment.Save(Result("unknown-cause", "e2", "orders", "unknown", FailureGuidance.Investigate));
        await Fail(store, "orders", "e3", "future-guidance", "Something odd");
        enrichment.Save(Result("future-guidance", "e3", "orders", "business_rule", (FailureGuidance)42));
        await Fail(store, "orders", "e4", "future-category", "Something odd");
        enrichment.Save(Result("future-category", "e4", "orders", "quantum_flux", FailureGuidance.RetryMayHelp));
        await Fail(store, "orders", "e5", "mismatch", "Something odd");
        enrichment.Save(Result("mismatch", "e5", "invoices", "transient_dependency", FailureGuidance.RetryMayHelp));
        await Fail(store, "orders", "e6", "low-confidence", "Something odd");
        enrichment.Save(Result("low-confidence", "e6", "orders", "business_rule", FailureGuidance.Uncertain));
        await Fail(store, "orders", "e7", "none", "Something odd");
        await Fail(store, "billing", "e8", "not-allowed", "Something odd");

        var overview = await Build(store, enrichment);

        var coverage = overview.Coverage!;
        Assert.AreEqual(8, coverage.Total);
        Assert.AreEqual(1, coverage.Classified);
        Assert.AreEqual(5, coverage.Uncertain, "unknown cause, undefined guidance, unknown category, mismatch, Uncertain guidance");
        Assert.AreEqual(1, coverage.NotAnalyzed);
        Assert.AreEqual(1, coverage.NotEligible);
        Assert.AreEqual(6, coverage.Analyzed);
        Assert.AreEqual(8, overview.CauseGroups!.Sum(g => g.Count));
        Assert.AreEqual(8, overview.GuidanceGroups!.Sum(g => g.Count));
        Assert.AreEqual(8, overview.Groups.Sum(g => g.Count));
        Assert.AreEqual(5, Group(overview.CauseGroups, FailedInsightsService.Uncertain).Count);
        Assert.AreEqual(1, Group(overview.CauseGroups, FailedInsightsService.NotEligible).Count);
        Assert.AreEqual("Not eligible", Group(overview.CauseGroups, FailedInsightsService.NotEligible).Label);
        // Guidance is grouped independently of cause: unknown cause with Investigate stays Investigate.
        Assert.AreEqual(1, Group(overview.GuidanceGroups, nameof(FailureGuidance.Investigate)).Count);
        // Undefined guidance, mismatched identity and Uncertain guidance are all Uncertain guidance.
        Assert.AreEqual(3, Group(overview.GuidanceGroups, nameof(FailureGuidance.Uncertain)).Count);
        Assert.AreEqual(2, Group(overview.GuidanceGroups, nameof(FailureGuidance.RetryMayHelp)).Count, "classified + unknown category with known guidance");
        foreach (var group in overview.CauseGroups!.Concat(overview.GuidanceGroups!))
            Assert.AreEqual(group.Count, group.Coverage!.Total, group.GroupKey);
    }

    [TestMethod]
    public async Task Window_bounds_and_the_endpoint_filter_select_exactly_the_matching_occurrences()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "at-from", "x", at: From);
        await Fail(store, "Orders", "e2", "mixed-case", "x", at: From.AddMinutes(1));
        await Fail(store, "orders", "e3", "at-to", "x", at: To);
        await Fail(store, "billing", "e4", "other", "x", at: From.AddMinutes(2));

        var all = await Build(store, new FakeEnrichment());
        var orders = await Build(store, new FakeEnrichment(), endpointId: "ORDERS");

        Assert.AreEqual(3, all.TotalOccurrences, "[from, to): the row at to is excluded");
        Assert.AreEqual(2, orders.TotalOccurrences, "endpoint ids compare case-insensitively");
        Assert.AreEqual("ORDERS", orders.EndpointId);
        Assert.AreEqual(From, orders.From);
        Assert.AreEqual(To, orders.To);
    }

    [TestMethod]
    public async Task Duplicate_identities_count_once_and_a_missing_identity_makes_the_scope_unsupported()
    {
        var duplicated = new FixedRowsStore(
            Row("orders", "e1", "m1", Now.AddMinutes(-5)), Row("ORDERS", "e1", "m1", Now.AddMinutes(-4)), Row("orders", "e2", "m2", Now.AddMinutes(-3)));
        var deduplicated = await Build(duplicated, new FakeEnrichment());
        Assert.AreEqual(2, deduplicated.TotalOccurrences);

        var missing = new FixedRowsStore(Row("orders", "e1", "m1", Now.AddMinutes(-5)), Row("orders", "e2", null, Now.AddMinutes(-4)));
        var enrichment = new FakeEnrichment();
        var overview = await Build(missing, enrichment);
        Assert.AreEqual(InsightsEnrichmentStatus.Unsupported, overview.Enrichment);
        Assert.IsNull(overview.Coverage);
        Assert.IsNull(overview.CauseGroups);
        Assert.AreEqual(2, overview.Groups.Sum(g => g.Count), "Error patterns still render.");
        Assert.AreEqual(0, enrichment.Reads);
    }

    [TestMethod]
    public async Task Over_the_ceiling_is_limit_exceeded_with_the_limit_and_no_classification_read()
    {
        var store = new FixedRowsStore(Enumerable.Range(0, 101).Select(i => Row("orders", $"e{i}", $"m{i}", Now.AddMinutes(-i - 1))).ToArray());
        var enrichment = new FakeEnrichment();

        var overview = await Build(store, enrichment, options: new FailedInsightsOptions { EnrichmentCeiling = 100 });

        Assert.AreEqual(InsightsEnrichmentStatus.LimitExceeded, overview.Enrichment);
        Assert.AreEqual(100, overview.EnrichmentLimit);
        Assert.IsNull(overview.Coverage);
        Assert.AreEqual(101, overview.TotalOccurrences);
        Assert.AreEqual(0, enrichment.Reads);
    }

    [TestMethod]
    public async Task Unavailable_storage_reports_no_coverage_and_is_retried_on_the_next_request()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment { Status = ClassificationBulkReadStatus.Unavailable };
        var cache = NewCache();

        var first = await Build(store, enrichment, cache: cache);
        enrichment.Status = ClassificationBulkReadStatus.Available;
        var second = await Build(store, enrichment, cache: cache);

        Assert.AreEqual(InsightsEnrichmentStatus.Unavailable, first.Enrichment);
        Assert.IsNull(first.Coverage, "A failed lookup never reads as zero or not analyzed.");
        Assert.IsNull(first.ClassificationsReadAt);
        Assert.AreEqual(InsightsEnrichmentStatus.Available, second.Enrichment);
        Assert.AreEqual(2, enrichment.Reads, "An unavailable read is not cached.");
    }

    [TestMethod]
    public async Task Without_a_reader_enrichment_is_disabled_and_error_patterns_still_render()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout calling CRM");

        var overview = await Build(store, new FakeEnrichment { IsEnabled = false });

        Assert.AreEqual(InsightsEnrichmentStatus.Disabled, overview.Enrichment);
        Assert.IsNull(overview.Coverage);
        Assert.AreEqual(1, overview.Groups.Single().Count);
        Assert.IsNull(overview.Groups.Single().Coverage);
    }

    [TestMethod]
    public async Task One_bulk_read_per_scope_and_the_mcp_path_reads_no_classifications()
    {
        var store = new CountingStore();
        for (var i = 0; i < 25; i++) await Fail(store, "orders", $"e{i}", $"m{i}", "x");
        var enrichment = new FakeEnrichment();
        var cache = NewCache();

        await Build(store, enrichment, cache: cache, enrich: false);
        Assert.AreEqual(0, enrichment.Reads);

        await Build(store, enrichment, cache: cache);
        await Build(store, enrichment, cache: cache);
        Assert.AreEqual(1, enrichment.Reads, "Repeated loads inside the enrichment TTL share one read.");
        Assert.AreEqual(25, enrichment.RequestedCounts.Single());
        Assert.AreEqual(1, store.Calls, "All three requests share one occurrence read.");
    }

    [TestMethod]
    public async Task A_preset_and_a_request_with_its_returned_window_read_the_projection_once()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var cache = NewCache();
        var service = Service(store, new FakeEnrichment(), cache: cache);

        var preset = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, null, null, null, true, false))).Overview!;
        var again = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, preset.From, preset.To, null, true, false))).Overview!;

        Assert.AreEqual(1, store.Calls);
        Assert.AreEqual(preset.TotalOccurrences, again.TotalOccurrences);
        Assert.AreEqual(Now, preset.To);
    }

    [TestMethod]
    public async Task A_preset_window_sent_back_as_the_api_serialized_it_reuses_the_cached_reads()
    {
        // The API writes DateTimes to the millisecond (UtcDateTimeJsonConverter); the clock is finer.
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout calling CRM");
        var enrichment = new FakeEnrichment();
        var cache = NewCache();
        var service = Service(store, enrichment, cache: cache, clock: new FixedClock(Now.AddTicks(1_234)));
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, null, null, null, true, false))).Overview!;

        var json = new JsonSerializerOptions { Converters = { new UtcDateTimeJsonConverter() } };
        var from = JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(overview.From, json), json);
        var to = JsonSerializer.Deserialize<DateTime>(JsonSerializer.Serialize(overview.To, json), json);
        var page = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(from, to, null, InsightsPerspective.ErrorPattern,
            overview.Groups.Single().GroupKey, 10, null));

        Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
        Assert.AreEqual(overview.From, from, "The returned window survives the API's serialization unchanged.");
        Assert.AreEqual(1, store.Calls, "The drill-down reads the overview's cached occurrences.");
        Assert.AreEqual(1, enrichment.Reads, "The drill-down reuses the overview's classification read.");
    }

    [TestMethod]
    public async Task A_cancelled_request_reads_nothing()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        var service = Service(store, enrichment);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false), cancelled.Token));

        Assert.AreEqual(0, store.Calls);
        Assert.AreEqual(0, enrichment.Reads);
    }

    [TestMethod]
    public async Task A_caller_that_cancels_stops_waiting_without_failing_the_shared_read_for_others()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        enrichment.Save(Result("m1", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        enrichment.Gate = () => release.Task;
        var service = Service(store, enrichment);
        using var leaving = new CancellationTokenSource();

        var first = service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false), leaving.Token);
        var second = service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false));
        await Task.Delay(50);
        await leaving.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        release.SetResult();
        var overview = (await second).Overview!;

        Assert.AreEqual(InsightsEnrichmentStatus.Available, overview.Enrichment);
        Assert.AreEqual(1, overview.Coverage!.Classified);
        Assert.AreEqual(1, enrichment.Reads, "Both callers shared one read.");
    }

    [TestMethod]
    public async Task The_metrics_routes_pass_the_request_aborted_token_to_the_insights_query()
    {
        var query = new CapturingQuery();
        using var aborted = new CancellationTokenSource();
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestAborted = aborted.Token },
        };
        var store = new CountingStore();
        var metrics = new MetricsImplementation(store, NewCache(), new Authorization(true), query, accessor);

        await metrics.GetMetricsFailedInsightsAsync(Period._1d);
        await metrics.GetMetricsFailedInsightsOccurrencesAsync(From, To, null, InsightsPerspective.Cause, "notAnalyzed", null, null);
        await metrics.GetMetricsFailedInsightsPatternsAsync(From, To, null, InsightsPerspective.Cause, "notAnalyzed", null, null);

        Assert.HasCount(3, query.Tokens);
        foreach (var token in query.Tokens) Assert.AreEqual(aborted.Token, token);
    }

    [TestMethod]
    [DataRow(true, true, true)]
    [DataRow(true, false, false)]
    [DataRow(false, true, false)]
    public async Task The_overview_says_whether_new_analysis_can_be_requested(bool enabled, bool canAnalyze, bool expected)
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");

        var overview = await Build(store, new FakeEnrichment { IsEnabled = enabled, CanAnalyze = canAnalyze });

        Assert.AreEqual(expected, overview.AnalysisAvailable);
    }

    [TestMethod]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public void The_enrichment_source_can_analyze_only_in_ready_mode(bool ready, bool expected)
    {
        var source = new InsightsEnrichmentSource([], [], [new IntegrationIntelligenceActivation(true, ready, [])]);

        Assert.AreEqual(expected, source.CanAnalyze);
        Assert.IsFalse(new InsightsEnrichmentSource([], [], []).CanAnalyze, "Intelligence off: nothing can be analyzed.");
    }

    [TestMethod]
    public async Task Enrichment_telemetry_counts_overview_loads_not_drill_down_pages()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var service = Service(store, new FakeEnrichment());
        var recorded = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == NimBusIntelligenceTelemetry.Meter && instrument.Name == "nimbus.intelligence.insights.enrichment")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref recorded, (int)value));
        listener.Start();

        await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false));
        await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null));
        await service.GetPatternsAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null));

        Assert.AreEqual(1, recorded);
    }

    [TestMethod]
    public async Task Concurrent_loads_share_one_bulk_read()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enrichment = new FakeEnrichment { Gate = () => release.Task };
        var service = Service(store, enrichment);

        var loads = Enumerable.Range(0, 8).Select(_ => service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).ToArray();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(loads);

        Assert.AreEqual(1, enrichment.Reads);
    }

    [TestMethod]
    public async Task Concurrent_refreshing_loads_share_reads_instead_of_one_each()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enrichment = new FakeEnrichment { Gate = () => release.Task };
        var service = Service(store, enrichment);

        // The Insights page sends refresh=true on every first load, so this is several operators
        // opening the page at once.
        var loads = Enumerable.Range(0, 8).Select(_ => service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, true))).ToArray();
        await Task.Delay(50);
        release.SetResult();
        await Task.WhenAll(loads);

        Assert.IsLessThanOrEqualTo(2, enrichment.Reads, "The running read plus one follow-up shared by every later refresh.");
    }

    [TestMethod]
    public async Task Zero_cache_seconds_reads_classifications_on_every_request()
    {
        var options = new FailedInsightsOptions { EnrichmentCacheSeconds = 0 };
        Assert.AreEqual(0, options.Validate().Count, "0 is inside the documented 0-30 range.");
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        var cache = NewCache();

        var first = await Build(store, enrichment, cache: cache, options: options);
        await Build(store, enrichment, cache: cache, options: options);

        Assert.AreEqual(InsightsEnrichmentStatus.Available, first.Enrichment);
        Assert.AreEqual(2, enrichment.Reads);
    }

    [TestMethod]
    public async Task Refresh_bypasses_the_classification_cache_but_not_the_occurrence_cache()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        var cache = NewCache();

        var before = await Build(store, enrichment, cache: cache);
        enrichment.Save(Result("m1", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        var cached = await Build(store, enrichment, cache: cache);
        var refreshed = await Build(store, enrichment, cache: cache, refresh: true);

        Assert.AreEqual(1, before.Coverage!.NotAnalyzed);
        Assert.AreEqual(1, cached.Coverage!.NotAnalyzed, "Without refresh the 15 s classification read is reused.");
        Assert.AreEqual(1, refreshed.Coverage!.Classified, "An explicit refresh sees the new revision.");
        Assert.AreEqual(2, enrichment.Reads);
        Assert.AreEqual(1, store.Calls, "Refresh does not re-read failure occurrences.");
    }

    [TestMethod]
    public async Task A_cached_classification_read_that_misses_newer_occurrences_is_read_again()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        enrichment.Save(Result("m2", "e2", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        var cache = NewCache();

        await Build(store, enrichment, cache: cache);
        // The occurrence cache expires first (30 s) while the classification read (15 s) is still
        // cached under a later key: simulate by dropping only the occurrence entry.
        await Fail(store, "orders", "e2", "m2", "x");
        cache.Remove($"metrics:failed-insights:rows:{From.Ticks}-{To.Ticks}");
        var overview = await Build(store, enrichment, cache: cache);

        Assert.AreEqual(2, overview.TotalOccurrences);
        Assert.AreEqual(1, overview.Coverage!.Classified, "m2 must not read as not analyzed from a read that never covered it.");
        Assert.AreEqual(2, enrichment.Reads);
    }

    [TestMethod]
    public async Task A_tombstoned_classification_is_never_exposed()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        enrichment.Snapshots["m1"] = new ClassificationSnapshot("m1", Result("m1", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp),
            "Completed", null, Deleted: true);

        var overview = await Build(store, enrichment);

        Assert.AreEqual(0, overview.Coverage!.Classified);
        Assert.IsFalse(overview.CauseGroups!.Any(g => g.GroupKey == "transient_dependency"));
    }

    [TestMethod]
    public async Task Groups_embed_the_top_patterns_and_count_the_rest()
    {
        var store = new CountingStore();
        var enrichment = new FakeEnrichment();
        var texts = new[] { "Timeout A", "Timeout A", "Timeout A", "Refused B", "Refused B", "Reset C" };
        for (var i = 0; i < texts.Length; i++)
        {
            await Fail(store, "orders", $"e{i}", $"m{i}", texts[i]);
            enrichment.Save(Result($"m{i}", $"e{i}", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        }

        var overview = await Build(store, enrichment, options: new FailedInsightsOptions { TopPatternsPerGroup = 2 });

        var group = Group(overview.CauseGroups, "transient_dependency");
        Assert.AreEqual("Transient dependency", group.Label);
        Assert.HasCount(2, group.TopPatterns);
        Assert.AreEqual(3, group.TopPatterns.First().Count);
        Assert.AreEqual(1, group.MorePatterns);
        StringAssert.StartsWith(group.TopPatterns.First().GroupKey, "ep:", StringComparison.Ordinal);
        Assert.AreEqual(group.TopPatterns.First().GroupKey,
            overview.Groups.SelectMany(g => g.SubGroups).Single(s => s.NormalizedPattern == group.TopPatterns.First().NormalizedPattern).GroupKey,
            "A pattern has the same key in every perspective.");
    }

    [TestMethod]
    public async Task A_caller_without_site_reader_is_forbidden_and_an_invalid_window_is_rejected_before_any_read()
    {
        var store = new CountingStore();
        var denied = Service(store, new FakeEnrichment(), authorization: new Authorization(false));
        Assert.AreEqual(FailedInsightsQueryStatus.Forbidden,
            (await denied.GetOverviewAsync(new FailedInsightsRequest(Period._1d, null, null, null, true, false))).Status);

        var allowed = Service(store, new FakeEnrichment());
        Assert.AreEqual(FailedInsightsQueryStatus.InvalidWindow,
            (await allowed.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, null, null, true, false))).Status);
        Assert.AreEqual(0, store.Calls);
    }

    [TestMethod]
    [DataRow(99, 15, 500, 4, 10)]
    [DataRow(10_000, 31, 500, 4, 10)]
    [DataRow(10_000, 15, 49, 4, 10)]
    [DataRow(10_000, 15, 500, 9, 10)]
    [DataRow(10_000, 15, 500, 4, 0)]
    public void Options_outside_their_bounds_are_rejected(int ceiling, int cacheSeconds, int batch, int concurrency, int patterns)
        => Assert.AreNotEqual(0, new FailedInsightsOptions
        {
            EnrichmentCeiling = ceiling, EnrichmentCacheSeconds = cacheSeconds, BulkReadBatchSize = batch,
            BulkReadConcurrency = concurrency, TopPatternsPerGroup = patterns,
        }.Validate().Count);

    [TestMethod]
    public void The_default_options_are_valid() => Assert.AreEqual(0, new FailedInsightsOptions().Validate().Count);

    [TestMethod]
    public async Task The_api_serializes_the_enrichment_status_as_the_spec_declares()
    {
        var store = new FixedRowsStore(Enumerable.Range(0, 101).Select(i => Row("orders", $"e{i}", $"m{i}", Now.AddMinutes(-i - 1))).ToArray());
        var cache = NewCache();
        var service = Service(store, new FakeEnrichment(), cache: cache, options: new FailedInsightsOptions { EnrichmentCeiling = 100 });
        using var host = await new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddSingleton<IMetricsApiController>(new MetricsImplementation(store, cache, new Authorization(true), service));
                services.AddControllers(options => options.ModelBinderProviders.Insert(0, new EnumMemberModelBinderProvider()))
                    .AddApplicationPart(typeof(MetricsApiController).Assembly)
                    .AddJsonOptions(options =>
                    {
                        options.JsonSerializerOptions.Converters.Add(new EnumMemberJsonConverterFactory());
                        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    });
            });
            web.Configure(app => { app.UseRouting(); app.UseEndpoints(e => e.MapControllers()); });
        }).StartAsync();
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.GetAsync($"/api/metrics/failed-insights?period=1d&from={From:O}&to={To:O}&endpointId=orders&refresh=true");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual("limitExceeded", json.RootElement.GetProperty("enrichment").GetString());
        Assert.AreEqual(100, json.RootElement.GetProperty("enrichmentLimit").GetInt32());
        Assert.AreEqual("orders", json.RootElement.GetProperty("endpointId").GetString());
    }

    internal static StoreResultCache NewCache() => new(new MemoryCache(new MemoryCacheOptions()));

    internal static FailedInsightsService Service(InMemoryMessageStore store, IInsightsEnrichmentSource enrichment, IStoreResultCache? cache = null,
        IEndpointAuthorizationService? authorization = null, FailedInsightsOptions? options = null, TimeProvider? clock = null)
        => new(store, store, cache ?? NewCache(), authorization ?? new Authorization(true), enrichment,
            Options.Create(options ?? new FailedInsightsOptions()), clock ?? new FixedClock(Now));

    /// <summary>A MetricsImplementation wired to a real FailedInsightsService with intelligence off.</summary>
    internal static MetricsImplementation Metrics(InMemoryMessageStore store, IStoreResultCache cache, IEndpointAuthorizationService authorization)
        => new(store, cache, authorization, new FailedInsightsService(store, store, cache, authorization,
            new FakeEnrichment { IsEnabled = false }, Options.Create(new FailedInsightsOptions())));

    private static async Task<FailedInsightsOverview> Build(InMemoryMessageStore store, IInsightsEnrichmentSource enrichment, string? endpointId = null,
        IStoreResultCache? cache = null, FailedInsightsOptions? options = null, bool enrich = true, bool refresh = false)
    {
        var result = await Service(store, enrichment, cache, options: options)
            .GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, endpointId, enrich, refresh));
        Assert.AreEqual(FailedInsightsQueryStatus.Ok, result.Status);
        return result.Overview!;
    }

    private static InsightGroup Group(IEnumerable<InsightGroup>? groups, string key)
        => groups!.Single(g => g.GroupKey == key);

    private static Task Fail(InMemoryMessageStore store, string endpointId, string eventId, string messageId, string errorText,
        int minutesAgo = 60, DateTime? at = null, string sessionId = "s1", string? publisher = null)
        => store.StoreMessage(new MessageEntity
        {
            OriginatingFrom = publisher!,
            EventId = eventId,
            MessageId = messageId,
            SessionId = sessionId,
            EndpointId = endpointId,
            EventTypeId = "OrderPlaced",
            MessageType = NimBus.Core.Messages.MessageType.ErrorResponse,
            EnqueuedTimeUtc = at ?? Now.AddMinutes(-minutesAgo),
            MessageContent = new NimBus.Core.Messages.MessageContent { ErrorContent = new NimBus.Core.Messages.ErrorContent { ErrorText = errorText } },
        });

    private static FailedMessageInfo Row(string endpointId, string eventId, string? messageId, DateTime at) => new()
    {
        EndpointId = endpointId, EventId = eventId, MessageId = messageId, EventTypeId = "OrderPlaced", ErrorText = "Timeout", EnqueuedTimeUtc = at,
    };

    internal static FailureClassification Result(string messageId, string eventId, string endpointId, string category, FailureGuidance guidance) => new()
    {
        Id = $"{messageId}:1", FailureMessageId = messageId, Revision = 1, EventId = eventId, EventTypeId = "OrderPlaced", EndpointId = endpointId,
        Provider = "TypeSafe", Model = "test", QuestionSetVersion = 1, Category = category, CategoryConfidence = 0.9,
        CategoryProbabilities = new Dictionary<string, double> { [category] = 0.9 }, RetryLikelihood = 0.5, ChangeRequiredLikelihood = 0.5,
        ExternalDependencyLikelihood = 0.5, Guidance = guidance, EventPayloadIncluded = false, RequestedBy = "operator", CreatedAtUtc = DateTimeOffset.UtcNow,
    };

    internal sealed class FakeEnrichment : IInsightsEnrichmentSource
    {
        private int _reads;
        public bool IsEnabled { get; set; } = true;
        public bool CanAnalyze { get; set; } = true;
        public HashSet<string> Disallowed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ClassificationSnapshot> Snapshots { get; } = new(StringComparer.Ordinal);
        public ClassificationBulkReadStatus Status { get; set; } = ClassificationBulkReadStatus.Available;
        public Func<Task>? Gate { get; set; }
        public List<int> RequestedCounts { get; } = [];
        public int Reads => _reads;

        public void Save(FailureClassification result)
            => Snapshots[result.FailureMessageId] = new ClassificationSnapshot(result.FailureMessageId, result, "Completed", null, false);

        public bool IsEligible(string endpointId) => !Disallowed.Contains(endpointId);

        public async Task<ClassificationBulkReadResult> ReadAsync(IReadOnlyCollection<string> failureMessageIds, ClassificationBulkReadOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reads);
            lock (RequestedCounts) RequestedCounts.Add(failureMessageIds.Count);
            // Like FailureClassificationReader, a cancelled read throws.
            if (Gate is not null) await Gate().WaitAsync(cancellationToken);
            var snapshots = Status == ClassificationBulkReadStatus.Available
                ? failureMessageIds.Where(Snapshots.ContainsKey).ToDictionary(id => id, id => Snapshots[id])
                : new Dictionary<string, ClassificationSnapshot>();
            return new ClassificationBulkReadResult(Status, snapshots);
        }
    }

    internal class CountingStore : InMemoryMessageStore
    {
        private int _calls;
        public int Calls => _calls;

        public override Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to)
        {
            Interlocked.Increment(ref _calls);
            return base.GetFailedMessageInsights(from, to);
        }
    }

    private sealed class FixedRowsStore(params FailedMessageInfo[] rows) : InMemoryMessageStore
    {
        public override Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to) => Task.FromResult(rows.ToList());
    }

    /// <summary>Records the cancellation token of every query and answers Forbidden.</summary>
    private sealed class CapturingQuery : IFailedInsightsQuery
    {
        public List<CancellationToken> Tokens { get; } = [];

        public Task<FailedInsightsQueryResult> GetOverviewAsync(FailedInsightsRequest request, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult(new FailedInsightsQueryResult(FailedInsightsQueryStatus.Forbidden, null));
        }

        public Task<FailedInsightsPageResult<InsightOccurrencePage>> GetOccurrencesAsync(FailedInsightsPageRequest request, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult(new FailedInsightsPageResult<InsightOccurrencePage>(FailedInsightsQueryStatus.Forbidden, null));
        }

        public Task<FailedInsightsPageResult<InsightGroupPatternPage>> GetPatternsAsync(FailedInsightsPageRequest request, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult(new FailedInsightsPageResult<InsightGroupPatternPage>(FailedInsightsQueryStatus.Forbidden, null));
        }

        public Task<FailureOccurrenceSet> GetOccurrencesForEndpointsAsync(Period period, IReadOnlyCollection<string> endpointIds, CancellationToken cancellationToken = default)
        {
            Tokens.Add(cancellationToken);
            return Task.FromResult(new FailureOccurrenceSet(Now, Now, DateTimeOffset.UtcNow, []));
        }
    }

    private sealed class FixedClock(DateTime now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(now);
    }

    internal sealed class Authorization(bool siteReader) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(siteReader);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess { SiteRole = siteReader ? AccessRole.Reader : AccessRole.None });
        public string GetCurrentUserName() => "test-user";
    }
}
