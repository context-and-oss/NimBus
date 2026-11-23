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
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services.AlertRules;

namespace NimBus.WebApp.Tests.AlertRules;

/// <summary>Spec 053 Phase 3: classification categories on alert rules, and the rule preview.</summary>
[TestClass]
public sealed class AlertRuleCategoryAndPreviewTests
{
    private const string Endpoint = "ErpEndpoint";

    private readonly InMemoryMessageStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeClassifications _classifications = new();
    private readonly ScriptedSignals _signals = new();

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private AlertRuleEvaluator Evaluator(IAlertRuleClassifications? classifications) => new(
        _store,
        _store,
        new AlertRuleDelivery(null, null, new NoWebhooks(), "https://nimbus.example.com"),
        _time,
        NullLogger<AlertRuleEvaluator>.Instance,
        "a",
        _signals,
        classifications);

    private async Task<AlertRule> CreateRule(AlertRuleCondition condition)
    {
        var rule = new AlertRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            EndpointId = Endpoint,
            Name = "classified",
            Enabled = true,
            Condition = condition,
            ResolveAfterMinutes = 1,
            Targets = [new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Webhook, Url = "https://hooks.example.com/a" }],
            CreatedBy = "owner",
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        };
        return (await _store.TrySaveAlertRuleDefinition(rule, expectedVersion: null))!;
    }

    private async Task Tick(IAlertRuleClassifications? classifications)
    {
        _time.Advance(TimeSpan.FromMinutes(1));
        await Evaluator(classifications).RunTickAsync(CancellationToken.None);
    }

    private async Task<AlertRule> Reload(AlertRule rule) => (await _store.GetAlertRule(Endpoint, rule.RuleId))!;

    private async Task Fail(string eventId)
    {
        var failed = new UnresolvedEvent
        {
            EventId = eventId,
            SessionId = "s-" + eventId,
            EndpointId = Endpoint,
            EventTypeId = "OrderPlaced",
            EnqueuedTimeUtc = Now,
            UpdatedAt = Now,
            LastMessageId = "m-" + eventId,
            MessageContent = new MessageContent { ErrorContent = new ErrorContent { ErrorText = "boom" } },
        };
        await _store.UploadFailedMessage(eventId, failed.SessionId, Endpoint, failed);
    }

    // ───────── categories ─────────

    [TestMethod]
    public async Task A_backlog_rule_with_categories_counts_only_failures_classified_into_them()
    {
        var rule = await CreateRule(new AlertRuleCondition { Threshold = 2, Categories = ["transient_dependency"] });
        await Fail("e1");
        await Fail("e2");
        await Fail("e3");
        await Fail("e4");
        _classifications.Classify("m-e1", Endpoint, "e1", "transient_dependency");
        _classifications.Classify("m-e2", Endpoint, "e2", "contract_schema");
        _classifications.Classify("m-e3", "OtherEndpoint", "e3", "transient_dependency");      // names another source
        _classifications.Classify("m-e4", Endpoint, "e4", "transient_dependency", FailureGuidance.Uncertain);

        await Tick(_classifications);
        var state = (await Reload(rule)).State;
        Assert.AreEqual(AlertRuleStatus.Ok, state.Status);
        Assert.AreEqual(1, state.LastValue, "Only e1 is classified into the category, by a result for the same source.");

        await Fail("e5");
        _classifications.Classify("m-e5", Endpoint, "e5", "transient_dependency");
        await Tick(_classifications);

        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
    }

    [TestMethod]
    public async Task A_rule_with_categories_is_left_unchanged_when_classifications_cannot_be_read()
    {
        var rule = await CreateRule(new AlertRuleCondition { Threshold = 1, Categories = ["transient_dependency"] });
        await Fail("e1");
        _classifications.Classify("m-e1", Endpoint, "e1", "transient_dependency");
        await Tick(_classifications);
        var firing = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, firing.State.Status);

        _classifications.Status = ClassificationBulkReadStatus.Unavailable;
        await Tick(_classifications);
        await Tick(_classifications);
        await Tick(classifications: null);
        await Tick(classifications: null);

        var after = await Reload(rule);
        Assert.AreEqual(AlertRuleStatus.Firing, after.State.Status, "A 0 would have resolved a rule that may still hold.");
        Assert.AreEqual(firing.Version, after.Version, "Nothing was written.");
    }

    [TestMethod]
    public async Task A_backlog_rule_with_categories_stops_at_the_scan_ceiling_without_resolving()
    {
        var rule = await CreateRule(new AlertRuleCondition { Threshold = 1, Categories = ["transient_dependency"] });
        await Fail("first");
        _classifications.Classify("m-first", Endpoint, "first", "transient_dependency");
        await Tick(_classifications);
        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);

        // The classified failure is resolved; more unclassified failures than the ceiling remain.
        await _store.UploadCompletedMessage("first", "s-first", Endpoint, new UnresolvedEvent { EventId = "first", SessionId = "s-first", EndpointId = Endpoint, UpdatedAt = Now, EnqueuedTimeUtc = Now, MessageContent = new MessageContent() });
        for (var i = 0; i < AlertRuleConditionReader.CategoryScanCeiling + 1; i++)
            await Fail("u" + i);
        await Tick(_classifications);
        await Tick(_classifications);

        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status, "An unread remainder may hold a match.");
        Assert.IsTrue(_classifications.Requested <= AlertRuleConditionReader.CategoryScanCeiling * 3, "Each reading looks at no more than the ceiling.");
    }

    [TestMethod]
    public async Task An_occurrences_rule_with_categories_classifies_the_attempts_in_its_window()
    {
        var rule = await CreateRule(new AlertRuleCondition
        {
            Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 2, WindowMinutes = 15, Categories = ["contract_schema"],
        });
        _signals.Attempts = [new FailedAttempt("a1", "e1", Now), new FailedAttempt("a2", "e1", Now), new FailedAttempt("a3", "e2", Now)];
        _classifications.Classify("a1", Endpoint, "e1", "contract_schema");
        _classifications.Classify("a3", Endpoint, "e2", "contract_schema");

        await Tick(_classifications);

        Assert.AreEqual(AlertRuleStatus.Firing, (await Reload(rule)).State.Status);
        Assert.AreEqual(AlertRuleConditionReader.CategoryScanCeiling, _signals.LastListLimit);
    }

    [TestMethod]
    public void The_shared_matcher_needs_a_live_same_source_classified_result_in_the_categories()
    {
        var result = FakeClassifications.Result("m1", Endpoint, "e1", "business_rule", FailureGuidance.ChangeLikelyRequired);
        var live = new ClassificationSnapshot("m1", result, "completed", null, Deleted: false);

        Assert.IsTrue(ClassificationMatch.Matches(live, "erpendpoint", "e1", ["business_rule"]), "The endpoint compares case-insensitively.");
        Assert.IsFalse(ClassificationMatch.Matches(live, Endpoint, "e1", ["contract_schema"]));
        Assert.IsFalse(ClassificationMatch.Matches(live, Endpoint, "e2", ["business_rule"]));
        Assert.IsFalse(ClassificationMatch.Matches(live with { Deleted = true }, Endpoint, "e1", ["business_rule"]));
        Assert.IsFalse(ClassificationMatch.Matches(null, Endpoint, "e1", ["business_rule"]));
        Assert.IsFalse(ClassificationMatch.Matches(live with { Latest = result with { Category = "unknown" } }, Endpoint, "e1", ["unknown"]));
    }

    // ───────── preview ─────────

    private AlertRulePreviewer Previewer(IAlertSignalStore? signals = null)
        => new(new AlertRuleConditionReader(_store, signals ?? _signals, _classifications));

    [TestMethod]
    public async Task An_occurrences_preview_replays_when_the_rule_would_have_fired()
    {
        // Two failures at 03:10 and one at 03:12, window 5, threshold 2, resolve after 3 minutes.
        var end = Now;
        _signals.MinuteCounts = [new MinuteCount(end.Date.AddHours(3).AddMinutes(10), 2), new MinuteCount(end.Date.AddHours(3).AddMinutes(12), 1)];
        var condition = new AlertRuleCondition { Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 2, WindowMinutes = 5 };

        var preview = await Previewer().PreviewAsync(Endpoint, condition, resolveAfterMinutes: 3, Now, CancellationToken.None);

        var replay = preview.Replay!;
        Assert.AreEqual(end.AddDays(-1), replay.FromUtc);
        Assert.AreEqual(end, replay.ToUtc);
        var (type, from, to) = _signals.LastMinuteRead!.Value;
        Assert.AreEqual(MessageType.ErrorResponse, type);
        Assert.AreEqual(end.AddDays(-1).AddMinutes(-5), from, "The first replayed minute needs the window before it.");
        Assert.AreEqual(end, to);

        // The window [t-5, t) holds 2 or more from 03:11 (it sees 03:10) through 03:15; at 03:16 it
        // sees only 03:12's one failure, and the rule resolves after 3 minutes of false: at 03:19.
        var period = replay.Periods.Single();
        Assert.AreEqual(end.Date.AddHours(3).AddMinutes(11), period.FromUtc);
        Assert.AreEqual(end.Date.AddHours(3).AddMinutes(19), period.ToUtc);
        Assert.IsTrue(replay.Series.Count <= AlertRulePreviewer.MaxChartPoints);
        Assert.AreEqual(3, replay.Series.Max(p => p.Value), "The series shows the uncapped count.");
    }

    [TestMethod]
    public async Task A_silence_preview_fires_after_the_minutes_without_a_completion()
    {
        var end = Now;
        // Completions every minute except from 05:00 to 05:59.
        var counts = Enumerable.Range(0, 1440 + 30)
            .Select(i => end.AddMinutes(-1440 - 30 + i))
            .Where(m => m.Hour != 5)
            .Select(m => new MinuteCount(m, 1))
            .ToList();
        _signals.MinuteCounts = counts;
        var condition = new AlertRuleCondition { Kind = AlertRuleConditionKinds.Silence, Minutes = 30 };

        var preview = await Previewer().PreviewAsync(Endpoint, condition, resolveAfterMinutes: 1, Now, CancellationToken.None);

        Assert.AreEqual(MessageType.ResolutionResponse, _signals.LastMinuteRead!.Value.Type);
        var period = preview.Replay!.Periods.Single();
        Assert.AreEqual(end.Date.AddHours(5).AddMinutes(30), period.FromUtc, "No completion in [t-30, t) first at 05:30.");
        Assert.AreEqual(end.Date.AddHours(6).AddMinutes(2), period.ToUtc, "06:00's completion clears it at 06:01; resolved a minute later.");
    }

    [TestMethod]
    [DataRow("failure_backlog", "no_history")]
    [DataRow("blocked_session", "no_history")]
    public async Task Backlog_and_blocked_previews_show_the_value_now_without_a_replay(string kind, string reason)
    {
        await Fail("e1");
        var condition = kind == AlertRuleConditionKinds.FailureBacklog
            ? new AlertRuleCondition { Threshold = 1 }
            : new AlertRuleCondition { Kind = kind, DeferredThreshold = 5 };

        var preview = await Previewer().PreviewAsync(Endpoint, condition, 5, Now, CancellationToken.None);

        Assert.IsNull(preview.Replay);
        Assert.AreEqual(reason, preview.ReplayUnsupportedReason);
        Assert.IsNotNull(preview.HoldsNow);
        if (kind == AlertRuleConditionKinds.FailureBacklog)
            Assert.IsTrue(preview.HoldsNow.Value);
    }

    [TestMethod]
    public async Task A_preview_with_categories_has_no_replay()
    {
        var condition = new AlertRuleCondition { Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 1, WindowMinutes = 15, Categories = ["business_rule"] };

        var preview = await Previewer().PreviewAsync(Endpoint, condition, 5, Now, CancellationToken.None);

        Assert.AreEqual("categories", preview.ReplayUnsupportedReason);
        Assert.IsNull(_signals.LastMinuteRead);
    }

    [TestMethod]
    public async Task A_preview_records_no_transition_metrics_and_sends_nothing()
    {
        _signals.MinuteCounts = [new MinuteCount(Now.AddHours(-2), 5)];
        var condition = new AlertRuleCondition { Kind = AlertRuleConditionKinds.FailureOccurrences, Threshold = 1, WindowMinutes = 5 };
        var transitions = 0L;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AlertRuleTelemetry.Name && instrument.Name == "nimbus.alert_rules.transitions")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref transitions, value));
        listener.Start();

        var preview = await Previewer().PreviewAsync(Endpoint, condition, 1, Now, CancellationToken.None);

        Assert.AreEqual(1, preview.Replay!.Periods.Count);
        Assert.AreEqual(0, Interlocked.Read(ref transitions));
    }

    // ───────── fakes ─────────

    private sealed class FakeClassifications : IAlertRuleClassifications
    {
        private readonly Dictionary<string, ClassificationSnapshot> _snapshots = new(StringComparer.Ordinal);

        public ClassificationBulkReadStatus Status { get; set; } = ClassificationBulkReadStatus.Available;

        public int Requested { get; private set; }

        public static FailureClassification Result(string messageId, string endpointId, string eventId, string category, FailureGuidance guidance) => new()
        {
            Id = messageId + ":1",
            FailureMessageId = messageId,
            Revision = 1,
            EventId = eventId,
            EventTypeId = "OrderPlaced",
            EndpointId = endpointId,
            Provider = "agent",
            Model = "test",
            QuestionSetVersion = 2,
            Category = category,
            CategoryConfidence = 0.9,
            CategoryProbabilities = new Dictionary<string, double>(),
            RetryLikelihood = 0.5,
            ChangeRequiredLikelihood = 0.5,
            ExternalDependencyLikelihood = 0.5,
            Guidance = guidance,
            EventPayloadIncluded = false,
            RequestedBy = "agent",
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
        };

        public void Classify(string messageId, string endpointId, string eventId, string category, FailureGuidance guidance = FailureGuidance.RetryMayHelp)
            => _snapshots[messageId] = new ClassificationSnapshot(messageId, Result(messageId, endpointId, eventId, category, guidance), "completed", null, Deleted: false);

        public Task<ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken)
        {
            Requested += failureMessageIds.Count;
            if (Status != ClassificationBulkReadStatus.Available)
                return Task.FromResult(new ClassificationBulkReadResult(Status, new Dictionary<string, ClassificationSnapshot>()));
            var found = failureMessageIds.Where(_snapshots.ContainsKey).ToDictionary(id => id, id => _snapshots[id], StringComparer.Ordinal);
            return Task.FromResult(new ClassificationBulkReadResult(ClassificationBulkReadStatus.Available, found));
        }
    }

    private sealed class ScriptedSignals : IAlertSignalStore
    {
        public IReadOnlyList<FailedAttempt> Attempts { get; set; } = [];

        public IReadOnlyList<MinuteCount> MinuteCounts { get; set; } = [];

        public int? LastListLimit { get; private set; }

        public (MessageType Type, DateTime From, DateTime To)? LastMinuteRead { get; private set; }

        public Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap)
            => Task.FromResult(Math.Min(Attempts.Count, cap));

        public Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId) => Task.FromResult<BlockingEvent?>(null);

        public Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc) => Task.FromResult(true);

        public Task<IReadOnlyList<MinuteCount>> CountMessagesPerMinute(string endpointId, MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
        {
            LastMinuteRead = (type, fromUtc, toUtc);
            return Task.FromResult<IReadOnlyList<MinuteCount>>(MinuteCounts.Where(c => c.MinuteUtc >= fromUtc && c.MinuteUtc < toUtc).ToList());
        }

        public Task<FailedAttemptPage> ListFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int limit)
        {
            LastListLimit = limit;
            return Task.FromResult(new FailedAttemptPage(Attempts.Take(limit).ToList(), Attempts.Count > limit));
        }
    }

    private sealed class NoWebhooks : IAlertRuleWebhookSender
    {
        public bool CanSendTo(string? url) => true;

        public Task SendRuleNoticeAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
