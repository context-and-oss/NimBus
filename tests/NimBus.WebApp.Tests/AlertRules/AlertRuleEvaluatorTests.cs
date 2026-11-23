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
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.AlertRules;

namespace NimBus.WebApp.Tests.AlertRules;

/// <summary>Spec 053 §5.3–§5.5: the alert rule evaluator against the in-memory stores.</summary>
[TestClass]
public sealed class AlertRuleEvaluatorTests
{
    private const string Endpoint = "ErpEndpoint";

    private readonly InMemoryMessageStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingWebhooks _webhooks = new();

    private AlertRuleEvaluator Evaluator(string instance = "a") => new(
        _store,
        _store,
        new AlertRuleDelivery(null, null, _webhooks, "https://nimbus.example.com"),
        _time,
        NullLogger<AlertRuleEvaluator>.Instance,
        instance);

    private async Task<AlertRule> CreateRule(int threshold = 3, int resolveAfterMinutes = 5, bool enabled = true, string? errorText = null)
    {
        var rule = new AlertRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            EndpointId = Endpoint,
            Name = "ERP backlog",
            Enabled = enabled,
            Condition = new AlertRuleCondition { Threshold = threshold, ErrorTextContains = errorText },
            ResolveAfterMinutes = resolveAfterMinutes,
            Targets = [new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Webhook, Url = "https://hooks.example.com/a" }],
            CreatedBy = "owner",
            CreatedAtUtc = _time.GetUtcNow().UtcDateTime,
            UpdatedAtUtc = _time.GetUtcNow().UtcDateTime,
        };
        return (await _store.TrySaveAlertRuleDefinition(rule, expectedVersion: null))!;
    }

    private async Task Fail(string eventId, string errorText = "SqlException: Timeout expired")
    {
        var failed = Event(eventId);
        failed.MessageContent = new MessageContent { ErrorContent = new ErrorContent { ErrorText = errorText } };
        await _store.UploadFailedMessage(eventId, "s-" + eventId, Endpoint, failed);
    }

    private Task Clear(string eventId) => _store.UploadCompletedMessage(eventId, "s-" + eventId, Endpoint, Event(eventId));

    private UnresolvedEvent Event(string eventId) => new()
    {
        EventId = eventId,
        SessionId = "s-" + eventId,
        EndpointId = Endpoint,
        EventTypeId = "OrderPlaced",
        EnqueuedTimeUtc = _time.GetUtcNow().UtcDateTime,
        UpdatedAt = _time.GetUtcNow().UtcDateTime,
        LastMessageId = "m-" + eventId,
        MessageContent = new MessageContent(),
    };

    private async Task<AlertRuleTickResult> Tick(AlertRuleEvaluator? evaluator = null)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        return await (evaluator ?? Evaluator()).RunTickAsync(CancellationToken.None);
    }

    private async Task<AlertRule> Reload(AlertRule rule) => (await _store.GetAlertRule(Endpoint, rule.RuleId))!;

    [TestMethod]
    public async Task A_rule_fires_at_its_threshold_and_not_below()
    {
        var rule = await CreateRule(threshold: 3);
        await Fail("e1");
        await Fail("e2");

        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);
        Assert.AreEqual(0, _webhooks.Sent.Count);

        await Fail("e3");
        await Tick();

        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);
        Assert.AreEqual(3, firing.State.LastValue);
        var (notice, target) = _webhooks.Sent.Single();
        Assert.AreEqual("t1", target.TargetId);
        Assert.AreEqual(AlertRuleStatus.Firing, notice.Status);
        Assert.AreEqual(3, notice.Value);
        Assert.AreEqual($"{rule.RuleId}:1:t1", notice.NoticeId);
        StringAssert.StartsWith(notice.WebUiUrl, "https://nimbus.example.com/Failed?endpointId=ErpEndpoint");
        Assert.AreEqual(0, firing.State.PendingNotices.Count, "A delivered notice is removed.");
    }

    [TestMethod]
    public async Task The_backlog_is_read_up_to_the_threshold_and_reported_as_at_least()
    {
        var rule = await CreateRule(threshold: 2);
        for (var i = 0; i < 5; i++)
            await Fail("e" + i);

        await Tick();

        Assert.AreEqual(2, (await Reload(rule)).State.LastValue);
    }

    [TestMethod]
    public async Task The_error_text_filter_narrows_the_backlog()
    {
        var rule = await CreateRule(threshold: 1, errorText: "deadlock");
        await Fail("e1", "SqlException: Timeout expired");

        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);

        await Fail("e2", "Transaction was deadlocked");
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
    }

    [TestMethod]
    public async Task A_quiet_rule_is_not_written_on_every_tick()
    {
        var rule = await CreateRule(threshold: 3);
        await Tick();
        var afterFirst = (await Reload(rule)).Version;

        await Tick();
        await Tick();

        Assert.AreEqual(afterFirst, (await Reload(rule)).Version, "Nothing changed, so nothing was written.");
    }

    [TestMethod]
    public async Task A_rule_resolves_only_after_its_resolve_time_of_continuous_false()
    {
        var rule = await CreateRule(threshold: 1, resolveAfterMinutes: 3);
        await Fail("e1");
        await Tick();
        await Clear("e1");

        await Tick();
        var falseSince = (await Reload(rule)).State.ConditionFalseSinceUtc;
        Assert.IsNotNull(falseSince);
        await Tick();
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status, "Two minutes of false are not three.");

        await Tick();

        var resolved = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Ok, resolved.State.Status);
        var notice = _webhooks.Sent.Last().Notice;
        Assert.AreEqual(AlertRuleStatus.Ok, notice.Status);
        Assert.AreEqual(AlertRuleResolveReasons.Cleared, notice.Reason);
        Assert.AreEqual($"{rule.RuleId}:2:t1", notice.NoticeId);
        Assert.AreEqual(2, resolved.State.Transitions.Count);
    }

    [TestMethod]
    public async Task A_true_evaluation_during_the_resolve_time_restarts_it()
    {
        var rule = await CreateRule(threshold: 1, resolveAfterMinutes: 2);
        await Fail("e1");
        await Tick();
        await Clear("e1");
        await Tick();
        await Fail("e2");
        await Tick();
        Assert.IsNull((await Reload(rule)).State.ConditionFalseSinceUtc);
        await Clear("e2");

        await Tick();
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
        await Tick();
        Assert.AreEqual(AlertRuleStatus.Ok, (await Reload(rule)).State.Status);
    }

    [TestMethod]
    public async Task A_gap_in_evaluation_restarts_the_resolve_time()
    {
        var rule = await CreateRule(threshold: 1, resolveAfterMinutes: 3);
        await Fail("e1");
        await Tick();
        await Clear("e1");
        await Tick();
        var falseSince = (await Reload(rule)).State.ConditionFalseSinceUtc;

        // The evaluator does not run for an hour.
        _time.Advance(TimeSpan.FromHours(1));
        await Tick();

        var state = (await Reload(rule)).State;
        Assert.AreEqual(AlertRuleStatus.Firing, state.Status, "An hour without evaluations does not prove the backlog stayed low.");
        Assert.IsTrue(state.ConditionFalseSinceUtc > falseSince);
    }

    [TestMethod]
    public async Task Scaled_out_evaluators_share_one_tick()
    {
        var rule = await CreateRule(threshold: 1);
        await Fail("e1");
        _time.Advance(TimeSpan.FromMinutes(1));

        var results = await Task.WhenAll(Evaluator("a").RunTickAsync(CancellationToken.None), Evaluator("b").RunTickAsync(CancellationToken.None));

        Assert.AreEqual(1, results.Count(r => r.Claimed));
        Assert.AreEqual(1, _webhooks.Sent.Count, "One opened notice, however many instances run.");
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
    }

    [TestMethod]
    public async Task A_failed_delivery_is_retried_with_the_same_notice_id()
    {
        var rule = await CreateRule(threshold: 1);
        await Fail("e1");
        _webhooks.Fail = true;

        await Tick();
        var pending = (await Reload(rule)).State.PendingNotices.Single();
        Assert.AreEqual(1, pending.Attempts);
        Assert.IsNotNull(pending.LastError);

        _webhooks.Fail = false;
        await Tick();

        Assert.AreEqual(pending.NoticeId, _webhooks.Sent.Single().Notice.NoticeId);
        Assert.AreEqual(0, (await Reload(rule)).State.PendingNotices.Count);
    }

    [TestMethod]
    public async Task The_opened_notice_is_saved_before_it_is_sent()
    {
        var rule = await CreateRule(threshold: 1);
        await Fail("e1");
        _webhooks.Fail = true;

        await Tick();

        var state = (await Reload(rule)).State;
        Assert.AreEqual(AlertRuleStatus.Firing, state.Status);
        Assert.AreEqual(1, state.PendingNotices.Count, "The transition and its notice were saved together.");
    }

    [TestMethod]
    public async Task An_undelivered_notice_is_dropped_after_24_hours()
    {
        var rule = await CreateRule(threshold: 1);
        await Fail("e1");
        _webhooks.Fail = true;
        await Tick();

        _time.Advance(TimeSpan.FromHours(24));
        await Tick();

        Assert.AreEqual(0, (await Reload(rule)).State.PendingNotices.Count);
        Assert.AreEqual(0, _webhooks.Sent.Count);
    }

    [TestMethod]
    public async Task A_disabled_rule_is_not_evaluated_but_its_pending_notices_are_delivered()
    {
        var rule = await CreateRule(threshold: 1);
        await Fail("e1");
        _webhooks.Fail = true;
        await Tick();

        var current = await Reload(rule);
        current.Enabled = false;
        await _store.TrySaveAlertRuleDefinition(current, current.Version);
        await Fail("e2");
        _webhooks.Fail = false;
        await Tick();

        var disabled = await Reload(rule);
        Assert.AreEqual(1, _webhooks.Sent.Count, "The pending opened notice went out.");
        Assert.AreEqual(1, disabled.State.LastValue, "The disabled rule was not read again.");
    }

    [TestMethod]
    public async Task The_tick_is_recorded_as_completed()
    {
        await Tick();

        var status = await _store.GetAlertRuleEvaluatorStatus();
        Assert.AreEqual(_time.GetUtcNow().UtcDateTime, status.LastCompletedAtUtc);
    }

    private sealed class RecordingWebhooks : IAlertRuleWebhookSender
    {
        public List<(AlertRuleNotice Notice, AlertRuleTarget Target)> Sent { get; } = [];

        public bool Fail { get; set; }

        public bool CanSendTo(string? url) => true;

        public Task SendRuleNoticeAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken = default)
        {
            if (Fail)
                throw new AlertWebhookDeliveryException("The webhook at hooks.example.com answered 503.");
            lock (Sent)
                Sent.Add((notice, target));
            return Task.CompletedTask;
        }
    }
}
