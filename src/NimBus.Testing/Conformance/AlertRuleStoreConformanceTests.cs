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
/// Provider-agnostic conformance suite for <see cref="IAlertRuleStore"/> (Spec 053). Every test
/// scopes its endpoints and rules by unique ids, so a shared backend may hold other rows; the
/// evaluator lease is one shared record, so lease tests work from times after whatever it holds.
/// </summary>
[TestClass]
public abstract class AlertRuleStoreConformanceTests
{
    // Sub-millisecond ticks catch a provider that rounds or drops precision.
    private static readonly DateTime T0 = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc).AddTicks(1234567);

    private readonly string _scope = Guid.NewGuid().ToString("N")[..12];

    /// <summary>A freshly initialized store.</summary>
    protected abstract IAlertRuleStore CreateStore();

    /// <summary>
    /// True when the backend keeps the evaluator lease across tests (a shared database with no
    /// reset), so a test cannot see the lease before its first claim.
    /// </summary>
    protected virtual bool SharesEvaluatorLease => false;

    private string Endpoint(string suffix = "a") => $"alert-ep-{_scope}-{suffix}";

    private string RuleId(string suffix) => $"{_scope}{suffix}".PadRight(32, '0')[..32];

    private AlertRule NewRule(string suffix, string? endpointId = null, Action<AlertRule>? configure = null)
    {
        var ruleId = RuleId(suffix);
        var rule = new AlertRule
        {
            RuleId = ruleId,
            EndpointId = endpointId ?? Endpoint(),
            Name = "Backlog on " + suffix,
            Enabled = true,
            Condition = new AlertRuleCondition
            {
                Kind = AlertRuleConditionKinds.FailureBacklog,
                EventTypes = ["OrderPlaced", "OrderCancelled"],
                Statuses = ["Failed", "DeadLettered"],
                ErrorTextContains = "timeout",
                Threshold = 3,
            },
            ResolveAfterMinutes = 7,
            Targets =
            [
                new AlertRuleTarget { TargetId = "t1", Type = AlertRuleTargetType.Mail, Address = "ops@example.com" },
                new AlertRuleTarget { TargetId = "t2", Type = AlertRuleTargetType.Webhook, Url = "https://hooks.example.com/nimbus" },
            ],
            CreatedBy = "owner@example.com",
            CreatedAtUtc = T0,
            UpdatedBy = "editor@example.com",
            UpdatedAtUtc = T0.AddMinutes(1),
            State = new AlertRuleState(),
        };
        configure?.Invoke(rule);
        return rule;
    }

    private static AlertRuleState FiringState(string ruleId, int seq = 1, bool withNotice = true)
    {
        var state = new AlertRuleState
        {
            Status = AlertRuleStatus.Firing,
            SinceUtc = T0.AddMinutes(5),
            LastValue = 3,
            UpdatedAtUtc = T0.AddMinutes(5).AddTicks(17),
            ConditionFalseSinceUtc = T0.AddMinutes(6),
            TransitionSeq = seq,
            Transitions =
            [
                new AlertRuleTransition { Seq = seq, Status = AlertRuleStatus.Firing, AtUtc = T0.AddMinutes(5), Value = 3 },
                new AlertRuleTransition { Seq = seq - 1, Status = AlertRuleStatus.Ok, AtUtc = T0.AddMinutes(2), Reason = AlertRuleResolveReasons.Cleared },
            ],
        };
        if (withNotice)
        {
            state.PendingNotices.Add(new AlertRulePendingNotice
            {
                NoticeId = $"{ruleId}:{seq}:t2",
                Target = new AlertRuleTarget { TargetId = "t2", Type = AlertRuleTargetType.Webhook, Url = "https://hooks.example.com/nimbus" },
                Status = AlertRuleStatus.Firing,
                Reason = null,
                Value = 3,
                SinceUtc = T0.AddMinutes(5),
                Attempts = 2,
                CreatedAtUtc = T0.AddMinutes(5).AddTicks(3),
                LastError = "hooks.example.com 503",
            });
        }

        return state;
    }

    private static async Task<AlertRule> CreateAsync(IAlertRuleStore store, AlertRule rule)
    {
        var created = await store.TrySaveAlertRuleDefinition(rule, null);
        Assert.IsNotNull(created, $"Creating rule {rule.RuleId} failed.");
        return created;
    }

    private static void AssertInstant(DateTime expected, DateTime actual, string what)
    {
        Assert.AreEqual(expected.Ticks, actual.Ticks, what);
        Assert.AreEqual(DateTimeKind.Utc, actual.Kind, what + " kind");
    }

    private static void AssertInstant(DateTime? expected, DateTime? actual, string what)
    {
        Assert.AreEqual(expected.HasValue, actual.HasValue, what);
        if (expected is { } e)
            AssertInstant(e, actual!.Value, what);
    }

    private static void AssertDefinition(AlertRule expected, AlertRule actual)
    {
        Assert.AreEqual(expected.RuleId, actual.RuleId);
        Assert.AreEqual(expected.EndpointId, actual.EndpointId);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.Enabled, actual.Enabled);
        Assert.AreEqual(expected.Condition.Kind, actual.Condition.Kind);
        CollectionAssert.AreEqual(expected.Condition.EventTypes, actual.Condition.EventTypes);
        CollectionAssert.AreEqual(expected.Condition.Statuses, actual.Condition.Statuses);
        Assert.AreEqual(expected.Condition.ErrorTextContains, actual.Condition.ErrorTextContains);
        Assert.AreEqual(expected.Condition.Threshold, actual.Condition.Threshold);
        Assert.AreEqual(expected.ResolveAfterMinutes, actual.ResolveAfterMinutes);
        Assert.AreEqual(expected.Targets.Count, actual.Targets.Count);
        for (var i = 0; i < expected.Targets.Count; i++)
        {
            Assert.AreEqual(expected.Targets[i].TargetId, actual.Targets[i].TargetId);
            Assert.AreEqual(expected.Targets[i].Type, actual.Targets[i].Type);
            Assert.AreEqual(expected.Targets[i].Address, actual.Targets[i].Address);
            Assert.AreEqual(expected.Targets[i].Url, actual.Targets[i].Url);
        }

        Assert.AreEqual(expected.CreatedBy, actual.CreatedBy);
        AssertInstant(expected.CreatedAtUtc, actual.CreatedAtUtc, nameof(AlertRule.CreatedAtUtc));
        Assert.AreEqual(expected.UpdatedBy, actual.UpdatedBy);
        AssertInstant(expected.UpdatedAtUtc, actual.UpdatedAtUtc, nameof(AlertRule.UpdatedAtUtc));
    }

    private static void AssertState(AlertRuleState expected, AlertRuleState actual, bool withTransitions = true)
    {
        Assert.AreEqual(expected.Status, actual.Status);
        AssertInstant(expected.SinceUtc, actual.SinceUtc, nameof(AlertRuleState.SinceUtc));
        Assert.AreEqual(expected.LastValue, actual.LastValue);
        AssertInstant(expected.UpdatedAtUtc, actual.UpdatedAtUtc, "State.UpdatedAtUtc");
        AssertInstant(expected.ConditionFalseSinceUtc, actual.ConditionFalseSinceUtc, nameof(AlertRuleState.ConditionFalseSinceUtc));
        Assert.AreEqual(expected.TransitionSeq, actual.TransitionSeq);

        Assert.AreEqual(expected.PendingNotices.Count, actual.PendingNotices.Count);
        for (var i = 0; i < expected.PendingNotices.Count; i++)
        {
            var e = expected.PendingNotices[i];
            var a = actual.PendingNotices[i];
            Assert.AreEqual(e.NoticeId, a.NoticeId);
            Assert.AreEqual(e.Target.TargetId, a.Target.TargetId);
            Assert.AreEqual(e.Target.Type, a.Target.Type);
            Assert.AreEqual(e.Target.Address, a.Target.Address);
            Assert.AreEqual(e.Target.Url, a.Target.Url);
            Assert.AreEqual(e.Status, a.Status);
            Assert.AreEqual(e.Reason, a.Reason);
            Assert.AreEqual(e.Value, a.Value);
            AssertInstant(e.SinceUtc, a.SinceUtc, "Notice.SinceUtc");
            Assert.AreEqual(e.Attempts, a.Attempts);
            AssertInstant(e.CreatedAtUtc, a.CreatedAtUtc, "Notice.CreatedAtUtc");
            Assert.AreEqual(e.LastError, a.LastError);
        }

        if (!withTransitions)
        {
            Assert.AreEqual(0, actual.Transitions.Count, "Transitions must be left empty.");
            return;
        }

        Assert.AreEqual(expected.Transitions.Count, actual.Transitions.Count);
        for (var i = 0; i < expected.Transitions.Count; i++)
        {
            var e = expected.Transitions[i];
            var a = actual.Transitions[i];
            Assert.AreEqual(e.Seq, a.Seq);
            Assert.AreEqual(e.Status, a.Status);
            AssertInstant(e.AtUtc, a.AtUtc, "Transition.AtUtc");
            Assert.AreEqual(e.Value, a.Value);
            Assert.AreEqual(e.Reason, a.Reason);
        }
    }

    // ───────── Rules ─────────

    [TestMethod]
    public async Task Create_then_get_round_trips_every_field()
    {
        var store = CreateStore();
        var rule = NewRule("roundtrip", configure: r => r.State = FiringState(RuleId("roundtrip")));

        var created = await store.TrySaveAlertRuleDefinition(rule, null);
        var read = await store.GetAlertRule(rule.EndpointId, rule.RuleId);

        Assert.IsNotNull(created);
        Assert.AreEqual(1L, created.Version);
        AssertDefinition(rule, created);
        AssertState(rule.State, created.State);
        Assert.IsNotNull(read);
        Assert.AreEqual(1L, read.Version);
        AssertDefinition(rule, read);
        AssertState(rule.State, read.State);
    }

    [TestMethod]
    public async Task A_rule_without_optional_values_round_trips()
    {
        var store = CreateStore();
        var rule = NewRule("sparse", configure: r =>
        {
            r.Condition = new AlertRuleCondition { Threshold = 1 };
            r.Targets = [];
            r.UpdatedBy = null;
        });

        await CreateAsync(store, rule);
        var read = await store.GetAlertRule(rule.EndpointId, rule.RuleId);

        Assert.IsNotNull(read);
        AssertDefinition(rule, read);
        AssertState(new AlertRuleState(), read.State);
    }

    [TestMethod]
    public async Task Creating_an_existing_rule_returns_null_and_changes_nothing()
    {
        var store = CreateStore();
        var rule = NewRule("twice");
        await CreateAsync(store, rule);

        var second = await store.TrySaveAlertRuleDefinition(NewRule("twice", configure: r => r.Name = "Other"), null);

        Assert.IsNull(second);
        var read = await store.GetAlertRule(rule.EndpointId, rule.RuleId);
        Assert.AreEqual(rule.Name, read!.Name);
        Assert.AreEqual(1L, read.Version);
    }

    [TestMethod]
    public async Task A_missing_rule_reads_as_null()
        => Assert.IsNull(await CreateStore().GetAlertRule(Endpoint(), RuleId("missing")));

    [TestMethod]
    public async Task A_rule_under_another_endpoint_is_not_found_saved_or_deleted()
    {
        var store = CreateStore();
        var rule = NewRule("scoped");
        await CreateAsync(store, rule);
        var other = Endpoint("other");

        Assert.IsNull(await store.GetAlertRule(other, rule.RuleId));
        Assert.IsNull(await store.TrySaveAlertRuleDefinition(NewRule("scoped", other, r => r.Name = "Moved"), 1));
        Assert.IsNull(await store.TrySaveAlertRuleState(other, rule.RuleId, 1, FiringState(rule.RuleId)));
        Assert.IsFalse(await store.DeleteAlertRule(other, rule.RuleId, 1));
        Assert.AreEqual(0, (await store.ListAlertRulesByEndpoint(other)).Count);

        var read = await store.GetAlertRule(rule.EndpointId, rule.RuleId);
        Assert.IsNotNull(read);
        Assert.AreEqual(1L, read.Version);
        Assert.AreEqual(rule.Name, read.Name);
        Assert.AreEqual(AlertRuleStatus.Ok, read.State.Status);
    }

    [TestMethod]
    public async Task The_same_rule_id_on_two_endpoints_is_two_rules()
    {
        var store = CreateStore();
        var first = NewRule("shared");
        var second = NewRule("shared", Endpoint("b"), r => r.Name = "Second");

        await CreateAsync(store, first);
        await CreateAsync(store, second);

        Assert.AreEqual(first.Name, (await store.GetAlertRule(first.EndpointId, first.RuleId))!.Name);
        Assert.AreEqual("Second", (await store.GetAlertRule(second.EndpointId, second.RuleId))!.Name);
    }

    [TestMethod]
    public async Task Every_write_bumps_the_version()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("versions"));
        Assert.AreEqual(1L, created.Version);

        created.Name = "Renamed";
        var edited = await store.TrySaveAlertRuleDefinition(created, 1);
        Assert.IsNotNull(edited);
        Assert.AreEqual(2L, edited.Version);

        var stateVersion = await store.TrySaveAlertRuleState(created.EndpointId, created.RuleId, 2, FiringState(created.RuleId));
        Assert.AreEqual(3L, stateVersion);

        var read = await store.GetAlertRule(created.EndpointId, created.RuleId);
        Assert.AreEqual(3L, read!.Version);
        Assert.AreEqual("Renamed", read.Name);
        Assert.AreEqual(AlertRuleStatus.Firing, read.State.Status);
    }

    [TestMethod]
    public async Task The_store_sets_the_version_whatever_the_caller_passes()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("ignoresversion", configure: r => r.Version = 41));
        Assert.AreEqual(1L, created.Version);

        created.Version = 99;
        var edited = await store.TrySaveAlertRuleDefinition(created, 1);
        Assert.AreEqual(2L, edited!.Version);
    }

    [TestMethod]
    public async Task Saving_a_definition_on_a_stale_version_returns_null_and_writes_nothing()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("stale-def"));
        created.Name = "First edit";
        Assert.IsNotNull(await store.TrySaveAlertRuleDefinition(created, 1));

        created.Name = "Racing edit";
        var racing = await store.TrySaveAlertRuleDefinition(created, 1);

        Assert.IsNull(racing);
        var read = await store.GetAlertRule(created.EndpointId, created.RuleId);
        Assert.AreEqual("First edit", read!.Name);
        Assert.AreEqual(2L, read.Version);
    }

    [TestMethod]
    public async Task Saving_a_missing_rule_returns_null()
    {
        var store = CreateStore();
        var rule = NewRule("never-created");

        Assert.IsNull(await store.TrySaveAlertRuleDefinition(rule, 1));
        Assert.IsNull(await store.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, 1, FiringState(rule.RuleId)));
        Assert.IsNull(await store.GetAlertRule(rule.EndpointId, rule.RuleId));
    }

    [TestMethod]
    public async Task Saving_state_on_a_stale_version_returns_null_and_writes_nothing()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("stale-state"));
        Assert.AreEqual(2L, await store.TrySaveAlertRuleState(created.EndpointId, created.RuleId, 1, FiringState(created.RuleId)));

        var resolved = new AlertRuleState { Status = AlertRuleStatus.Ok, SinceUtc = T0.AddMinutes(20), TransitionSeq = 2 };
        var racing = await store.TrySaveAlertRuleState(created.EndpointId, created.RuleId, 1, resolved);

        Assert.IsNull(racing);
        var read = await store.GetAlertRule(created.EndpointId, created.RuleId);
        Assert.AreEqual(2L, read!.Version);
        AssertState(FiringState(created.RuleId), read.State);
    }

    [TestMethod]
    public async Task Saving_state_keeps_the_stored_definition()
    {
        var store = CreateStore();
        var rule = NewRule("keeps-def");
        await CreateAsync(store, rule);
        var state = FiringState(rule.RuleId);

        var version = await store.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, 1, state);

        Assert.AreEqual(2L, version);
        var read = await store.GetAlertRule(rule.EndpointId, rule.RuleId);
        AssertDefinition(rule, read!);
        AssertState(state, read!.State);
    }

    [TestMethod]
    public async Task An_edit_invalidates_a_state_save_based_on_the_version_before_it()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("edit-wins"));
        var evaluatorRead = (await store.GetAlertRule(created.EndpointId, created.RuleId))!;

        created.Condition.Threshold = 10;
        Assert.IsNotNull(await store.TrySaveAlertRuleDefinition(created, 1));

        var saved = await store.TrySaveAlertRuleState(
            evaluatorRead.EndpointId, evaluatorRead.RuleId, evaluatorRead.Version, FiringState(created.RuleId));

        Assert.IsNull(saved);
        var read = await store.GetAlertRule(created.EndpointId, created.RuleId);
        Assert.AreEqual(10, read!.Condition.Threshold);
        Assert.AreEqual(AlertRuleStatus.Ok, read.State.Status);
        Assert.AreEqual(2L, read.Version);
    }

    [TestMethod]
    public async Task A_definition_save_stores_the_reset_state_it_carries()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("reset"));
        Assert.AreEqual(2L, await store.TrySaveAlertRuleState(created.EndpointId, created.RuleId, 1, FiringState(created.RuleId, withNotice: false)));
        var firing = (await store.GetAlertRule(created.EndpointId, created.RuleId))!;

        // An edit of a Firing rule closes the incident: Ok, a rule_changed transition and its notice.
        var reset = firing.State.Clone();
        reset.Status = AlertRuleStatus.Ok;
        reset.SinceUtc = T0.AddMinutes(30);
        reset.LastValue = null;
        reset.ConditionFalseSinceUtc = null;
        reset.TransitionSeq = 2;
        reset.Transitions.Insert(0, new AlertRuleTransition
        {
            Seq = 2,
            Status = AlertRuleStatus.Ok,
            AtUtc = T0.AddMinutes(30),
            Reason = AlertRuleResolveReasons.RuleChanged,
        });
        reset.PendingNotices.Add(new AlertRulePendingNotice
        {
            NoticeId = $"{created.RuleId}:2:t1",
            Target = firing.Targets[0].Clone(),
            Status = AlertRuleStatus.Ok,
            Reason = AlertRuleResolveReasons.RuleChanged,
            SinceUtc = T0.AddMinutes(30),
            CreatedAtUtc = T0.AddMinutes(30),
        });
        firing.Condition.Threshold = 5;
        firing.State = reset;

        var edited = await store.TrySaveAlertRuleDefinition(firing, firing.Version);

        Assert.IsNotNull(edited);
        Assert.AreEqual(3L, edited.Version);
        var read = await store.GetAlertRule(created.EndpointId, created.RuleId);
        Assert.AreEqual(5, read!.Condition.Threshold);
        AssertState(reset, read.State);
    }

    [TestMethod]
    public async Task ListAlertRulesByEndpoint_returns_that_endpoints_rules_with_transitions_in_creation_order()
    {
        var store = CreateStore();
        var second = NewRule("list2", configure: r => r.CreatedAtUtc = T0.AddMinutes(2));
        var first = NewRule("list1", configure: r => r.CreatedAtUtc = T0.AddMinutes(1));
        await CreateAsync(store, second);
        await CreateAsync(store, first);
        await CreateAsync(store, NewRule("list3", Endpoint("elsewhere")));
        Assert.IsNotNull(await store.TrySaveAlertRuleState(first.EndpointId, first.RuleId, 1, FiringState(first.RuleId)));

        var rules = await store.ListAlertRulesByEndpoint(Endpoint());

        CollectionAssert.AreEqual(new[] { first.RuleId, second.RuleId }, rules.Select(r => r.RuleId).ToList());
        Assert.AreEqual(2L, rules[0].Version);
        AssertDefinition(first, rules[0]);
        AssertState(FiringState(first.RuleId), rules[0].State);
        Assert.AreEqual(1L, rules[1].Version);
    }

    [TestMethod]
    public async Task ListAlertRulesByEndpoint_of_an_endpoint_without_rules_is_empty()
        => Assert.AreEqual(0, (await CreateStore().ListAlertRulesByEndpoint(Endpoint("none"))).Count);

    [TestMethod]
    public async Task ListAlertRulesToEvaluate_returns_enabled_rules_and_disabled_rules_with_pending_notices_without_transitions()
    {
        var store = CreateStore();
        var enabled = await CreateAsync(store, NewRule("eval-enabled"));
        var draining = await CreateAsync(store, NewRule("eval-draining", Endpoint("b"), r => r.Enabled = false));
        var disabled = await CreateAsync(store, NewRule("eval-disabled", configure: r => r.Enabled = false));
        var enabledState = FiringState(enabled.RuleId);
        var drainingState = FiringState(draining.RuleId);
        Assert.IsNotNull(await store.TrySaveAlertRuleState(enabled.EndpointId, enabled.RuleId, 1, enabledState));
        Assert.IsNotNull(await store.TrySaveAlertRuleState(draining.EndpointId, draining.RuleId, 1, drainingState));
        Assert.IsNotNull(await store.TrySaveAlertRuleState(
            disabled.EndpointId, disabled.RuleId, 1, FiringState(disabled.RuleId, withNotice: false)));

        var mine = (await store.ListAlertRulesToEvaluate(10_000))
            .Where(r => r.RuleId.StartsWith(_scope, StringComparison.Ordinal))
            .ToDictionary(r => r.RuleId);

        CollectionAssert.AreEquivalent(new[] { enabled.RuleId, draining.RuleId }, mine.Keys.ToList());

        var listedEnabled = mine[enabled.RuleId];
        Assert.AreEqual(2L, listedEnabled.Version);
        AssertDefinition(enabled, listedEnabled);
        AssertState(enabledState, listedEnabled.State, withTransitions: false);

        var listedDraining = mine[draining.RuleId];
        Assert.IsFalse(listedDraining.Enabled);
        Assert.AreEqual(draining.EndpointId, listedDraining.EndpointId);
        AssertState(drainingState, listedDraining.State, withTransitions: false);
    }

    [TestMethod]
    public async Task ListAlertRulesToEvaluate_honours_the_limit()
    {
        var store = CreateStore();
        await CreateAsync(store, NewRule("limit1"));
        await CreateAsync(store, NewRule("limit2"));

        Assert.AreEqual(1, (await store.ListAlertRulesToEvaluate(1)).Count);
    }

    [TestMethod]
    public async Task ListEndpointsWithFiringAlertRules_lists_each_endpoint_with_a_firing_rule_once()
    {
        var store = CreateStore();
        var firingA1 = await CreateAsync(store, NewRule("fire1"));
        var firingA2 = await CreateAsync(store, NewRule("fire2"));
        var firingB = await CreateAsync(store, NewRule("fire3", Endpoint("b")));
        await CreateAsync(store, NewRule("fire4", Endpoint("quiet")));
        var resolved = await CreateAsync(store, NewRule("fire5", Endpoint("resolved")));
        foreach (var rule in new[] { firingA1, firingA2, firingB, resolved })
            Assert.IsNotNull(await store.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, 1, FiringState(rule.RuleId)));
        Assert.IsNotNull(await store.TrySaveAlertRuleState(
            resolved.EndpointId, resolved.RuleId, 2, new AlertRuleState { Status = AlertRuleStatus.Ok, SinceUtc = T0.AddMinutes(40) }));

        var mine = (await store.ListEndpointsWithFiringAlertRules())
            .Where(e => e.Contains(_scope, StringComparison.Ordinal))
            .ToList();

        CollectionAssert.AreEquivalent(new[] { Endpoint(), Endpoint("b") }, mine);
    }

    [TestMethod]
    public async Task Delete_removes_the_rule_on_the_current_version_only()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("delete"));
        Assert.AreEqual(2L, await store.TrySaveAlertRuleState(created.EndpointId, created.RuleId, 1, FiringState(created.RuleId)));

        Assert.IsFalse(await store.DeleteAlertRule(created.EndpointId, created.RuleId, 1));
        Assert.IsNotNull(await store.GetAlertRule(created.EndpointId, created.RuleId));

        Assert.IsTrue(await store.DeleteAlertRule(created.EndpointId, created.RuleId, 2));
        Assert.IsNull(await store.GetAlertRule(created.EndpointId, created.RuleId));
        Assert.IsFalse(await store.DeleteAlertRule(created.EndpointId, created.RuleId, 2));
    }

    [TestMethod]
    public async Task A_deleted_rule_can_be_created_again_at_version_one()
    {
        var store = CreateStore();
        var created = await CreateAsync(store, NewRule("recreate"));
        Assert.IsTrue(await store.DeleteAlertRule(created.EndpointId, created.RuleId, 1));

        var again = await store.TrySaveAlertRuleDefinition(NewRule("recreate"), null);

        Assert.IsNotNull(again);
        Assert.AreEqual(1L, again.Version);
    }

    [TestMethod]
    public async Task Callers_never_hold_a_stored_rule_by_reference()
    {
        var store = CreateStore();
        var rule = NewRule("copies", configure: r => r.State = FiringState(RuleId("copies")));
        var created = await CreateAsync(store, rule);

        rule.Name = "Changed input";
        rule.Condition.EventTypes.Add("Mutated");
        rule.State.PendingNotices.Clear();
        created.Targets.Clear();
        created.State.Transitions.Clear();
        var read = (await store.GetAlertRule(rule.EndpointId, rule.RuleId))!;
        read.Condition.Statuses.Clear();
        read.State.Status = AlertRuleStatus.Ok;
        var state = FiringState(rule.RuleId, seq: 4);
        Assert.AreEqual(2L, await store.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, 1, state));
        state.PendingNotices.Clear();

        var again = (await store.GetAlertRule(rule.EndpointId, rule.RuleId))!;
        Assert.AreEqual("Backlog on copies", again.Name);
        CollectionAssert.AreEqual(new[] { "OrderPlaced", "OrderCancelled" }, again.Condition.EventTypes);
        CollectionAssert.AreEqual(new[] { "Failed", "DeadLettered" }, again.Condition.Statuses);
        Assert.AreEqual(2, again.Targets.Count);
        Assert.AreEqual(AlertRuleStatus.Firing, again.State.Status);
        Assert.AreEqual(4L, again.State.TransitionSeq);
        Assert.AreEqual(1, again.State.PendingNotices.Count);
        Assert.AreEqual(2, again.State.Transitions.Count);
    }

    // ───────── Evaluator lease ─────────

    // Lease times start after anything the shared lease already holds, so the tests never depend
    // on a fresh record or on each other's order.
    private static async Task<DateTime> AfterCurrentLeaseAsync(IAlertRuleStore store)
    {
        var status = await store.GetAlertRuleEvaluatorStatus();
        var latest = new[] { status.LastClaimedAtUtc, status.LastCompletedAtUtc, T0 }.Max()!.Value;
        return DateTime.SpecifyKind(latest, DateTimeKind.Utc).AddHours(1).AddTicks(7);
    }

    [TestMethod]
    public async Task GetAlertRuleEvaluatorStatus_is_empty_before_the_first_claim()
    {
        var status = await CreateStore().GetAlertRuleEvaluatorStatus();

        Assert.IsNotNull(status);
        if (SharesEvaluatorLease)
            return; // The shared record may hold an earlier test's claim; only the shape is checked.

        Assert.IsNull(status.LastClaimedAtUtc);
        Assert.IsNull(status.LastClaimedBy);
        Assert.IsNull(status.LastCompletedAtUtc);
    }

    [TestMethod]
    public async Task A_claim_records_its_time_and_instance()
    {
        var store = CreateStore();
        var now = await AfterCurrentLeaseAsync(store);

        Assert.IsTrue(await store.TryClaimAlertRuleEvaluatorTick(now, now, "instance-" + _scope));

        var status = await store.GetAlertRuleEvaluatorStatus();
        AssertInstant(now, status.LastClaimedAtUtc, nameof(AlertRuleEvaluatorStatus.LastClaimedAtUtc));
        Assert.AreEqual("instance-" + _scope, status.LastClaimedBy);
    }

    [TestMethod]
    public async Task Concurrent_claims_of_one_tick_let_exactly_one_win()
    {
        var store = CreateStore();
        var now = await AfterCurrentLeaseAsync(store);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(i => store.TryClaimAlertRuleEvaluatorTick(now, now, $"instance-{i}")));

        Assert.AreEqual(1, results.Count(r => r));
        var status = await store.GetAlertRuleEvaluatorStatus();
        Assert.AreEqual($"instance-{Array.IndexOf(results, true)}", status.LastClaimedBy);
        AssertInstant(now, status.LastClaimedAtUtc, nameof(AlertRuleEvaluatorStatus.LastClaimedAtUtc));
    }

    [TestMethod]
    public async Task A_claim_fails_until_the_last_one_is_before_dueBefore()
    {
        var store = CreateStore();
        var first = await AfterCurrentLeaseAsync(store);
        Assert.IsTrue(await store.TryClaimAlertRuleEvaluatorTick(first, first, "first"));

        // Claimed at exactly dueBefore: not due.
        Assert.IsFalse(await store.TryClaimAlertRuleEvaluatorTick(first, first.AddSeconds(30), "early"));
        Assert.AreEqual("first", (await store.GetAlertRuleEvaluatorStatus()).LastClaimedBy);

        var next = first.AddMinutes(1);
        Assert.IsTrue(await store.TryClaimAlertRuleEvaluatorTick(next.AddSeconds(-5), next, "second"));
        var status = await store.GetAlertRuleEvaluatorStatus();
        Assert.AreEqual("second", status.LastClaimedBy);
        AssertInstant(next, status.LastClaimedAtUtc, nameof(AlertRuleEvaluatorStatus.LastClaimedAtUtc));
    }

    [TestMethod]
    public async Task Complete_records_only_the_current_claim()
    {
        var store = CreateStore();
        var first = await AfterCurrentLeaseAsync(store);
        Assert.IsTrue(await store.TryClaimAlertRuleEvaluatorTick(first, first, "first"));

        Assert.IsTrue(await store.CompleteAlertRuleEvaluatorTick(first, first.AddSeconds(10)));
        AssertInstant(first.AddSeconds(10), (await store.GetAlertRuleEvaluatorStatus()).LastCompletedAtUtc, "after the first tick");

        var second = first.AddMinutes(1);
        Assert.IsTrue(await store.TryClaimAlertRuleEvaluatorTick(second, second, "second"));

        // The first tick's late completion must not overwrite anything.
        Assert.IsFalse(await store.CompleteAlertRuleEvaluatorTick(first, second.AddSeconds(5)));
        var status = await store.GetAlertRuleEvaluatorStatus();
        AssertInstant(first.AddSeconds(10), status.LastCompletedAtUtc, "after the stale completion");
        AssertInstant(second, status.LastClaimedAtUtc, nameof(AlertRuleEvaluatorStatus.LastClaimedAtUtc));

        Assert.IsTrue(await store.CompleteAlertRuleEvaluatorTick(second, second.AddSeconds(20)));
        AssertInstant(second.AddSeconds(20), (await store.GetAlertRuleEvaluatorStatus()).LastCompletedAtUtc, "after the second tick");
    }

    [TestMethod]
    public async Task Complete_before_any_claim_at_that_time_returns_false()
    {
        var store = CreateStore();
        var never = (await AfterCurrentLeaseAsync(store)).AddDays(1);

        Assert.IsFalse(await store.CompleteAlertRuleEvaluatorTick(never, never.AddSeconds(1)));
    }
}
