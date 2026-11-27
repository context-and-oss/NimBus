# Outbox: poison sessions, retry backoff and multi-replica claiming

Branch: `fix/outbox-poison-session-and-claiming` (from master `0c51e8fe`).

## Problem

1. **A poison session starves dispatch.** `OutboxDispatcher.DispatchPendingAsync` asks
   `IOutbox.GetPendingAsync` for the oldest `TOP (batchSize)` rows on every poll. When session S
   has a row that always fails, followed by at least `batchSize - 1` more rows of S, every poll
   returns the same rows. S's later rows are skipped (correctly, to keep FIFO), but no other
   session is ever fetched. Nothing counts attempts, backs off or quarantines the row, which
   contradicts the dispatcher comment that one poison row cannot stall other sessions.
2. **Two dispatcher replicas send the same rows.** `GetPendingAsync` uses
   `WITH (UPDLOCK, READPAST)` in an autocommit statement, so the locks are released as soon as
   the `SELECT` ends. Two replicas read the same rows and send them interleaved
   (1, 2, 1, 2) before either marks them dispatched: duplicates and broken session order.
3. **Unbounded purge.** `PurgeDispatchedAsync` runs one `DELETE` over every dispatched row older
   than the cutoff, which can hold a large lock (and escalate to a table lock) and grow the log.

## Design

### New optional companion interface `IOutboxClaiming` (NimBus.Core.Outbox)

Adding members to `IOutbox` would break every existing implementation, so claiming is an
optional companion, the same pattern as `IOutboxCleanup` and `IOutboxMetricsQuery`. All time
arithmetic happens on the store's clock (callers pass durations, not timestamps), so dispatcher
replicas with skewed clocks agree on lease and retry expiry.

- `ClaimPendingAsync(int batchSize, string claimantId, TimeSpan leaseDuration, ct)` returns up to
  `batchSize` rows in dispatch order and leases them atomically to `claimantId`. A row is
  claimable when it is not dispatched, not parked, its retry time has passed and it has no
  unexpired lease, **and** no *other* pending row of the same session is parked, backing off or
  leased. Session-less rows are independent. A blocked session therefore contributes no rows
  to the batch, so it cannot crowd out other sessions, and a session's later rows can never be
  sent past its failed row.
- `RecordDispatchFailureAsync(string id, string claimantId, string error, TimeSpan retryDelay, bool park, ct)`
  increments `AttemptCount`, stores `LastError`, sets `NextAttemptAtUtc = now + retryDelay`
  (or `ParkedAtUtc = now` when `park`), and releases the lease. Only the current lease holder's
  report is applied.
- `ReleaseClaimsAsync(string claimantId, IEnumerable<string> ids, ct)` drops leases on rows the
  claimant did not attempt (rows queued behind a failed row of the same session, or rows left
  when the lease budget ran out).
- `OutboxMessage.AttemptCount` (new, additive) carries the previous attempt count to the
  dispatcher.

### Dispatcher (`OutboxDispatcher` + new `OutboxDispatcherOptions`)

- Options: `MaxAttempts` (default 20), `InitialRetryDelay` (1 s), `MaxRetryDelay` (5 min),
  `LeaseDuration` (2 min), `DispatcherId` (default: machine name, process id and a random
  suffix). Existing constructors keep working with the defaults; new constructor takes options.
- With an `IOutboxClaiming` store: claim, send in order, block a session after its first failure
  in the poll, record the failure with exponential backoff
  (`min(InitialRetryDelay * 2^(attempt-1), MaxRetryDelay)`), park when
  `attempt >= MaxAttempts` or when the payload cannot be deserialized (deterministic failure).
  Stop starting new sends once half the lease has elapsed so the lease does not expire under an
  in-flight batch; release the unattempted rest. Order of bookkeeping after the sends: mark
  dispatched (most important), record failures, release claims; the last two are best-effort
  and logged, because an unreleased lease simply expires.
- Without `IOutboxClaiming` (custom stores, test fakes): the previous behaviour, plus a one-time
  warning that poison rows are not parked and only one dispatcher instance may run.
- Observability: `nimbus.outbox.parked` counter; warning log per failed attempt with attempt
  number and retry delay; error log on park. Pending gauges keep counting parked rows (they are
  undelivered), so a parked row also shows up as growing `nimbus.outbox.dispatch_lag`.
- SDK: `AddNimBusOutboxDispatcher(Action<OutboxDispatcherOptions> configure, TimeSpan? pollingInterval, int batchSize)` overload.

### SQL Server (`SqlServerOutbox`)

- Implements `IOutboxClaiming`.
- Schema, added by `EnsureTableExistsAsync` (the existing idempotent migration path), all
  nullable so the `ALTER TABLE ADD` is metadata-only on every edition and existing rows need no
  rewrite: `AttemptCount INT NULL` (NULL = 0), `NextAttemptAtUtc DATETIME2 NULL`,
  `LastError NVARCHAR(2000) NULL`, `ParkedAtUtc DATETIME2 NULL`, `LeaseUntilUtc DATETIME2 NULL`,
  `ClaimedBy NVARCHAR(256) NULL`. New filtered index
  `IX_OutboxMessages_PendingSession (SessionId) INCLUDE (ParkedAtUtc, NextAttemptAtUtc, LeaseUntilUtc) WHERE DispatchedAtUtc IS NULL`
  for the session-blocked check.
- Claim: one transaction that takes an exclusive `sp_getapplock` on the table, then
  `UPDATE` through a `TOP (@BatchSize) ... ORDER BY SequenceNumber, CreatedAtUtc` CTE setting
  `LeaseUntilUtc`/`ClaimedBy`, `OUTPUT inserted.*`. The app lock serialises claimers so the
  session-blocked check always sees the other claimer's committed leases, under both locking
  READ COMMITTED and READ_COMMITTED_SNAPSHOT, and claimers cannot deadlock on each other's row
  locks. Claims are short; publishers' inserts and checkpoints do not take the app lock.
  `OUTPUT` order is undefined, so rows are re-sorted client-side by dispatch order.
- `GetPendingAsync` keeps its contract (non-claiming read of pending rows, now documented as such).
- `RequeueParkedAsync(string? id)` (SQL-only public helper) clears parking and attempts so an
  operator can retry parked rows after fixing the cause.
- `PurgeDispatchedAsync` loops `DELETE TOP (@PurgeBatchSize)` (new option, default 1000, below
  the 5000-lock escalation threshold) until a batch deletes fewer rows, honouring cancellation
  between batches, and returns the total.

### Compatibility

- Public API is additive only. `IOutbox` is unchanged.
- Rolling upgrade: old dispatchers ignore the new columns and leases, so while old and new
  replicas run side by side the old ones can still double-send (at-least-once, as today).
  Parking by the new version is respected only by new versions.

## Tests

- `NimBus.Core.Tests` (in-memory claiming outbox implementing `IOutboxClaiming`):
  - batchSize 10, session A with 10 rows whose first always fails plus 1 row in session B:
    B is dispatched within 2 polls.
  - The poison row is parked after `MaxAttempts` attempts and stays parked; the session's later
    rows are never sent; other sessions continue.
  - Session order is preserved after a transient failure recovers.
  - Backoff grows exponentially and is capped; a payload that cannot be deserialized parks at once.
  - Rows skipped behind a failed row are released.
- Conformance (`NimBus.Testing.Conformance.OutboxClaimingConformanceTests`, run for in-memory
  and SQL Server): claimed rows are invisible to a second claimant, expired leases are
  re-claimable, a blocked session contributes no rows, failure/park/release semantics.
- SQL Server live tests (skip without `NIMBUS_SQL_TEST_CONNECTION`): two concurrent dispatchers
  send each row exactly once and keep per-session order; migration of a table created by the
  previous version; batched purge.
