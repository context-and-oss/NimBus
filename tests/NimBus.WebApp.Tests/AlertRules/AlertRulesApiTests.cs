#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Alerts;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AlertRules;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests.AlertRules;

/// <summary>Spec 053 §5.8: the alert rules API.</summary>
[TestClass]
public sealed class AlertRulesApiTests
{
    private const string Endpoint = "ErpEndpoint";
    private const string OtherEndpoint = "CrmEndpoint";
    private const string SigningKey = "0123456789abcdef0123456789abcdef";

    private readonly InMemoryMessageStore _store = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 9, 0, 0, TimeSpan.Zero));
    private readonly RecordingWebhooks _webhooks = new();
    private readonly RecordingAudit _audit = new();

    private AlertRulesImplementation Sut(bool owner = true, bool reader = true, bool mail = false, IAlertRuleStore? store = null, Func<string, bool>? canRead = null, bool signals = true, bool classifications = false)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AlertWebhookOptions.SectionName + ":SigningKey"] = SigningKey })
            .Build();
        var delivery = new AlertRuleDelivery(
            mail ? new RecordingMail() : null,
            mail ? new AlertMailComposer(new AlertMailOptions()) : null,
            _webhooks,
            "https://nimbus.example.com");
        return new AlertRulesImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FixedAuthorization(owner, reader, canRead),
            _audit,
            new FakePlatform(Endpoint, OtherEndpoint),
            delivery,
            configuration,
            _time,
            store ?? _store,
            signals ? new NoSignals() : null,
            _store,
            classifications ? new NoClassifications() : null);
    }

    private static Api.AlertRuleRequest Request(int threshold = 3, bool enabled = true, string? errorText = null, params Api.AlertRuleTargetModel[] targets) => new()
    {
        Name = "ERP backlog",
        Enabled = enabled,
        Condition = new Api.AlertRuleCondition { Threshold = threshold, ErrorTextContains = errorText, EventTypes = [], Statuses = [] },
        ResolveAfterMinutes = 5,
        Targets = targets.Length > 0 ? targets.ToList() : [Webhook("https://hooks.example.com/a")],
    };

    private static Api.AlertRuleTargetModel Webhook(string url, string? id = null) => new() { Id = id, Type = "webhook", Url = url };

    private static Api.AlertRuleTargetModel Mail(string address) => new() { Type = "mail", Address = address };

    private static T Ok<T>(ActionResult<T> result)
    {
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200, got {result.Result?.GetType().Name}: {(result.Result as ObjectResult)?.Value}");
        return (T)ok.Value!;
    }

    private static List<string> Errors<T>(ActionResult<T> result, int status = 400)
    {
        var problem = result.Result as ObjectResult;
        Assert.IsNotNull(problem);
        Assert.AreEqual(status, problem.StatusCode);
        return ((Api.ProblemDetails)problem.Value!).Errors.ToList();
    }

    private async Task<AlertRule> MakeFiring(string ruleId)
    {
        var rule = (await _store.GetAlertRule(Endpoint, ruleId))!;
        var state = rule.State.Clone();
        AlertRuleEvaluator.Transition(rule, state, AlertRuleStatus.Firing, null, 3, _time.GetUtcNow().UtcDateTime);
        state.PendingNotices.Clear();
        await _store.TrySaveAlertRuleState(Endpoint, ruleId, rule.Version, state);
        return (await _store.GetAlertRule(Endpoint, ruleId))!;
    }

    [TestMethod]
    public async Task Creating_a_rule_stores_it_and_returns_each_webhook_secret_once()
    {
        var saved = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));

        Assert.AreEqual("t1", saved.Rule.Targets.Single().Id);
        var secret = saved.Secrets.Single();
        Assert.AreEqual(EndpointAlertWebhook.DeriveRuleTargetSigningSecret(SigningKey, saved.Rule.Id, "t1"), secret.SigningSecret);
        var stored = await _store.GetAlertRule(Endpoint, saved.Rule.Id);
        Assert.IsNotNull(stored);
        Assert.AreEqual(AlertRuleConditionKinds.FailureBacklog, stored.Condition.Kind);
        Assert.AreEqual(MessageAuditType.AlertRuleCreated, _audit.Logged.Single().Type);
        Assert.IsFalse(_audit.Logged.Single().Data!.Contains(secret.SigningSecret, StringComparison.Ordinal), "The audit never holds a secret.");

        var listed = Ok(await Sut().GetEndpointAlertRulesAsync(Endpoint));
        Assert.AreEqual("https://hooks.example.com/a", listed.Rules.Single().Targets.Single().Url, "Owners see where notices go.");
        Assert.IsTrue(listed.CanManage);
        Assert.IsTrue(listed.WebhookEnabled);
    }

    [TestMethod]
    public async Task A_reader_sees_the_rule_and_its_state_but_not_where_notices_go()
    {
        Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));

        var listed = Ok(await Sut(owner: false).GetEndpointAlertRulesAsync(Endpoint));

        Assert.IsFalse(listed.CanManage);
        var target = listed.Rules.Single().Targets.Single();
        Assert.AreEqual("webhook", target.Type);
        Assert.IsNull(target.Url);
        Assert.IsNull(target.Address);
    }

    [TestMethod]
    public async Task Someone_who_cannot_read_the_endpoint_is_refused()
        => Assert.IsInstanceOfType<ForbidResult>((await Sut(owner: false, reader: false).GetEndpointAlertRulesAsync(Endpoint)).Result);

    [TestMethod]
    public async Task A_non_owner_cannot_create_a_rule_and_the_attempt_is_audited()
    {
        var result = await Sut(owner: false).PostEndpointAlertRuleAsync(Request(), Endpoint);

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(0, (await _store.ListAlertRulesByEndpoint(Endpoint)).Count);
        Assert.IsTrue(_audit.Logged.Single().AccessDenied);
    }

    [TestMethod]
    public async Task An_unknown_endpoint_is_not_found()
        => Assert.IsInstanceOfType<NotFoundObjectResult>((await Sut().PostEndpointAlertRuleAsync(Request(), "NoSuchEndpoint")).Result);

    [TestMethod]
    [DataRow(0, DisplayName = "threshold 0")]
    [DataRow(101, DisplayName = "threshold above 100")]
    public async Task The_threshold_must_be_1_to_100(int threshold)
        => CollectionAssert.Contains(Errors(await Sut().PostEndpointAlertRuleAsync(Request(threshold), Endpoint)), "condition.threshold must be 1 to 100.");

    [TestMethod]
    public async Task A_mail_target_is_refused_while_alert_mail_is_off()
    {
        var errors = Errors(await Sut(mail: false).PostEndpointAlertRuleAsync(Request(3, true, null, Mail("team@example.com")), Endpoint));

        Assert.IsTrue(errors.Any(e => e.Contains("NimBus:Alerts:Mail", StringComparison.Ordinal)), string.Join(" | ", errors));
    }

    [TestMethod]
    public async Task A_mail_target_is_accepted_when_alert_mail_is_on()
        => Ok(await Sut(mail: true).PostEndpointAlertRuleAsync(Request(3, true, null, Mail("team@example.com")), Endpoint));

    [TestMethod]
    public async Task An_endpoint_has_at_most_20_rules()
    {
        for (var i = 0; i < AlertRulesImplementation.MaxRulesPerEndpoint; i++)
            Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));

        Errors(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
    }

    [TestMethod]
    public async Task An_edit_keeps_existing_targets_and_returns_a_secret_only_for_a_new_one()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));

        var updated = Ok(await Sut().PutEndpointAlertRuleAsync(
            Request(3, true, null, Webhook("https://hooks.example.com/a", "t1"), Webhook("https://hooks.example.com/b")),
            Endpoint,
            created.Rule.Id));

        CollectionAssert.AreEqual(new[] { "t1", "t2" }, updated.Rule.Targets.Select(t => t.Id).ToArray());
        Assert.AreEqual("t2", updated.Secrets.Single().TargetId);
        Assert.AreEqual(MessageAuditType.AlertRuleUpdated, _audit.Logged.Last().Type);
    }

    [TestMethod]
    public async Task Changing_a_firing_rule_s_condition_resolves_it_with_a_notice()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
        await MakeFiring(created.Rule.Id);

        Ok(await Sut().PutEndpointAlertRuleAsync(Request(threshold: 10), Endpoint, created.Rule.Id));

        var stored = (await _store.GetAlertRule(Endpoint, created.Rule.Id))!;
        Assert.AreEqual(AlertRuleStatus.Ok, stored.State.Status);
        var notice = stored.State.PendingNotices.Single();
        Assert.AreEqual(AlertRuleResolveReasons.RuleChanged, notice.Reason);
        Assert.AreEqual(AlertRuleStatus.Ok, notice.Status);
    }

    [TestMethod]
    public async Task Disabling_a_firing_rule_resolves_it_with_the_reason_rule_disabled()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
        await MakeFiring(created.Rule.Id);

        Ok(await Sut().PutEndpointAlertRuleAsync(Request(enabled: false), Endpoint, created.Rule.Id));

        var stored = (await _store.GetAlertRule(Endpoint, created.Rule.Id))!;
        Assert.AreEqual(AlertRuleResolveReasons.RuleDisabled, stored.State.PendingNotices.Single().Reason);
    }

    [TestMethod]
    public async Task Renaming_a_firing_rule_keeps_it_firing()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
        await MakeFiring(created.Rule.Id);
        var request = Request();
        request.Name = "Renamed";

        Ok(await Sut().PutEndpointAlertRuleAsync(request, Endpoint, created.Rule.Id));

        Assert.AreEqual(AlertRuleStatus.Firing, (await _store.GetAlertRule(Endpoint, created.Rule.Id))!.State.Status);
    }

    [TestMethod]
    public async Task A_rule_id_from_another_endpoint_is_not_found()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));

        Assert.IsInstanceOfType<NotFoundObjectResult>((await Sut().PutEndpointAlertRuleAsync(Request(), OtherEndpoint, created.Rule.Id)).Result);
        Assert.IsInstanceOfType<NotFoundObjectResult>(await Sut().DeleteEndpointAlertRuleAsync(OtherEndpoint, created.Rule.Id));
        Assert.IsNotNull(await _store.GetAlertRule(Endpoint, created.Rule.Id));
    }

    [TestMethod]
    public async Task Deleting_a_firing_rule_tells_its_targets()
    {
        var created = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
        await MakeFiring(created.Rule.Id);

        Assert.IsInstanceOfType<NoContentResult>(await Sut().DeleteEndpointAlertRuleAsync(Endpoint, created.Rule.Id));

        Assert.IsNull(await _store.GetAlertRule(Endpoint, created.Rule.Id));
        var (notice, _) = _webhooks.Sent.Single();
        Assert.AreEqual(AlertRuleResolveReasons.RuleDeleted, notice.Reason);
        Assert.AreEqual(MessageAuditType.AlertRuleDeleted, _audit.Logged.Last().Type);
    }

    [TestMethod]
    public async Task The_firing_list_holds_only_endpoints_the_caller_can_read()
    {
        var erp = Ok(await Sut().PostEndpointAlertRuleAsync(Request(), Endpoint));
        await MakeFiring(erp.Rule.Id);

        var readable = Ok(await Sut(canRead: id => id == Endpoint).GetAlertRulesFiringAsync()).ToList();
        var hidden = Ok(await Sut(canRead: _ => false).GetAlertRulesFiringAsync()).ToList();

        CollectionAssert.AreEqual(new[] { Endpoint }, readable);
        Assert.AreEqual(0, hidden.Count);
    }

    [TestMethod]
    public async Task Without_a_rule_store_the_API_answers_501()
    {
        var sut = new AlertRulesImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FixedAuthorization(true, true, null),
            _audit,
            new FakePlatform(Endpoint),
            new AlertRuleDelivery(null, null, null, null),
            new ConfigurationBuilder().Build(),
            _time,
            store: null);

        Assert.AreEqual(501, ((ObjectResult)(await sut.GetEndpointAlertRulesAsync(Endpoint)).Result!).StatusCode);
        Assert.AreEqual(0, Ok(await sut.GetAlertRulesFiringAsync()).Count());
    }

    [TestMethod]
    public async Task Categories_need_Integration_Intelligence()
    {
        var condition = new Api.AlertRuleCondition { Threshold = 1, Categories = ["transient_dependency"] };

        StringAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(condition), Endpoint)).Single(),
            "condition.categories needs Integration Intelligence");
        var listed = Ok(await Sut().GetEndpointAlertRulesAsync(Endpoint));
        Assert.IsFalse(listed.ClassificationAvailable);
        Assert.AreEqual(0, listed.Categories.Count);
    }

    [TestMethod]
    public async Task Categories_are_stored_for_counting_kinds_and_must_be_known()
    {
        var saved = Ok(await Sut(classifications: true).PostEndpointAlertRuleAsync(
            KindRequest(new Api.AlertRuleCondition { Threshold = 1, Categories = ["transient_dependency", "transient_dependency", "contract_schema"] }), Endpoint));
        CollectionAssert.AreEqual(new[] { "transient_dependency", "contract_schema" }, (await _store.GetAlertRule(Endpoint, saved.Rule.Id))!.Condition.Categories);
        CollectionAssert.AreEqual(new[] { "transient_dependency", "contract_schema" }, saved.Rule.Condition.Categories.ToArray());

        StringAssert.StartsWith(
            Errors(await Sut(classifications: true).PostEndpointAlertRuleAsync(
                KindRequest(new Api.AlertRuleCondition { Threshold = 1, Categories = ["unknown"] }), Endpoint)).Single(),
            "condition.categories must be at most 8 of ");

        var silence = Ok(await Sut(classifications: true).PostEndpointAlertRuleAsync(
            KindRequest(new Api.AlertRuleCondition { Kind = "silence", Minutes = 60, Categories = ["business_rule"] }), Endpoint));
        Assert.AreEqual(0, (await _store.GetAlertRule(Endpoint, silence.Rule.Id))!.Condition.Categories.Count, "Categories apply to counting kinds only.");

        var listed = Ok(await Sut(classifications: true).GetEndpointAlertRulesAsync(Endpoint));
        Assert.IsTrue(listed.ClassificationAvailable);
        CollectionAssert.DoesNotContain(listed.Categories.ToList(), "unknown");
        CollectionAssert.Contains(listed.Categories.ToList(), "messaging_platform");
    }

    [TestMethod]
    public async Task A_reader_can_preview_a_draft_and_backlog_drafts_have_no_replay()
    {
        var occurrences = Ok(await Sut(owner: false).PostEndpointAlertRulePreviewAsync(
            new Api.AlertRulePreviewRequest { Condition = new Api.AlertRuleCondition { Kind = "failure_occurrences", Threshold = 2, WindowMinutes = 15 }, ResolveAfterMinutes = 5 },
            Endpoint));
        Assert.IsNotNull(occurrences.Replay);
        Assert.AreEqual(0, occurrences.Replay.Periods.Count);
        Assert.AreEqual(_time.GetUtcNow().UtcDateTime.AddDays(-1), occurrences.Replay.FromUtc);
        Assert.IsFalse(occurrences.HoldsNow);

        var backlog = Ok(await Sut(owner: false).PostEndpointAlertRulePreviewAsync(
            new Api.AlertRulePreviewRequest { Condition = new Api.AlertRuleCondition { Threshold = 1 }, ResolveAfterMinutes = 5 }, Endpoint));
        Assert.IsNull(backlog.Replay);
        Assert.AreEqual("no_history", backlog.ReplayUnsupportedReason);
        Assert.AreEqual(0, backlog.ValueNow);
        Assert.AreEqual(0, _store.ListAlertRulesByEndpoint(Endpoint).Result.Count, "A preview saves nothing.");
    }

    [TestMethod]
    public async Task A_preview_validates_the_draft_and_needs_read_access()
    {
        CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRulePreviewAsync(
                new Api.AlertRulePreviewRequest { Condition = new Api.AlertRuleCondition { Kind = "silence", Minutes = 2 }, ResolveAfterMinutes = 5 }, Endpoint)),
            "condition.minutes must be 5 to 1440.");
        CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRulePreviewAsync(
                new Api.AlertRulePreviewRequest { Condition = new Api.AlertRuleCondition { Threshold = 1 }, ResolveAfterMinutes = 0 }, Endpoint)),
            "resolveAfterMinutes must be 1 to 60.");
        Assert.IsInstanceOfType<ForbidResult>((await Sut(owner: false, reader: false).PostEndpointAlertRulePreviewAsync(
            new Api.AlertRulePreviewRequest { Condition = new Api.AlertRuleCondition { Threshold = 1 }, ResolveAfterMinutes = 5 }, Endpoint)).Result);
    }

    private sealed class NoClassifications : IAlertRuleClassifications
    {
        public Task<NimBus.Extensions.IntegrationIntelligence.ClassificationBulkReadResult> ReadLatestInBulkAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken cancellationToken)
            => Task.FromResult(new NimBus.Extensions.IntegrationIntelligence.ClassificationBulkReadResult(
                NimBus.Extensions.IntegrationIntelligence.ClassificationBulkReadStatus.Available,
                new Dictionary<string, NimBus.Extensions.IntegrationIntelligence.ClassificationSnapshot>()));
    }

    private static Api.AlertRuleRequest KindRequest(Api.AlertRuleCondition condition) => new()
    {
        Name = "ERP " + condition.Kind,
        Enabled = true,
        Condition = condition,
        ResolveAfterMinutes = 5,
        Targets = [Webhook("https://hooks.example.com/a")],
    };

    [TestMethod]
    public async Task A_failure_occurrences_rule_keeps_only_the_fields_it_uses()
    {
        var saved = Ok(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition
        {
            Kind = "failure_occurrences", Threshold = 2, WindowMinutes = 15, EventTypes = ["OrderPlaced"], Statuses = ["Failed"], Minutes = 30,
        }), Endpoint));

        var stored = (await _store.GetAlertRule(Endpoint, saved.Rule.Id))!.Condition;
        Assert.AreEqual(AlertRuleConditionKinds.FailureOccurrences, stored.Kind);
        Assert.AreEqual(15, stored.WindowMinutes);
        Assert.AreEqual(2, stored.Threshold);
        CollectionAssert.AreEqual(new[] { "OrderPlaced" }, stored.EventTypes);
        Assert.AreEqual(0, stored.Statuses.Count, "Statuses apply to the backlog only.");
        Assert.IsNull(stored.Minutes);
        Assert.AreEqual(15, saved.Rule.Condition.WindowMinutes, "The API returns the window.");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "no window")]
    [DataRow(4, DisplayName = "window under 5")]
    [DataRow(1441, DisplayName = "window over a day")]
    public async Task A_failure_occurrences_window_must_be_5_to_1440(int? window)
        => CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "failure_occurrences", Threshold = 2, WindowMinutes = window }), Endpoint)),
            "condition.windowMinutes must be 5 to 1440.");

    [TestMethod]
    public async Task A_blocked_session_rule_needs_minutes_or_a_deferred_threshold()
    {
        CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "blocked_session" }), Endpoint)),
            "A blocked-session condition needs minutes, deferredThreshold or both.");
        CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "blocked_session", DeferredThreshold = 1001 }), Endpoint)),
            "condition.deferredThreshold must be 1 to 1000.");

        var saved = Ok(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "blocked_session", Minutes = 30, DeferredThreshold = 50, Threshold = 7 }), Endpoint));
        var stored = (await _store.GetAlertRule(Endpoint, saved.Rule.Id))!.Condition;
        Assert.AreEqual(30, stored.Minutes);
        Assert.AreEqual(50, stored.DeferredThreshold);
        Assert.AreEqual(1, stored.Threshold, "The count threshold does not apply.");
    }

    [TestMethod]
    [DataRow(null, DisplayName = "no minutes")]
    [DataRow(4, DisplayName = "under 5 minutes")]
    [DataRow(1441, DisplayName = "over a day")]
    public async Task A_silence_rule_needs_5_to_1440_minutes(int? minutes)
        => CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "silence", Minutes = minutes }), Endpoint)),
            "condition.minutes must be 5 to 1440.");

    [TestMethod]
    public async Task An_unknown_kind_is_refused()
        => CollectionAssert.Contains(
            Errors(await Sut().PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "latency", Threshold = 1 }), Endpoint)),
            "condition.kind must be failure_backlog, failure_occurrences, blocked_session, silence.");

    [TestMethod]
    public async Task Without_alert_signals_only_backlog_rules_can_be_created()
    {
        var errors = Errors(await Sut(signals: false).PostEndpointAlertRuleAsync(KindRequest(new Api.AlertRuleCondition { Kind = "silence", Minutes = 60 }), Endpoint));
        StringAssert.Contains(errors.Single(), "needs a storage provider that can read alert signals");

        var listed = Ok(await Sut(signals: false).GetEndpointAlertRulesAsync(Endpoint));
        CollectionAssert.AreEqual(new[] { "failure_backlog" }, listed.AvailableKinds.ToArray());
        CollectionAssert.AreEqual(AlertRuleConditionKinds.All.ToArray(), Ok(await Sut().GetEndpointAlertRulesAsync(Endpoint)).AvailableKinds.ToArray());
    }

    private sealed class NoSignals : IAlertSignalStore
    {
        public Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap) => Task.FromResult(0);

        public Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId) => Task.FromResult<BlockingEvent?>(null);

        public Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc) => Task.FromResult(true);

        public Task<IReadOnlyList<MinuteCount>> CountMessagesPerMinute(string endpointId, NimBus.Core.Messages.MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
            => Task.FromResult<IReadOnlyList<MinuteCount>>([]);

        public Task<FailedAttemptPage> ListFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int limit)
            => Task.FromResult(new FailedAttemptPage([], false));
    }

    private sealed class FixedAuthorization(bool owner, bool reader, Func<string, bool>? canRead) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(
            required == AccessRole.Owner ? owner
            : endpointId is not null && canRead is not null ? canRead(endpointId)
            : reader);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());

        public string GetCurrentUserName() => "owner@example.com";
    }

    private sealed class RecordingAudit : IAuditLogService
    {
        public List<(MessageAuditType Type, bool AccessDenied, string? Data)> Logged { get; } = [];

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Logged.Add((type, accessDenied, data));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
            => LogAuditAsync(type, context, data: data, cancellationToken: cancellationToken);
    }

    private sealed class RecordingWebhooks : IAlertRuleWebhookSender
    {
        public List<(AlertRuleNotice Notice, AlertRuleTarget Target)> Sent { get; } = [];

        public bool CanSendTo(string? url) => EndpointAlertWebhook.IsValidUrl(url);

        public Task SendRuleNoticeAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken = default)
        {
            Sent.Add((notice, target));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingMail : IAlertMailSender
    {
        public Task SendAsync(AlertMail mail, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePlatform(params string[] endpointIds) : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints { get; } = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEventType> EventTypes => [];
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => [];
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => [];
    }

    private sealed class FakeEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
