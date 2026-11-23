#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Alerts;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.AlertRules;

namespace NimBus.WebApp.Tests.AlertRules;

/// <summary>
/// Spec 053 Phase 2: the failure-occurrences, blocked-session and silence conditions, read
/// through a scripted <see cref="IAlertSignalStore"/> (the stores themselves have a conformance suite).
/// </summary>
[TestClass]
public sealed class AlertRuleSignalConditionTests
{
    private const string Endpoint = "ErpEndpoint";

    private readonly CountingStore _store = new();
    private readonly ScriptedSignals _signals = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly List<AlertRuleNotice> _sent = [];

    private AlertRuleEvaluator Evaluator(IAlertSignalStore? signals) => new(
        _store,
        _store,
        new AlertRuleDelivery(null, null, new RecordingWebhooks(_sent), "https://nimbus.example.com"),
        _time,
        NullLogger<AlertRuleEvaluator>.Instance,
        "a",
        signals);

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private async Task<AlertRule> CreateRule(AlertRuleCondition condition, int resolveAfterMinutes = 5)
    {
        var rule = new AlertRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            EndpointId = Endpoint,
            Name = "ERP " + condition.Kind,
            Enabled = true,
            Condition = condition,
            ResolveAfterMinutes = resolveAfterMinutes,
            Targets = [new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Webhook, Url = "https://hooks.example.com/a" }],
            CreatedBy = "owner",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        return (await _store.TrySaveAlertRuleDefinition(rule, expectedVersion: null))!;
    }

    private async Task Tick(IAlertSignalStore? signals = null, bool useSignals = true)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        await Evaluator(useSignals ? signals ?? _signals : null).RunTickAsync(CancellationToken.None);
    }

    private async Task<AlertRule> Reload(AlertRule rule) => (await _store.GetAlertRule(Endpoint, rule.RuleId))!;

    [TestMethod]
    public async Task Failure_occurrences_count_in_the_window_ending_now()
    {
        var rule = await CreateRule(new AlertRuleCondition
        {
            Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 2, WindowMinutes = 15, EventTypes = ["OrderPlaced"], ErrorTextContains = "timeout",
        });
        _signals.FailedAttempts = 1;

        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);

        _signals.FailedAttempts = 5;
        await Tick();

        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);
        Assert.AreEqual(2, firing.State.LastValue, "The count is capped at the threshold.");
        var call = _signals.CountCalls.Last();
        Assert.AreEqual(Endpoint, call.EndpointId);
        Assert.AreEqual(Now - TimeSpan.FromMinutes(15), call.FromUtc);
        Assert.AreEqual(Now, call.ToUtc);
        CollectionAssert.AreEqual(new[] { "OrderPlaced" }, call.EventTypes!.ToArray());
        Assert.AreEqual("timeout", call.ErrorText);
        Assert.AreEqual(2, call.Cap);
        var notice = _sent.Single();
        StringAssert.StartsWith(notice.WebUiUrl, "https://nimbus.example.com/Failed?endpointId=ErpEndpoint&period=1h");
    }

    [TestMethod]
    public async Task Failure_occurrences_resolve_once_they_age_out_of_the_window()
    {
        var rule = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 1, WindowMinutes = 5 }, resolveAfterMinutes: 2);
        _signals.FailedAttempts = 1;
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);

        _signals.FailedAttempts = 0;
        await Tick();
        await Tick();
        await Tick();

        var resolved = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Ok, resolved.State.Status);
        Assert.AreEqual(AlertRuleResolveReasons.Cleared, resolved.State.Transitions[0].Reason);
    }

    [TestMethod]
    public async Task A_session_blocked_longer_than_the_minutes_fires_and_reports_how_long()
    {
        var rule = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, Minutes = 30 });
        _signals.OldestBlocking = new BlockingEvent("e1", "s1", ResolutionStatus.Failed, Now.AddMinutes(-20));

        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);

        _time.Advance(TimeSpan.FromMinutes(9));
        await Tick();

        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);
        Assert.AreEqual(31, firing.State.LastValue);
        Assert.AreEqual(31, _sent.Single().Value);
        Assert.AreEqual(
            "https://nimbus.example.com/Endpoints/Details/ErpEndpoint?status=Failed&status=Pending&status=Deferred",
            _sent.Single().WebUiUrl);
    }

    [TestMethod]
    public async Task A_blocked_rule_is_not_written_every_minute_while_the_age_grows()
    {
        var rule = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, Minutes = 30 });
        _signals.OldestBlocking = new BlockingEvent("e1", "s1", ResolutionStatus.Failed, Now.AddMinutes(-40));
        await Tick();
        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);

        await Tick();
        await Tick();
        await Tick();

        Assert.AreEqual(firing.Version, (await Reload(rule)).Version, "Only the age changed, so nothing was written.");

        var quiet = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, Minutes = 600 });
        await Tick();
        var afterFirst = (await Reload(quiet)).Version;
        await Tick();
        await Tick();
        Assert.AreEqual(afterFirst, (await Reload(quiet)).Version, "A rule below its minutes is not written while the age grows.");
    }

    [TestMethod]
    public async Task Enough_deferred_events_fire_a_blocked_session_rule_and_draining_them_resolves_it()
    {
        var rule = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, DeferredThreshold = 2 }, resolveAfterMinutes: 1);
        await Defer("d1");
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);

        await Defer("d2");
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
        Assert.AreEqual(0, _signals.BlockingCalls, "Without minutes the oldest blocking event is not read.");

        await _store.UploadCompletedMessage("d1", "s1", Endpoint, Event("d1"));
        await Tick();
        await Tick();

        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);
        StringAssert.Contains(_sent.Last().Condition.Kind, AlertRuleConditionKinds.BlockedSession);
    }

    [TestMethod]
    public async Task Silence_fires_when_nothing_completed_for_the_minutes_and_resolves_when_messages_complete()
    {
        var rule = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.Silence, Minutes = 60 }, resolveAfterMinutes: 1);
        _signals.CompletedSince = _ => true;
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);
        Assert.AreEqual(Now - TimeSpan.FromMinutes(60), _signals.CompletedSinceCalls.Last());

        _signals.CompletedSince = _ => false;
        await Tick();
        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);
        Assert.AreEqual(60, _sent.Single().Value);
        Assert.AreEqual("https://nimbus.example.com/Endpoints/Details/ErpEndpoint", _sent.Single().WebUiUrl);

        _signals.CompletedSince = _ => true;
        await Tick();
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);
    }

    [TestMethod]
    public async Task A_rule_whose_kind_the_store_cannot_read_is_skipped_while_backlog_rules_still_run()
    {
        var silence = await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.Silence, Minutes = 60 });
        var backlog = await CreateRule(new AlertRuleCondition { Threshold = 1 });

        await Tick(useSignals: false);

        Assert.IsNull((await Reload(silence)).State.SinceUtc, "The silence rule was not evaluated.");
        Assert.IsNotNull((await Reload(backlog)).State.SinceUtc, "The backlog rule was.");
    }

    [TestMethod]
    public async Task Blocked_session_rules_on_one_endpoint_share_the_deferred_count_in_a_tick()
    {
        await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, DeferredThreshold = 2 });
        await CreateRule(new AlertRuleCondition { Kind = AlertRuleConditionKinds.BlockedSession, DeferredThreshold = 5 });

        await Tick();
        Assert.AreEqual(1, _store.StateCountReads);

        await Tick();
        Assert.AreEqual(2, _store.StateCountReads, "Each tick reads it again.");
    }

    private Task Defer(string eventId) => _store.UploadDeferredMessage(eventId, "s1", Endpoint, Event(eventId));

    private UnresolvedEvent Event(string eventId) => new()
    {
        EventId = eventId,
        SessionId = "s1",
        EndpointId = Endpoint,
        EventTypeId = "OrderPlaced",
        EnqueuedTimeUtc = Now,
        UpdatedAt = Now,
        LastMessageId = "m-" + eventId,
        MessageContent = new MessageContent(),
    };

    private sealed class CountingStore : InMemoryMessageStore
    {
        public int StateCountReads { get; private set; }

        public override Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
        {
            StateCountReads++;
            return base.DownloadEndpointStateCount(endpointId);
        }
    }

    private sealed class ScriptedSignals : IAlertSignalStore
    {
        public int FailedAttempts { get; set; }

        public BlockingEvent? OldestBlocking { get; set; }

        public Func<DateTime, bool> CompletedSince { get; set; } = _ => true;

        public List<(string EndpointId, DateTime FromUtc, DateTime ToUtc, IReadOnlyCollection<string>? EventTypes, string? ErrorText, int Cap)> CountCalls { get; } = [];

        public List<DateTime> CompletedSinceCalls { get; } = [];

        public int BlockingCalls { get; private set; }

        public Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap)
        {
            CountCalls.Add((endpointId, fromUtc, toUtc, eventTypes, errorTextContains, cap));
            return Task.FromResult(Math.Min(FailedAttempts, cap));
        }

        public Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId)
        {
            BlockingCalls++;
            return Task.FromResult(OldestBlocking);
        }

        public Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc)
        {
            CompletedSinceCalls.Add(sinceUtc);
            return Task.FromResult(CompletedSince(sinceUtc));
        }
    }

    private sealed class RecordingWebhooks(List<AlertRuleNotice> sent) : IAlertRuleWebhookSender
    {
        public bool CanSendTo(string? url) => true;

        public Task SendRuleNoticeAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken = default)
        {
            lock (sent)
                sent.Add(notice);
            return Task.CompletedTask;
        }
    }
}
