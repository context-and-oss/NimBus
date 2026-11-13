#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The operation journal's crash and retry handling (Spec 044 Phase 2, Spec 035 Phase 2b): a
/// command a crashed request left behind is repaired from what the journal recorded before each
/// step, dispatch is confirmed from the Resolver's stored copy of the command, and nothing is
/// ever republished.
/// </summary>
[TestClass]
public sealed class OperatorOperationJournalTests
{
    private const string Endpoint = "CrmEndpoint";
    private const string EventId = "evt-1";
    private const string Session = "sess-1";
    private const string OperationId = "0123456789abcdef0123456789abcdef";

    private readonly InMemoryMessageStore _store = new();
    private readonly MutableTime _time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly RecordingAudit _audit = new();

    private OperatorOperationJournal Journal() => new([_store], _store, new CommandClient(), NullLogger<OperatorOperationJournal>.Instance, _time);

    [TestMethod]
    public void Operation_ids_are_stable_per_caller_and_key()
    {
        var a = OperatorOperationJournal.OperationIdFor("t", "caller", "key");
        Assert.AreEqual(a, OperatorOperationJournal.OperationIdFor("t", "caller", "key"));
        Assert.AreNotEqual(a, OperatorOperationJournal.OperationIdFor("t", "other", "key"));
        Assert.AreNotEqual(a, OperatorOperationJournal.OperationIdFor("u", "caller", "key"));
        Assert.AreEqual(32, a.Length);
    }

    [TestMethod]
    public void Without_a_store_or_a_command_client_the_journal_is_unavailable()
    {
        Assert.IsFalse(new OperatorOperationJournal([], _store, new CommandClient(), NullLogger<OperatorOperationJournal>.Instance).IsAvailable);
        Assert.IsFalse(new OperatorOperationJournal([_store], _store, new PlainClient(), NullLogger<OperatorOperationJournal>.Instance).IsAvailable);
        Assert.IsTrue(Journal().IsAvailable);
    }

    [TestMethod]
    public async Task A_request_that_crashed_after_the_claim_is_repaired_as_not_sent_and_the_row_restored()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Claimed);
        _time.Advance(OperatorOperationJournal.RequestLease + TimeSpan.FromSeconds(1));

        var repaired = await journal.ReconcileAsync(operation, repair: true, _audit);

        Assert.AreEqual(OperatorOperationStatus.NotSent, repaired.Status);
        Assert.IsNotNull(await _store.GetEvent(Endpoint, EventId), "The archived row is restored.");
        Assert.AreEqual(MessageAuditType.CommandNotSent, _audit.Rows.Single().Type);
        Assert.AreEqual(OperatorOperationJournal.ReconcilerAuditor, _audit.Rows.Single().Auditor);
    }

    [TestMethod]
    public async Task A_request_still_inside_its_lease_is_left_alone()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Claimed);

        var unchanged = await journal.ReconcileAsync(operation, repair: true, _audit);

        Assert.AreEqual(OperatorOperationStatus.Claimed, unchanged.Status);
        Assert.IsNull(await _store.GetEvent(Endpoint, EventId), "The row stays claimed.");
    }

    [TestMethod]
    public async Task A_read_never_repairs()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Claimed);
        _time.Advance(TimeSpan.FromHours(2));

        var read = await journal.ReconcileAsync(operation, repair: false);

        Assert.AreEqual(OperatorOperationStatus.Claimed, read.Status);
        Assert.IsNull(await _store.GetEvent(Endpoint, EventId));
    }

    [TestMethod]
    public async Task A_command_the_resolver_stored_is_dispatched_even_when_the_request_lost_the_acknowledgement()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Publishing);
        _time.Advance(OperatorOperationJournal.RequestLease + TimeSpan.FromSeconds(1));
        await StoreAsync(OperationId, MessageType.ResubmissionRequest, parent: "err-1");

        var reconciled = await journal.ReconcileAsync(operation, repair: true, _audit);

        Assert.AreEqual(OperatorOperationStatus.Dispatched, reconciled.Status);
        Assert.IsNotNull(reconciled.DispatchedAtUtc);
        Assert.AreEqual(0, _audit.Rows.Count);
    }

    [TestMethod]
    public async Task A_dispatched_command_takes_the_outcome_of_the_response_parented_to_it()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Dispatched);
        await StoreAsync("unrelated-response", MessageType.ErrorResponse, parent: "someone-else");
        await StoreAsync("handled", MessageType.ResolutionResponse, parent: OperationId);

        var reconciled = await journal.ReconcileAsync(operation, repair: true, _audit);

        Assert.AreEqual(OperatorOperationStatus.Succeeded, reconciled.Status);
        Assert.AreEqual("handled", reconciled.EvidenceMessageId);
    }

    [TestMethod]
    public async Task A_pending_hand_off_is_observed_but_not_an_outcome()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Dispatched);
        await StoreAsync("parked", MessageType.PendingHandoffResponse, parent: OperationId);

        var reconciled = await journal.ReconcileAsync(operation, repair: false);

        Assert.AreEqual(OperatorOperationStatus.Dispatched, reconciled.Status);
        Assert.AreEqual("PendingHandoffResponse", reconciled.ObservedStatus);
    }

    [TestMethod]
    public async Task A_possibly_sent_command_is_kept_until_the_grace_period_then_restored_for_a_person()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Publishing);

        // Past the request lease but inside the grace period: the Resolver may lag.
        _time.Advance(TimeSpan.FromMinutes(10));
        operation = await journal.ReconcileAsync(operation, repair: true, _audit);
        Assert.AreEqual(OperatorOperationStatus.Publishing, operation.Status);
        Assert.IsNull(await _store.GetEvent(Endpoint, EventId));

        _time.Advance(OperatorOperationJournal.PublishGrace);
        operation = await journal.ReconcileAsync(operation, repair: true, _audit);

        Assert.AreEqual(OperatorOperationStatus.OutcomeUnknown, operation.Status);
        Assert.IsNotNull(await _store.GetEvent(Endpoint, EventId), "Restored so a person can see and act on it.");

        // If the command shows up later after all, the operation follows it.
        await StoreAsync(OperationId, MessageType.ResubmissionRequest, parent: "err-1");
        operation = await journal.ReconcileAsync(operation, repair: true, _audit);
        Assert.AreEqual(OperatorOperationStatus.Dispatched, operation.Status);
    }

    [TestMethod]
    public async Task The_reconciler_visits_due_operations_through_a_scope()
    {
        var version = await SeedArchivedRowAsync();
        await CreateAsync(Journal(), version, OperatorOperationStatus.Accepted);
        _time.Advance(TimeSpan.FromMinutes(5));

        var services = new ServiceCollection();
        services.AddSingleton<IOperatorOperationStore>(_store);
        services.AddSingleton<IMessageTrackingStore>(_store);
        services.AddSingleton<IManagerClient>(new CommandClient());
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<IAuditLogService>(_audit);
        services.AddLogging();
        services.AddScoped(sp => new OperatorOperationJournal(
            sp.GetServices<IOperatorOperationStore>(), _store, sp.GetRequiredService<IManagerClient>(),
            NullLogger<OperatorOperationJournal>.Instance, _time));
        await using var provider = services.BuildServiceProvider();
        var reconciler = new OperatorOperationReconciler(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OperatorOperationReconciler>.Instance, _time);

        var visited = await reconciler.RunOnceAsync(purge: true);

        Assert.AreEqual(1, visited);
        Assert.AreEqual(OperatorOperationStatus.NotSent, (await _store.GetOperatorOperation(OperationId))!.Status);
    }

    [TestMethod]
    public async Task Concurrent_reconcilers_change_an_operation_once()
    {
        var journal = Journal();
        var version = await SeedArchivedRowAsync();
        var operation = await CreateAsync(journal, version, OperatorOperationStatus.Claimed);
        _time.Advance(TimeSpan.FromMinutes(5));

        // Both read the same version; the second write loses the race and re-reads.
        var results = await Task.WhenAll(
            journal.ReconcileAsync(operation, repair: true, _audit),
            journal.ReconcileAsync(operation, repair: true, _audit));

        Assert.IsTrue(results.All(r => r.Status == OperatorOperationStatus.NotSent));
        Assert.AreEqual(OperatorOperationStatus.NotSent, (await _store.GetOperatorOperation(OperationId))!.Status);
    }

    private async Task<OperatorMessageVersion> SeedArchivedRowAsync()
    {
        var row = await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, EventId, Session, "err-1");
        var version = OperatorMessageVersion.From(row);
        Assert.IsTrue(await _store.TryArchiveUnresolvedEvent(EventId, Session, Endpoint, version.Status, version.LastMessageId, version.UpdatedAt));
        return version;
    }

    private async Task<OperatorOperation> CreateAsync(OperatorOperationJournal journal, OperatorMessageVersion version, OperatorOperationStatus status)
    {
        var created = await journal.CreateAsync(new OperatorOperation
        {
            OperationId = OperationId,
            TenantId = "t",
            Caller = "caller",
            IdempotencyKey = Guid.NewGuid().ToString(),
            Action = OperatorOperationAction.Resubmit,
            EndpointId = Endpoint,
            EventId = EventId,
            SessionId = Session,
            SourceMessageId = "err-1",
            MessageVersion = version.Encode(),
            Reason = "test",
        });
        var operation = created.Operation;
        if (status == OperatorOperationStatus.Accepted)
            return operation;
        return await journal.ChangeAsync(operation, o => { o.Status = status; return true; });
    }

    private Task StoreAsync(string messageId, MessageType type, string parent)
        => _store.StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = messageId,
            ParentMessageId = parent,
            EndpointId = Endpoint,
            SessionId = Session,
            MessageType = type,
            EnqueuedTimeUtc = _time.GetUtcNow().UtcDateTime,
        });

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private class PlainClient : IManagerClient
    {
        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson) => Task.CompletedTask;

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId) => Task.CompletedTask;
    }

    private sealed class CommandClient : PlainClient, IManagerCommandClient
    {
        public Task Resubmit(MessageEntity errorResponse, string endpoint, string eventTypeId, string eventJson, string commandMessageId) => Task.CompletedTask;

        public Task Skip(MessageEntity errorResponse, string endpoint, string eventTypeId, string commandMessageId) => Task.CompletedTask;
    }

    private sealed record AuditRow(MessageAuditType Type, string? Auditor);

    private sealed class RecordingAudit : IAuditLogService
    {
        private readonly List<AuditRow> _rows = [];

        public IReadOnlyList<AuditRow> Rows { get { lock (_rows) return _rows.ToList(); } }

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default)
        {
            lock (_rows) _rows.Add(new AuditRow(type, auditorNameOverride));
            return Task.CompletedTask;
        }
    }
}
