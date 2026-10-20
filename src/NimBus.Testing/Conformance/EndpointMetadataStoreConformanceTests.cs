#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IEndpointMetadataStore"/>.
/// </summary>
[TestClass]
public abstract class EndpointMetadataStoreConformanceTests
{
    private static readonly DateTime T0 = new(2026, 07, 10, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract IEndpointMetadataStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task SetEndpointMetadata_then_GetEndpointMetadata_round_trips()
    {
        var store = CreateStore();
        var endpointId = Id("ep-meta");
        var metadata = SampleMetadata(endpointId);

        var saved = await store.SetEndpointMetadata(metadata);
        Assert.IsTrue(saved);

        var fetched = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(endpointId, fetched!.EndpointId);
        Assert.AreEqual("Team Blue", fetched.EndpointOwnerTeam);
        Assert.AreEqual("owner@example.com", fetched.EndpointOwnerEmail);
        Assert.AreEqual(true, fetched.SubscriptionStatus);
        Assert.AreEqual(1, fetched.TechnicalContacts!.Count);
        Assert.AreEqual("Ops", fetched.TechnicalContacts[0].Name);
    }

    [TestMethod]
    public async Task GetMetadatas_filters_by_endpoint_ids()
    {
        var store = CreateStore();
        var endpointOne = Id("ep-one");
        var endpointTwo = Id("ep-two");
        await store.SetEndpointMetadata(SampleMetadata(endpointOne));
        await store.SetEndpointMetadata(SampleMetadata(endpointTwo));

        var all = await store.GetMetadatas();
        Assert.IsTrue(all.Count >= 2);

        var filtered = await store.GetMetadatas(new[] { endpointTwo, Id("missing") });
        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered!.Count);
        Assert.AreEqual(endpointTwo, filtered[0].EndpointId);
    }

    [TestMethod]
    public async Task GetMetadatas_includes_heartbeat_rows()
    {
        var store = CreateStore();
        var endpointId = Id("batch-heartbeats");
        await store.SetHeartbeat(
            Probe("probe-1", HeartbeatStatus.On, T0, T0.AddMilliseconds(50), "1.2.3"),
            endpointId);

        var filtered = await store.GetMetadatas([endpointId]);
        var fromAll = (await store.GetMetadatas()).Single(metadata => metadata.EndpointId == endpointId);

        Assert.IsNotNull(filtered);
        Assert.AreEqual(1, filtered!.Single().Heartbeats!.Count,
            "The scheduled history fold uses the filtered batch overload and requires its probe rows.");
        Assert.AreEqual("probe-1", filtered.Single().Heartbeats!.Single().MessageId);
        Assert.AreEqual(1, fromAll.Heartbeats!.Count,
            "Both batch overloads must return complete EndpointMetadata records.");
    }

    // ───────── Heartbeat ─────────

    [TestMethod]
    public async Task SetHeartbeat_settles_the_pending_row_instead_of_duplicating_it()
    {
        var store = CreateStore();
        var endpointId = Id("hb-settle");

        await store.SetHeartbeat(Probe("probe-1", HeartbeatStatus.Pending, T0), endpointId);

        var afterSend = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(1, afterSend!.Heartbeats!.Count);
        Assert.AreEqual(HeartbeatStatus.Pending, afterSend.EndpointHeartbeatStatus);

        // Same MessageId: the answer settles the row the send created.
        await store.SetHeartbeat(
            Probe("probe-1", HeartbeatStatus.On, T0, T0.AddMilliseconds(150), "9.9.9"),
            endpointId);

        var afterAnswer = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(1, afterAnswer!.Heartbeats!.Count);
        var row = afterAnswer.Heartbeats[0];
        Assert.AreEqual(HeartbeatStatus.On, row.EndpointHeartbeatStatus);
        Assert.AreEqual(T0.Ticks, row.StartTime.Ticks);
        Assert.AreEqual(T0.AddMilliseconds(150).Ticks, row.EndTime.Ticks);
        Assert.AreEqual("9.9.9", row.SdkVersion);
        Assert.AreEqual(HeartbeatStatus.On, afterAnswer.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task SetHeartbeat_prunes_history_to_the_most_recent_rows()
    {
        var store = CreateStore();
        var endpointId = Id("hb-prune");
        const int written = HeartbeatRollup.MaxHeartbeatsPerEndpoint + 5;

        for (var i = 0; i < written; i++)
        {
            var start = T0.AddMinutes(i);
            await store.SetHeartbeat(
                Probe($"probe-{i:D2}", HeartbeatStatus.On, start, start.AddMilliseconds(10), "1.0.0"),
                endpointId);
        }

        var metadata = await store.GetEndpointMetadata(endpointId);
        Assert.AreEqual(HeartbeatRollup.MaxHeartbeatsPerEndpoint, metadata!.Heartbeats!.Count);
        // The oldest five were dropped, not the newest.
        Assert.AreEqual(T0.AddMinutes(5).Ticks, metadata.Heartbeats.Min(h => h.StartTime).Ticks);
        Assert.AreEqual(T0.AddMinutes(written - 1).Ticks, metadata.Heartbeats.Max(h => h.StartTime).Ticks);
    }

    [TestMethod]
    public async Task EnableHeartbeatOnEndpoint_drives_the_fan_out_filter()
    {
        var store = CreateStore();
        var optedIn = Id("hb-opt-in");
        var optedOut = Id("hb-opt-out");

        await store.EnableHeartbeatOnEndpoint(optedIn, true);
        await store.EnableHeartbeatOnEndpoint(optedOut, false);

        Assert.AreEqual(true, (await store.GetEndpointMetadata(optedIn))!.IsHeartbeatEnabled);
        Assert.AreEqual(false, (await store.GetEndpointMetadata(optedOut))!.IsHeartbeatEnabled);

        var enabled = (await store.GetMetadatasWithEnabledHeartbeat()).Select(m => m.EndpointId).ToList();
        CollectionAssert.Contains(enabled, optedIn);
        CollectionAssert.DoesNotContain(enabled, optedOut);

        await store.EnableHeartbeatOnEndpoint(optedIn, false);
        var afterOptOut = (await store.GetMetadatasWithEnabledHeartbeat()).Select(m => m.EndpointId).ToList();
        CollectionAssert.DoesNotContain(afterOptOut, optedIn);
    }

    [TestMethod]
    public async Task GetHeartbeatSettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetHeartbeatSettings();

        // Providers whose backing record has never been written report the documented
        // defaults; a shared backend may already carry an operator's edit, so only the
        // never-null contract and the shape are asserted here.
        Assert.IsNotNull(settings);
        Assert.AreEqual(HeartbeatSettings.SingletonId, settings.Id);
        Assert.IsTrue(settings.IntervalSeconds > 0);
        Assert.IsTrue(settings.TimeoutSeconds > 0);

        var defaults = new HeartbeatSettings();
        Assert.IsFalse(defaults.Enabled);
        Assert.AreEqual(300, defaults.IntervalSeconds);
        Assert.AreEqual(60, defaults.TimeoutSeconds);
    }

    [TestMethod]
    public async Task SetHeartbeatSettings_round_trips_and_keeps_LastSentAtUtc_when_none_is_supplied()
    {
        var store = CreateStore();
        var stamp = T0.AddHours(-3);

        Assert.IsTrue(await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 90,
            TimeoutSeconds = 45,
            LastSentAtUtc = stamp,
        }));

        var stored = await store.GetHeartbeatSettings();
        Assert.IsTrue(stored.Enabled);
        Assert.AreEqual(90, stored.IntervalSeconds);
        Assert.AreEqual(45, stored.TimeoutSeconds);
        Assert.AreEqual(stamp.Ticks, stored.LastSentAtUtc!.Value.Ticks);

        // The claim owns LastSentAtUtc: an operator edit must not reset the schedule.
        Assert.IsTrue(await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = false,
            IntervalSeconds = 120,
            TimeoutSeconds = 60,
            LastSentAtUtc = null,
        }));

        var preserved = await store.GetHeartbeatSettings();
        Assert.IsFalse(preserved.Enabled);
        Assert.AreEqual(120, preserved.IntervalSeconds);
        Assert.AreEqual(stamp.Ticks, preserved.LastSentAtUtc!.Value.Ticks);
    }

    [TestMethod]
    public async Task GetAuditSettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetAuditSettings();

        // A shared backend may already carry an operator's selection, so only the
        // never-null contract is asserted against the store; the default is asserted
        // on the type.
        Assert.IsNotNull(settings);
        Assert.AreEqual(AuditSettings.SingletonId, settings.Id);
        Assert.IsNotNull(settings.DisabledAuditTypes);
        Assert.AreEqual(0, new AuditSettings().DisabledAuditTypes.Count);
    }

    [TestMethod]
    public async Task SetAuditSettings_round_trips_and_replaces_the_stored_selection()
    {
        var store = CreateStore();

        Assert.IsTrue(await store.SetAuditSettings(new AuditSettings
        {
            DisabledAuditTypes = new List<string> { "SearchEvents", "FailureClassified" },
        }));
        var stored = await store.GetAuditSettings();
        CollectionAssert.AreEquivalent(new[] { "SearchEvents", "FailureClassified" }, stored.DisabledAuditTypes);

        // A write replaces the list rather than merging, so re-enabling a type sticks.
        Assert.IsTrue(await store.SetAuditSettings(new AuditSettings
        {
            DisabledAuditTypes = new List<string> { "GetEventDetails" },
        }));
        var replaced = await store.GetAuditSettings();
        CollectionAssert.AreEquivalent(new[] { "GetEventDetails" }, replaced.DisabledAuditTypes);

        Assert.IsTrue(await store.SetAuditSettings(new AuditSettings()));
        Assert.AreEqual(0, (await store.GetAuditSettings()).DisabledAuditTypes.Count);
    }

    [TestMethod]
    public async Task GetMcpAccessSettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetMcpAccessSettings();

        // A shared backend may already carry a saved policy, so the defaults are asserted
        // on the type and only the never-null contract against the store.
        Assert.IsNotNull(settings);
        Assert.AreEqual(McpAccessSettings.SingletonId, settings.Id);
        Assert.IsNotNull(settings.Capabilities);
        Assert.IsNotNull(settings.People?.Principals);
        Assert.IsNotNull(settings.Clients?.Approved);
        Assert.IsNotNull(settings.Endpoints?.Hidden);
        Assert.IsNotNull(settings.Limits);

        var defaults = new McpAccessSettings();
        Assert.IsNull(defaults.Revision);
        Assert.IsTrue(defaults.Enabled);
        Assert.IsTrue(defaults.Capabilities.Skip);
    }

    [TestMethod]
    public async Task TrySetMcpAccessSettings_round_trips_every_field()
    {
        var store = CreateStore();
        var sample = SampleMcpAccessSettings();

        await SaveMcpAccessSettingsAsync(store, sample);
        var stored = await store.GetMcpAccessSettings();

        Assert.AreEqual(sample.Revision, stored.Revision);
        Assert.AreEqual(sample.UpdatedBy, stored.UpdatedBy);
        Assert.AreEqual(sample.UpdatedAtUtc!.Value.Ticks, stored.UpdatedAtUtc!.Value.Ticks);
        Assert.IsFalse(stored.Enabled);
        Assert.IsFalse(stored.Capabilities.Payloads);
        Assert.IsTrue(stored.Capabilities.Report);
        Assert.IsFalse(stored.Capabilities.Classify);
        Assert.IsTrue(stored.Capabilities.Resubmit);
        Assert.IsFalse(stored.Capabilities.Skip);
        Assert.IsFalse(stored.AllowWorkloads);
        Assert.AreEqual(McpPeopleMode.Listed, stored.People.Mode);
        Assert.AreEqual("pilot@example.com", stored.People.Principals.Single().Principal);
        Assert.AreEqual("Pilot", stored.People.Principals.Single().Label);
        Assert.AreEqual(McpClientMode.Approved, stored.Clients.Mode);
        Assert.AreEqual("5c1e9a40-7d2b-4f6e-9a31-0b8d2c7e4f19", stored.Clients.Approved.Single().ClientId);
        Assert.AreEqual("Claude Code", stored.Clients.Approved.Single().Name);
        Assert.IsTrue(stored.Clients.Approved.Single().MayChange);
        Assert.AreEqual(McpEndpointVisibility.AllExcept, stored.Endpoints.Visibility);
        CollectionAssert.AreEqual(new[] { "PayrollEndpoint" }, stored.Endpoints.Hidden);
        Assert.AreEqual(McpChangeScope.Listed, stored.Endpoints.Changes);
        CollectionAssert.AreEqual(new[] { "ErpEndpoint" }, stored.Endpoints.ChangeOn);
        Assert.AreEqual(30, stored.Limits.RequestsPerWindow);
        Assert.AreEqual(2, stored.Limits.MutationsPerWindow);
    }

    [TestMethod]
    public async Task TrySetMcpAccessSettings_refuses_a_stale_revision_and_keeps_the_record()
    {
        var store = CreateStore();
        var saved = await SaveMcpAccessSettingsAsync(store, SampleMcpAccessSettings());

        var stale = SampleMcpAccessSettings();
        stale.Enabled = true;
        stale.Revision = Guid.NewGuid().ToString();
        Assert.IsFalse(await store.TrySetMcpAccessSettings(stale, Guid.NewGuid().ToString()));

        // Null means "create": refused because a record exists.
        Assert.IsFalse(await store.TrySetMcpAccessSettings(stale, null));

        var stored = await store.GetMcpAccessSettings();
        Assert.AreEqual(saved, stored.Revision);
        Assert.IsFalse(stored.Enabled);
    }

    [TestMethod]
    public async Task TrySetMcpAccessSettings_lets_exactly_one_of_two_concurrent_writers_win()
    {
        var store = CreateStore();
        var current = await SaveMcpAccessSettingsAsync(store, SampleMcpAccessSettings());

        var first = SampleMcpAccessSettings();
        first.Revision = Guid.NewGuid().ToString();
        var second = SampleMcpAccessSettings();
        second.Revision = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(
            store.TrySetMcpAccessSettings(first, current),
            store.TrySetMcpAccessSettings(second, current));

        Assert.AreEqual(1, results.Count(r => r));
        var winner = results[0] ? first.Revision : second.Revision;
        Assert.AreEqual(winner, (await store.GetMcpAccessSettings()).Revision);
    }

    /// <summary>Saves <paramref name="settings"/> over whatever is stored and returns its new revision.</summary>
    private static async Task<string> SaveMcpAccessSettingsAsync(IEndpointMetadataStore store, McpAccessSettings settings)
    {
        var current = await store.GetMcpAccessSettings();
        settings.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, current.Revision));
        return settings.Revision;
    }

    private static McpAccessSettings SampleMcpAccessSettings() => new()
    {
        UpdatedBy = "owner@example.com",
        UpdatedAtUtc = T0,
        Enabled = false,
        Capabilities = new McpCapabilitySettings { Payloads = false, Report = true, Classify = false, Resubmit = true, Skip = false },
        AllowWorkloads = false,
        People = new McpPeopleSettings
        {
            Mode = McpPeopleMode.Listed,
            Principals = new List<McpPrincipal> { new() { Principal = "pilot@example.com", Label = "Pilot" } },
        },
        Clients = new McpClientSettings
        {
            Mode = McpClientMode.Approved,
            Approved = new List<McpApprovedClient>
            {
                new() { ClientId = "5c1e9a40-7d2b-4f6e-9a31-0b8d2c7e4f19", Name = "Claude Code", MayChange = true },
            },
        },
        Endpoints = new McpEndpointSettings
        {
            Visibility = McpEndpointVisibility.AllExcept,
            Hidden = new List<string> { "PayrollEndpoint" },
            Changes = McpChangeScope.Listed,
            ChangeOn = new List<string> { "ErpEndpoint" },
        },
        Limits = new McpLimitSettings { RequestsPerWindow = 30, MutationsPerWindow = 2 },
    };

    [TestMethod]
    public async Task TryClaimHeartbeatSend_lets_exactly_one_caller_send_per_interval()
    {
        var store = CreateStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 300,
            TimeoutSeconds = 60,
        });

        // Wide windows (±5 min) so the assertions hold whatever the store's own clock
        // reads relative to this process.
        Assert.IsTrue(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)),
            "A send that is due must be claimable.");
        Assert.IsFalse(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(-5)),
            "The claim just stamped a send, so the next one is not due yet.");
        Assert.IsTrue(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)),
            "Once the interval has elapsed the claim is available again.");
    }

    [TestMethod]
    public async Task TryClaimHeartbeatSend_returns_false_while_heartbeats_are_disabled()
    {
        var store = CreateStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings
        {
            Enabled = false,
            IntervalSeconds = 300,
            TimeoutSeconds = 60,
        });

        Assert.IsFalse(await store.TryClaimHeartbeatSend(DateTime.UtcNow.AddMinutes(5)));
    }

    [TestMethod]
    public async Task SweepTimedOutHeartbeats_settles_only_stale_pending_rows()
    {
        var store = CreateStore();
        var stale = Id("hb-stale");
        var fresh = Id("hb-fresh");
        var answered = Id("hb-answered");
        var now = DateTime.UtcNow;

        await store.SetHeartbeat(Probe("stale-1", HeartbeatStatus.Pending, now.AddMinutes(-10)), stale);
        await store.SetHeartbeat(Probe("fresh-1", HeartbeatStatus.Pending, now), fresh);
        await store.SetHeartbeat(
            Probe("answered-1", HeartbeatStatus.On, now.AddMinutes(-10), now.AddMinutes(-10).AddMilliseconds(20), "1.0.0"),
            answered);

        var swept = await store.SweepTimedOutHeartbeats(now.AddMinutes(-5));

        CollectionAssert.Contains(swept, stale);
        CollectionAssert.DoesNotContain(swept, fresh);
        CollectionAssert.DoesNotContain(swept, answered);
        Assert.AreEqual(HeartbeatStatus.Off, (await store.GetEndpointMetadata(stale))!.EndpointHeartbeatStatus);
        Assert.AreEqual(HeartbeatStatus.Pending, (await store.GetEndpointMetadata(fresh))!.EndpointHeartbeatStatus);
        Assert.AreEqual(HeartbeatStatus.On, (await store.GetEndpointMetadata(answered))!.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task SweepTimedOutHeartbeats_settles_a_whole_second_probe_by_a_cutoff_later_in_that_second()
    {
        // Cosmos stores a whole-second StartTime as "…:00Z", which sorts after a "…:00.1Z"
        // cutoff; the sweep must compare instants, not those strings.
        var store = CreateStore();
        var endpointId = Id("hb-edge");
        var start = T0.AddDays(-1);
        await store.SetHeartbeat(Probe("edge-1", HeartbeatStatus.Pending, start), endpointId);

        var swept = await store.SweepTimedOutHeartbeats(start.AddMilliseconds(100));

        CollectionAssert.Contains(swept, endpointId);
        Assert.AreEqual(HeartbeatStatus.Off, (await store.GetEndpointMetadata(endpointId))!.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_in_flight_probe_does_not_mask_the_last_settled_outcome()
    {
        var store = CreateStore();
        var endpointId = Id("ov-inflight");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "10.0.5"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.On, item.Status);
        Assert.AreEqual(120L, item.RoundTripMs!.Value);
        Assert.AreEqual("10.0.5", item.SdkVersion);
        Assert.AreEqual("p2", item.MessageId);
        Assert.AreEqual(T0.AddMinutes(5).Ticks, item.LastStartTime!.Value.Ticks);
        Assert.AreEqual(T0.AddMilliseconds(120).Ticks, item.LastEndTime!.Value.Ticks);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_dead_endpoint_keeps_showing_off_while_the_next_probe_is_in_flight()
    {
        var store = CreateStore();
        var endpointId = Id("ov-dead");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.Off, T0), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, (await Overview(store, endpointId)).Status);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_first_probe_shows_pending_until_it_settles()
    {
        var store = CreateStore();
        var endpointId = Id("ov-first");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.Pending, T0), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Pending, item.Status);
        Assert.IsNull(item.RoundTripMs);
        Assert.IsNull(item.LastEndTime);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_endpoint_without_probes_reads_unknown()
    {
        var store = CreateStore();
        var endpointId = Id("ov-none");
        await store.SetEndpointMetadata(SampleMetadata(endpointId));

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Unknown, item.Status);
        Assert.IsNull(item.LastStartTime);
        Assert.IsNull(item.RoundTripMs);
    }

    [TestMethod]
    public async Task GetHeartbeatOverview_round_trip_and_sdk_version_come_from_the_last_answered_probe()
    {
        var store = CreateStore();
        var endpointId = Id("ov-answered");
        // A swept (timed-out) Off row never carried a response; its timestamps must
        // not feed the round-trip column.
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(80), "10.0.4"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Off, T0.AddMinutes(5)), endpointId);

        var item = await Overview(store, endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, item.Status);
        Assert.AreEqual(80L, item.RoundTripMs!.Value);
        Assert.AreEqual("10.0.4", item.SdkVersion);
    }

    [TestMethod]
    public async Task Rollup_keeps_the_settled_outcome_when_the_next_probe_is_written_as_pending()
    {
        var store = CreateStore();
        var endpointId = Id("rollup-pending");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "1.0.0"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Pending, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.On, (await store.GetEndpointMetadata(endpointId))!.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task Rollup_follows_the_newest_settled_outcome()
    {
        var store = CreateStore();
        var endpointId = Id("rollup-off");
        await store.SetHeartbeat(Probe("p1", HeartbeatStatus.On, T0, T0.AddMilliseconds(120), "1.0.0"), endpointId);
        await store.SetHeartbeat(Probe("p2", HeartbeatStatus.Off, T0.AddMinutes(5)), endpointId);

        Assert.AreEqual(HeartbeatStatus.Off, (await store.GetEndpointMetadata(endpointId))!.EndpointHeartbeatStatus);
    }

    private static async Task<HeartbeatOverviewItem> Overview(IEndpointMetadataStore store, string endpointId)
    {
        var overview = await store.GetHeartbeatOverview();
        var item = overview.SingleOrDefault(i => string.Equals(i.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(item, $"Overview should carry a row for '{endpointId}'.");
        return item!;
    }

    private static Heartbeat Probe(
        string messageId,
        HeartbeatStatus status,
        DateTime start,
        DateTime? end = null,
        string? sdkVersion = null) => new()
    {
        MessageId = messageId,
        StartTime = start,
        ReceivedTime = end ?? start,
        EndTime = end ?? start,
        SdkVersion = sdkVersion,
        EndpointHeartbeatStatus = status,
    };

    private static EndpointMetadata SampleMetadata(string endpointId) => new()
    {
        EndpointId = endpointId,
        EndpointOwner = "Alice",
        EndpointOwnerTeam = "Team Blue",
        EndpointOwnerEmail = "owner@example.com",
        TechnicalContacts = new List<TechnicalContact>
        {
            new() { Name = "Ops", Email = "ops@example.com" },
        },
        SubscriptionStatus = true,
    };

    // Single-row lookups return null for a missing row; optional strings round-trip exactly.

    [TestMethod]
    public async Task GetEndpointMetadata_returns_null_for_an_endpoint_without_metadata()
    {
        var store = CreateStore();

        Assert.IsNull(await store.GetEndpointMetadata(Id("no-metadata")));
    }

    [TestMethod]
    public async Task Optional_metadata_and_heartbeat_fields_round_trip_null()
    {
        var store = CreateStore();
        var endpointId = Id("ep-null-owner");
        await store.SetEndpointMetadata(new EndpointMetadata
        {
            EndpointId = endpointId,
            EndpointOwner = null,
            EndpointOwnerTeam = null,
            EndpointOwnerEmail = null,
            TechnicalContacts = new List<TechnicalContact>(),
        });
        await store.SetHeartbeat(Probe("probe-null-sdk", HeartbeatStatus.Pending, T0), endpointId);

        var fetched = await store.GetEndpointMetadata(endpointId);

        Assert.IsNotNull(fetched);
        Assert.IsNull(fetched.EndpointOwner);
        Assert.IsNull(fetched.EndpointOwnerTeam);
        Assert.IsNull(fetched.EndpointOwnerEmail);
        Assert.AreEqual(1, fetched.Heartbeats!.Count);
        Assert.IsNull(fetched.Heartbeats[0].SdkVersion, "a probe that has not been answered carries no SDK version");
    }

    [TestMethod]
    public async Task GetAgentApiKeySettings_always_returns_the_singleton_record()
    {
        var store = CreateStore();

        var settings = await store.GetAgentApiKeySettings();

        // A shared backend may already carry saved keys, so the empty default is asserted on
        // the type and only the never-null contract against the store.
        Assert.IsNotNull(settings);
        Assert.AreEqual(AgentApiKeySettings.SingletonId, settings.Id);
        Assert.IsNotNull(settings.Keys);
        Assert.IsNull(new AgentApiKeySettings().Revision);
        Assert.AreEqual(0, new AgentApiKeySettings().Keys.Count);
    }

    [TestMethod]
    public async Task TrySetAgentApiKeySettings_round_trips_every_field()
    {
        var store = CreateStore();
        var sample = SampleAgentApiKeySettings();

        await SaveAgentApiKeySettingsAsync(store, sample);
        var stored = await store.GetAgentApiKeySettings();

        Assert.AreEqual(sample.Revision, stored.Revision);
        Assert.AreEqual(sample.UpdatedBy, stored.UpdatedBy);
        Assert.AreEqual(sample.UpdatedAtUtc!.Value.Ticks, stored.UpdatedAtUtc!.Value.Ticks);
        Assert.AreEqual(2, stored.Keys.Count);

        var active = stored.Keys.Single(k => k.KeyId == "abcdefghijkl");
        Assert.AreEqual("enrichment-agent", active.AgentId);
        Assert.AreEqual("Production enrichment", active.Name);
        Assert.AreEqual("q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=", active.SecretHash);
        Assert.AreEqual(T0.Ticks, active.CreatedAtUtc.Ticks);
        Assert.AreEqual("owner@example.com", active.CreatedBy);
        Assert.AreEqual(T0.AddDays(90).Ticks, active.ExpiresAtUtc!.Value.Ticks);
        Assert.IsNull(active.RevokedAtUtc);
        Assert.IsNull(active.RevokedBy);

        var revoked = stored.Keys.Single(k => k.KeyId == "mnopqrstuvwx");
        Assert.IsNull(revoked.Name);
        Assert.IsNull(revoked.ExpiresAtUtc);
        Assert.AreEqual(T0.AddHours(1).Ticks, revoked.RevokedAtUtc!.Value.Ticks);
        Assert.AreEqual("owner@example.com", revoked.RevokedBy);
    }

    [TestMethod]
    public async Task TrySetAgentApiKeySettings_refuses_a_stale_revision_and_keeps_the_record()
    {
        var store = CreateStore();
        var saved = await SaveAgentApiKeySettingsAsync(store, SampleAgentApiKeySettings());

        var stale = SampleAgentApiKeySettings();
        stale.Keys.Clear();
        stale.Revision = Guid.NewGuid().ToString();
        Assert.IsFalse(await store.TrySetAgentApiKeySettings(stale, Guid.NewGuid().ToString()));

        // Null means "create": refused because a record exists.
        Assert.IsFalse(await store.TrySetAgentApiKeySettings(stale, null));

        var stored = await store.GetAgentApiKeySettings();
        Assert.AreEqual(saved, stored.Revision);
        Assert.AreEqual(2, stored.Keys.Count);
    }

    [TestMethod]
    public async Task TrySetAgentApiKeySettings_lets_exactly_one_of_two_concurrent_writers_win()
    {
        var store = CreateStore();
        var current = await SaveAgentApiKeySettingsAsync(store, SampleAgentApiKeySettings());

        var first = SampleAgentApiKeySettings();
        first.Revision = Guid.NewGuid().ToString();
        var second = SampleAgentApiKeySettings();
        second.Revision = Guid.NewGuid().ToString();

        var results = await Task.WhenAll(
            store.TrySetAgentApiKeySettings(first, current),
            store.TrySetAgentApiKeySettings(second, current));

        Assert.AreEqual(1, results.Count(r => r));
        var winner = results[0] ? first.Revision : second.Revision;
        Assert.AreEqual(winner, (await store.GetAgentApiKeySettings()).Revision);
    }

    [TestMethod]
    public async Task RecordAgentApiKeyUse_records_and_overwrites_per_key()
    {
        var store = CreateStore();
        // Key ids scoped to this test so a shared backend's other rows don't interfere.
        var keyA = _scope + "-a";
        var keyB = _scope + "-b";

        await store.RecordAgentApiKeyUse(keyA, T0);
        await store.RecordAgentApiKeyUse(keyB, T0.AddMinutes(1));
        await store.RecordAgentApiKeyUse(keyA, T0.AddMinutes(10));

        var usage = (await store.GetAgentApiKeyUsage())
            .Where(u => u.KeyId == keyA || u.KeyId == keyB)
            .ToDictionary(u => u.KeyId, u => u.LastUsedAtUtc);

        Assert.AreEqual(2, usage.Count);
        Assert.AreEqual(T0.AddMinutes(10).Ticks, usage[keyA].Ticks);
        Assert.AreEqual(T0.AddMinutes(1).Ticks, usage[keyB].Ticks);
    }

    /// <summary>Saves <paramref name="settings"/> over whatever is stored and returns its new revision.</summary>
    private static async Task<string> SaveAgentApiKeySettingsAsync(IEndpointMetadataStore store, AgentApiKeySettings settings)
    {
        var current = await store.GetAgentApiKeySettings();
        settings.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetAgentApiKeySettings(settings, current.Revision));
        return settings.Revision;
    }

    private static AgentApiKeySettings SampleAgentApiKeySettings() => new()
    {
        UpdatedBy = "owner@example.com",
        UpdatedAtUtc = T0.AddHours(1),
        Keys =
        {
            new AgentApiKey
            {
                KeyId = "abcdefghijkl",
                AgentId = "enrichment-agent",
                Name = "Production enrichment",
                SecretHash = "q83vEjRWeJ1mN9u5Hl1YGqHk8Ywq4cG4x3V5D8pX1Ak=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                ExpiresAtUtc = T0.AddDays(90),
            },
            new AgentApiKey
            {
                KeyId = "mnopqrstuvwx",
                AgentId = "triage-agent",
                SecretHash = "3Yz2vT0pG8hJ6kL4mN1qR7sU9wX5aB2cD4eF6gH8iJ0=",
                CreatedAtUtc = T0,
                CreatedBy = "owner@example.com",
                RevokedAtUtc = T0.AddHours(1),
                RevokedBy = "owner@example.com",
            },
        },
    };
}
