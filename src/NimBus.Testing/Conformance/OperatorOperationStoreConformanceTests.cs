#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IOperatorOperationStore"/>, the operator
/// operation journal (Spec 044 Phase 2). Every test scopes its operations by unique ids, so a
/// shared backend may hold other rows.
/// </summary>
[TestClass]
public abstract class OperatorOperationStoreConformanceTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _scope = Guid.NewGuid().ToString("N")[..12];

    /// <summary>A freshly initialized store.</summary>
    protected abstract IOperatorOperationStore CreateStore();

    private OperatorOperation New(string suffix, Action<OperatorOperation>? configure = null)
    {
        var operation = new OperatorOperation
        {
            OperationId = $"{_scope}{suffix}".PadRight(32, '0')[..32],
            TenantId = "tenant",
            Caller = "caller-" + _scope,
            ClientId = "client-" + _scope,
            IsWorkload = true,
            ActorName = "app:client",
            IdempotencyKey = Guid.NewGuid().ToString(),
            ArgumentsHash = "hash",
            Action = OperatorOperationAction.Resubmit,
            EndpointId = "endpoint-" + _scope,
            EventId = "event-" + _scope,
            SessionId = "session",
            SourceMessageId = "failed-attempt",
            MessageVersion = "version",
            Reason = "reason",
            Status = OperatorOperationStatus.Accepted,
            CreatedAtUtc = T0,
            UpdatedAtUtc = T0,
            ExpiresAtUtc = T0.AddDays(30),
            TimeToLiveSeconds = 30 * 24 * 3600,
        };
        configure?.Invoke(operation);
        return operation;
    }

    [TestMethod]
    public async Task Create_then_get_round_trips_every_field()
    {
        var store = CreateStore();
        var operation = New("a", o =>
        {
            o.Status = OperatorOperationStatus.Dispatched;
            o.ObservedStatus = "ResolutionResponse";
            o.EvidenceMessageId = "response-1";
            o.ObservedAtUtc = T0.AddMinutes(2);
            o.Detail = "detail";
            o.LeaseOwner = "instance-1";
            o.LeaseUntilUtc = T0.AddMinutes(1);
            o.DispatchedAtUtc = T0.AddSeconds(5);
            o.Action = OperatorOperationAction.Skip;
        });

        var created = await store.TryCreateOperatorOperation(operation);
        var read = await store.GetOperatorOperation(operation.OperationId);

        Assert.IsTrue(created.Created);
        Assert.IsFalse(string.IsNullOrEmpty(created.Operation.Version));
        Assert.IsNotNull(read);
        Assert.AreEqual(operation.TenantId, read.TenantId);
        Assert.AreEqual(operation.Caller, read.Caller);
        Assert.AreEqual(operation.ClientId, read.ClientId);
        Assert.IsTrue(read.IsWorkload);
        Assert.AreEqual(operation.ActorName, read.ActorName);
        Assert.AreEqual(operation.IdempotencyKey, read.IdempotencyKey);
        Assert.AreEqual(operation.ArgumentsHash, read.ArgumentsHash);
        Assert.AreEqual(OperatorOperationAction.Skip, read.Action);
        Assert.AreEqual(operation.EndpointId, read.EndpointId);
        Assert.AreEqual(operation.EventId, read.EventId);
        Assert.AreEqual(operation.SessionId, read.SessionId);
        Assert.AreEqual(operation.SourceMessageId, read.SourceMessageId);
        Assert.AreEqual(operation.MessageVersion, read.MessageVersion);
        Assert.AreEqual(operation.Reason, read.Reason);
        Assert.AreEqual(OperatorOperationStatus.Dispatched, read.Status);
        Assert.AreEqual("ResolutionResponse", read.ObservedStatus);
        Assert.AreEqual("response-1", read.EvidenceMessageId);
        Assert.AreEqual("detail", read.Detail);
        Assert.AreEqual("instance-1", read.LeaseOwner);
        // Compare by ticks: providers may hand DateTimes back with another Kind.
        Assert.AreEqual(T0.AddMinutes(2).Ticks, read.ObservedAtUtc!.Value.Ticks);
        Assert.AreEqual(T0.AddMinutes(1).Ticks, read.LeaseUntilUtc!.Value.Ticks);
        Assert.AreEqual(T0.AddSeconds(5).Ticks, read.DispatchedAtUtc!.Value.Ticks);
        Assert.AreEqual(T0.Ticks, read.CreatedAtUtc.Ticks);
        Assert.AreEqual(T0.Ticks, read.UpdatedAtUtc.Ticks);
        Assert.AreEqual(T0.AddDays(30).Ticks, read.ExpiresAtUtc.Ticks);
        Assert.AreEqual(created.Operation.Version, read.Version);
    }

    [TestMethod]
    public async Task Creating_an_existing_operation_returns_the_stored_one_and_changes_nothing()
    {
        var store = CreateStore();
        var first = New("b");
        await store.TryCreateOperatorOperation(first);

        var second = await store.TryCreateOperatorOperation(New("b", o => o.Reason = "another reason"));

        Assert.IsFalse(second.Created);
        Assert.AreEqual("reason", second.Operation.Reason);
        Assert.AreEqual("reason", (await store.GetOperatorOperation(first.OperationId))!.Reason);
    }

    [TestMethod]
    public async Task A_missing_operation_reads_as_null()
        => Assert.IsNull(await CreateStore().GetOperatorOperation(New("missing").OperationId));

    [TestMethod]
    public async Task Update_succeeds_on_the_current_version_and_sets_a_new_one()
    {
        var store = CreateStore();
        var created = (await store.TryCreateOperatorOperation(New("c"))).Operation;
        var version = created.Version;
        created.Status = OperatorOperationStatus.Claimed;
        created.UpdatedAtUtc = T0.AddSeconds(1);

        var updated = await store.TryUpdateOperatorOperation(created, version);

        Assert.IsTrue(updated);
        Assert.AreNotEqual(version, created.Version);
        var read = await store.GetOperatorOperation(created.OperationId);
        Assert.AreEqual(OperatorOperationStatus.Claimed, read!.Status);
        Assert.AreEqual(created.Version, read.Version);
    }

    [TestMethod]
    public async Task Update_on_a_stale_version_writes_nothing()
    {
        var store = CreateStore();
        var created = (await store.TryCreateOperatorOperation(New("d"))).Operation;
        var stale = created.Version;
        created.Status = OperatorOperationStatus.Claimed;
        Assert.IsTrue(await store.TryUpdateOperatorOperation(created, stale));

        var racing = created.Clone();
        racing.Status = OperatorOperationStatus.NotSent;
        var updated = await store.TryUpdateOperatorOperation(racing, stale);

        Assert.IsFalse(updated);
        Assert.AreEqual(OperatorOperationStatus.Claimed, (await store.GetOperatorOperation(created.OperationId))!.Status);
    }

    [TestMethod]
    public async Task Update_of_a_missing_operation_returns_false()
        => Assert.IsFalse(await CreateStore().TryUpdateOperatorOperation(New("e"), "no-such-version"));

    [TestMethod]
    public async Task ListDue_returns_matching_statuses_updated_before_the_cutoff_oldest_first()
    {
        var store = CreateStore();
        await store.TryCreateOperatorOperation(New("f1", o => { o.Status = OperatorOperationStatus.Publishing; o.UpdatedAtUtc = T0.AddMinutes(-3); }));
        await store.TryCreateOperatorOperation(New("f2", o => { o.Status = OperatorOperationStatus.Claimed; o.UpdatedAtUtc = T0.AddMinutes(-5); }));
        await store.TryCreateOperatorOperation(New("f3", o => { o.Status = OperatorOperationStatus.Succeeded; o.UpdatedAtUtc = T0.AddMinutes(-9); }));
        await store.TryCreateOperatorOperation(New("f4", o => { o.Status = OperatorOperationStatus.Claimed; o.UpdatedAtUtc = T0.AddMinutes(1); }));

        var due = (await store.ListDueOperatorOperations(
                [OperatorOperationStatus.Claimed, OperatorOperationStatus.Publishing], T0, 1000))
            .Where(o => o.OperationId.StartsWith(_scope, StringComparison.Ordinal))
            .Select(o => o.OperationId)
            .ToList();

        CollectionAssert.AreEqual(new[] { New("f2").OperationId, New("f1").OperationId }, due);
    }

    [TestMethod]
    public async Task Count_filters_by_every_query_member()
    {
        var store = CreateStore();
        var since = T0.AddHours(-1);
        await store.TryCreateOperatorOperation(New("g1"));
        await store.TryCreateOperatorOperation(New("g2", o => o.Status = OperatorOperationStatus.NotSent));
        await store.TryCreateOperatorOperation(New("g3", o => o.Action = OperatorOperationAction.Skip));
        await store.TryCreateOperatorOperation(New("g4", o => o.IsWorkload = false));
        await store.TryCreateOperatorOperation(New("g5", o => o.EventId = "other-event-" + _scope));
        await store.TryCreateOperatorOperation(New("g6", o => o.CreatedAtUtc = T0.AddHours(-2)));
        await store.TryCreateOperatorOperation(New("g7", o => o.ClientId = "other-client-" + _scope));

        var count = await store.CountOperatorOperations(new OperatorOperationQuery
        {
            Action = OperatorOperationAction.Resubmit,
            TenantId = "tenant",
            ClientId = "client-" + _scope,
            IsWorkload = true,
            EndpointId = "endpoint-" + _scope,
            EventId = "event-" + _scope,
            CreatedFromUtc = since,
            ExcludedStatuses = [OperatorOperationStatus.NotSent],
        });
        var excludingOne = await store.CountOperatorOperations(new OperatorOperationQuery
        {
            ClientId = "client-" + _scope,
            ExcludedOperationId = New("g1").OperationId,
        });

        Assert.AreEqual(1, count, "Only g1 matches every member.");
        Assert.AreEqual(5, excludingOne, "g2-g6 share the client; g1 is excluded by id.");
    }

    [TestMethod]
    public async Task Purge_removes_only_expired_operations()
    {
        var store = CreateStore();
        var expired = New("h1", o => o.ExpiresAtUtc = T0.AddDays(-1));
        var live = New("h2", o => o.ExpiresAtUtc = T0.AddDays(1));
        await store.TryCreateOperatorOperation(expired);
        await store.TryCreateOperatorOperation(live);

        await store.PurgeExpiredOperatorOperations(T0, 1000);

        Assert.IsNull(await store.GetOperatorOperation(expired.OperationId));
        Assert.IsNotNull(await store.GetOperatorOperation(live.OperationId));
    }
}
