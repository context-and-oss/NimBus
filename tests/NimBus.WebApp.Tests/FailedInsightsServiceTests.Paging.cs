#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services.Insights;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

/// <summary>Spec 043 §8.2: occurrence drill-down and group-pattern pages.</summary>
public sealed partial class FailedInsightsServiceTests
{
    [TestMethod]
    public async Task Occurrences_page_newest_first_with_ties_by_message_id_and_keyset_continuation()
    {
        var store = new CountingStore();
        var tie = Now.AddMinutes(-30);
        await Fail(store, "orders", "e1", "m-b", "Timeout calling CRM", at: tie);
        await Fail(store, "orders", "e2", "m-a", "Timeout calling CRM", at: tie);
        for (var i = 0; i < 5; i++) await Fail(store, "orders", $"e{i + 3}", $"m-{i}", "Timeout calling CRM", minutesAgo: 60 + i);
        var service = Service(store, new FakeEnrichment());
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).Overview!;
        var key = overview.Groups.Single().GroupKey;

        var seen = new List<InsightOccurrence>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, key, 3, token));
            Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
            seen.AddRange(page.Page!.Occurrences);
            token = page.Page.ContinuationToken;
            pages++;
        }
        while (token is not null);

        Assert.AreEqual(3, pages);
        CollectionAssert.AreEqual(new[] { "m-a", "m-b", "m-0", "m-1", "m-2", "m-3", "m-4" }, seen.Select(o => o.MessageId).ToArray());
        Assert.AreEqual(1, store.Calls, "Overview and every page share one occurrence read.");
    }

    [TestMethod]
    public async Task Paging_visits_every_row_when_failure_times_tie_and_message_ids_are_missing()
    {
        // A store without failed-attempt ids (enrichment unsupported) still pages error patterns.
        var tie = Now.AddMinutes(-30);
        var store = new FixedRowsStore(Row("orders", "e1", null, tie), Row("orders", "e2", null, tie), Row("billing", "e1", null, tie));
        var service = Service(store, new FakeEnrichment());
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).Overview!;
        Assert.AreEqual(InsightsEnrichmentStatus.Unsupported, overview.Enrichment);

        var seen = new List<string>();
        string? token = null;
        do
        {
            var page = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern,
                overview.Groups.Single().GroupKey, 1, token));
            Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
            seen.AddRange(page.Page!.Occurrences.Select(o => $"{o.EndpointId}/{o.EventId}"));
            token = page.Page.ContinuationToken;
        }
        while (token is not null && seen.Count < 10);

        CollectionAssert.AreEquivalent(new[] { "orders/e1", "orders/e2", "billing/e1" }, seen);
    }

    [TestMethod]
    public async Task A_cause_or_guidance_key_without_occurrences_in_the_scope_is_a_bad_request()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var service = Service(store, new FakeEnrichment());

        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, "business_rule", 10, null))).Status);
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetPatternsAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Guidance, nameof(FailureGuidance.RetryMayHelp), 10, null))).Status);
        Assert.AreEqual(FailedInsightsQueryStatus.Ok, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null))).Status,
            "A key with occurrences in the scope pages.");
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(101)]
    public async Task A_page_size_outside_1_to_100_is_a_bad_request(int pageSize)
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var service = Service(store, new FakeEnrichment());

        var page = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, pageSize, null));

        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, page.Status);
    }

    [TestMethod]
    public async Task The_default_page_size_is_50()
    {
        var store = new CountingStore();
        for (var i = 0; i < 60; i++) await Fail(store, "orders", $"e{i}", $"m{i}", "x", minutesAgo: i + 1);
        var service = Service(store, new FakeEnrichment());

        var page = (await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, null, null))).Page!;

        Assert.HasCount(50, page.Occurrences);
        Assert.IsNotNull(page.ContinuationToken);
    }

    [TestMethod]
    public async Task Unknown_group_keys_and_foreign_or_garbled_continuation_tokens_are_bad_requests()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout calling CRM", minutesAgo: 10);
        await Fail(store, "orders", "e2", "m2", "Customer not found", minutesAgo: 20);
        await Fail(store, "orders", "e3", "m3", "Customer not found", minutesAgo: 30);
        var service = Service(store, new FakeEnrichment());
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).Overview!;
        var customer = overview.Groups.Single(g => g.Count == 2).GroupKey;
        var timeout = overview.Groups.Single(g => g.Count == 1).GroupKey;

        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, "ec:" + new string('0', 32), 10, null))).Status);
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, "quantum_flux", 10, null))).Status);

        var first = (await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, customer, 1, null))).Page!;
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, timeout, 1, first.ContinuationToken))).Status,
            "A token is bound to the group it came from.");
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, "orders", InsightsPerspective.ErrorPattern, customer, 1, first.ContinuationToken))).Status,
            "A token is bound to the endpoint filter it came from.");
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetOccurrencesAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, customer, 1, "not-a-token"))).Status);
    }

    [TestMethod]
    public async Task Cause_drill_down_needs_saved_classifications_but_error_pattern_drill_down_does_not()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout calling CRM");
        var service = Service(store, new FakeEnrichment { IsEnabled = false });
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).Overview!;

        var cause = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null));
        var pattern = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, overview.Groups.Single().GroupKey, 10, null));

        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, cause.Status);
        StringAssert.Contains(cause.Error, "Disabled", StringComparison.Ordinal);
        Assert.AreEqual(FailedInsightsQueryStatus.Ok, pattern.Status);
        Assert.AreEqual(InsightAnalysisState.Unavailable, pattern.Page!.Occurrences.Single().AnalysisState);
        Assert.AreEqual(InsightsEnrichmentStatus.Disabled, pattern.Page.Enrichment);
    }

    [TestMethod]
    public async Task Rows_carry_their_analysis_state_marker_and_saved_values()
    {
        var store = new CountingStore();
        var enrichment = new FakeEnrichment();
        await Fail(store, "orders", "e1", "running", "x", minutesAgo: 1);
        enrichment.Snapshots["running"] = new ClassificationSnapshot("running",
            Result("running", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp), "Active", null, false);
        await Fail(store, "orders", "e2", "unknown-outcome", "x", minutesAgo: 2);
        enrichment.Snapshots["unknown-outcome"] = new ClassificationSnapshot("unknown-outcome",
            Result("unknown-outcome", "e2", "orders", "transient_dependency", FailureGuidance.RetryMayHelp), "Failed", "AnalysisOutcomeUnknown", false);
        await Fail(store, "orders", "e3", "failed-again", "x", minutesAgo: 3);
        enrichment.Snapshots["failed-again"] = new ClassificationSnapshot("failed-again",
            Result("failed-again", "e3", "orders", "transient_dependency", FailureGuidance.RetryMayHelp), "Failed", "ProviderUnavailable", false);
        await Fail(store, "orders", "e4", "suggested", "x", minutesAgo: 4);
        enrichment.Save(Result("suggested", "e4", "orders", "business_rule", FailureGuidance.Uncertain));
        await Fail(store, "orders", "e5", "mismatch", "x", minutesAgo: 5);
        enrichment.Save(Result("mismatch", "e5", "invoices", "business_rule", FailureGuidance.ChangeLikelyRequired));
        await Fail(store, "orders", "e6", "none", "x", minutesAgo: 6);
        var service = Service(store, enrichment);
        var overview = (await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, From, To, null, true, false))).Overview!;

        var rows = (await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern,
            overview.Groups.Single().GroupKey, 100, null))).Page!.Occurrences.ToDictionary(o => o.MessageId);

        Assert.AreEqual(InsightAnalysisState.Classified, rows["running"].AnalysisState);
        Assert.AreEqual("analysisRunning", rows["running"].OperationMarker);
        Assert.AreEqual("transient_dependency", rows["running"].Category);
        Assert.AreEqual(nameof(FailureGuidance.RetryMayHelp), rows["running"].Guidance);
        Assert.AreEqual(1, rows["running"].Revision);
        Assert.AreEqual("outcomeUnknown", rows["unknown-outcome"].OperationMarker);
        Assert.AreEqual("lastAnalysisFailed", rows["failed-again"].OperationMarker);
        Assert.AreEqual(InsightAnalysisState.Uncertain, rows["suggested"].AnalysisState);
        Assert.AreEqual("business_rule", rows["suggested"].Category, "An uncertain result keeps the provider's suggestion in details.");
        Assert.AreEqual(0.9, rows["suggested"].CategoryConfidence);
        Assert.IsNull(rows["suggested"].OperationMarker);
        Assert.AreEqual(InsightAnalysisState.Uncertain, rows["mismatch"].AnalysisState);
        Assert.AreEqual("identityMismatch", rows["mismatch"].OperationMarker);
        Assert.IsNull(rows["mismatch"].Category, "A result that belongs to another source is never shown.");
        Assert.AreEqual(InsightAnalysisState.NotAnalyzed, rows["none"].AnalysisState);
        Assert.IsNull(rows["none"].Category);
    }

    [TestMethod]
    public async Task Current_status_is_read_once_per_endpoint_per_page_with_stored_event_ids()
    {
        var store = new StatusStore();
        await Fail(store, "orders", "e1", "m1", "x", minutesAgo: 1, sessionId: "s-a");
        await Fail(store, "orders", "e2", "m2", "x", minutesAgo: 2, sessionId: "s-b");
        await Fail(store, "billing", "e3", "m3", "x", minutesAgo: 3, sessionId: "s-c");
        await Fail(store, "billing", "e4", "m4", "x", minutesAgo: 4, sessionId: "s-d");
        store.Statuses["e1_s-a"] = ResolutionStatus.Completed;
        store.Statuses["e2_s-b"] = ResolutionStatus.Failed;
        store.Statuses["e3_s-c"] = ResolutionStatus.Skipped;
        var service = Service(store, new FakeEnrichment());

        var rows = (await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause,
            FailedInsightsService.NotAnalyzed, 100, null))).Page!.Occurrences.ToDictionary(o => o.MessageId);

        Assert.HasCount(2, store.Lookups, "One lookup per endpoint on the page.");
        CollectionAssert.AreEquivalent(new[] { "e1_s-a", "e2_s-b" }, store.Lookups.Single(l => l.Endpoint == "orders").Ids.ToArray());
        Assert.AreEqual("Completed", rows["m1"].CurrentStatus);
        Assert.AreEqual("Failed", rows["m2"].CurrentStatus);
        Assert.AreEqual("Skipped", rows["m3"].CurrentStatus);
        Assert.IsNull(rows["m4"].CurrentStatus, "A missing event is unavailable, never implied unresolved.");
    }

    [TestMethod]
    public async Task A_caller_without_site_reader_gets_no_page_and_no_read()
    {
        var store = new CountingStore();
        var service = Service(store, new FakeEnrichment(), authorization: new Authorization(false));

        var occurrences = await service.GetOccurrencesAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null));
        var patterns = await service.GetPatternsAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, FailedInsightsService.NotAnalyzed, 10, null));

        Assert.AreEqual(FailedInsightsQueryStatus.Forbidden, occurrences.Status);
        Assert.AreEqual(FailedInsightsQueryStatus.Forbidden, patterns.Status);
        Assert.IsNull(occurrences.Page);
        Assert.AreEqual(0, store.Calls);
    }

    [TestMethod]
    public async Task Patterns_page_a_cause_group_most_frequent_first_and_reject_the_error_pattern_perspective()
    {
        var store = new CountingStore();
        var enrichment = new FakeEnrichment();
        var texts = new[] { "Timeout A", "Timeout A", "Timeout A", "Refused B", "Refused B", "Reset C", "Reset C", "Gone D", "Late E" };
        for (var i = 0; i < texts.Length; i++)
        {
            await Fail(store, "orders", $"e{i}", $"m{i}", texts[i], minutesAgo: i + 1);
            enrichment.Save(Result($"m{i}", $"e{i}", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        }
        var service = Service(store, enrichment);

        var counts = new List<int>();
        var keys = new List<string>();
        string? token = null;
        do
        {
            var page = await service.GetPatternsAsync(new FailedInsightsPageRequest(From, To, null, InsightsPerspective.Cause, "transient_dependency", 2, token));
            Assert.AreEqual(FailedInsightsQueryStatus.Ok, page.Status, page.Error);
            counts.AddRange(page.Page!.Patterns.Select(p => p.Count));
            keys.AddRange(page.Page.Patterns.Select(p => p.GroupKey));
            token = page.Page.ContinuationToken;
        }
        while (token is not null);

        CollectionAssert.AreEqual(new[] { 3, 2, 2, 1, 1 }, counts.ToArray());
        Assert.AreEqual(5, keys.Distinct().Count());
        Assert.AreEqual(FailedInsightsQueryStatus.BadRequest, (await service.GetPatternsAsync(
            new FailedInsightsPageRequest(From, To, null, InsightsPerspective.ErrorPattern, keys[0], 2, null))).Status);
    }

    [TestMethod]
    public async Task The_occurrences_route_binds_the_perspective_and_serializes_states_as_the_spec_declares()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "x");
        var enrichment = new FakeEnrichment();
        enrichment.Save(Result("m1", "e1", "orders", "transient_dependency", FailureGuidance.RetryMayHelp));
        var cache = NewCache();
        var service = Service(store, enrichment, cache: cache);
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

        using var ok = await client.GetAsync($"/api/metrics/failed-insights/occurrences?from={From:O}&to={To:O}&perspective=cause&groupKey=transient_dependency");
        using var bad = await client.GetAsync($"/api/metrics/failed-insights/occurrences?from={From:O}&to={To:O}&perspective=cause&groupKey=transient_dependency&pageSize=500");

        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        using var json = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        var row = json.RootElement.GetProperty("occurrences").EnumerateArray().Single();
        Assert.AreEqual("classified", row.GetProperty("analysisState").GetString());
        Assert.AreEqual("m1", row.GetProperty("messageId").GetString());
        Assert.AreEqual(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    /// <summary>Answers GetEventsByIds from a stored-id map and records each lookup.</summary>
    private sealed class StatusStore : CountingStore
    {
        public Dictionary<string, ResolutionStatus> Statuses { get; } = new(StringComparer.Ordinal);
        public List<(string Endpoint, List<string> Ids)> Lookups { get; } = [];

        public override Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
        {
            var ids = eventIds.ToList();
            Lookups.Add((endpointId, ids));
            return Task.FromResult(ids.Where(Statuses.ContainsKey).Select(id => new UnresolvedEvent
            {
                EndpointId = endpointId,
                EventId = id[..id.IndexOf('_', StringComparison.Ordinal)],
                SessionId = id[(id.IndexOf('_', StringComparison.Ordinal) + 1)..],
                ResolutionStatus = Statuses[id],
            }).ToList());
        }
    }
}
