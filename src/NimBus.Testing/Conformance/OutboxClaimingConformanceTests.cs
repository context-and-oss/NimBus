#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IOutboxClaiming"/>: leases, session
/// blocking, retry backoff, parking and releases. Derive a fixture whose
/// <see cref="CreateOutboxAsync"/> returns an empty outbox that implements both
/// <see cref="IOutbox"/> and <see cref="IOutboxClaiming"/>.
/// </summary>
[TestClass]
public abstract class OutboxClaimingConformanceTests
{
    private static readonly TimeSpan LongLease = TimeSpan.FromMinutes(10);
    private readonly string _scope = $"cl-{Guid.NewGuid():N}"[..16];
    private int _order;

    /// <summary>
    /// Creates an isolated, empty outbox for the current test. It must implement
    /// <see cref="IOutboxClaiming"/>.
    /// </summary>
    /// <returns>The outbox under test.</returns>
    protected abstract Task<IOutbox> CreateOutboxAsync();

    /// <summary>
    /// Lets at least <paramref name="duration"/> pass on the provider's clock. Providers with a
    /// real clock wait; deterministic providers should advance their test clock instead.
    /// </summary>
    /// <param name="duration">The minimum time that must pass.</param>
    /// <returns>A task that completes once the time has passed.</returns>
    protected virtual Task AdvanceClockAsync(TimeSpan duration) =>
        Task.Delay(duration + TimeSpan.FromMilliseconds(250));

    /// <summary>
    /// Verifies that claimed rows come back in store order and are invisible to another claimant
    /// while leased.
    /// </summary>
    [TestMethod]
    public async Task ClaimPendingAsync_leases_rows_in_store_order_and_hides_them_from_other_claimants()
    {
        var (outbox, claiming) = await CreateAsync();
        var first = CreateMessage("s1");
        var second = CreateMessage("s2");
        var third = CreateMessage("s3");
        await outbox.StoreBatchAsync([first, second, third]);

        var claimedByA = await claiming.ClaimPendingAsync(2, "claimant-a", LongLease);
        var claimedByB = await claiming.ClaimPendingAsync(10, "claimant-b", LongLease);

        CollectionAssert.AreEqual(new[] { first.Id, second.Id }, Ids(claimedByA));
        Assert.IsTrue(claimedByA.All(message => message.AttemptCount == 0));
        CollectionAssert.AreEqual(new[] { third.Id }, Ids(claimedByB));
        Assert.AreEqual(0, (await claiming.ClaimPendingAsync(10, "claimant-c", LongLease)).Count);
    }

    /// <summary>
    /// Verifies that an expired lease makes the row claimable again, by anyone.
    /// </summary>
    [TestMethod]
    public async Task ClaimPendingAsync_reclaims_rows_whose_lease_expired()
    {
        var (outbox, claiming) = await CreateAsync();
        var message = CreateMessage("s1");
        await outbox.StoreAsync(message);

        var lease = TimeSpan.FromSeconds(1);
        Assert.AreEqual(1, (await claiming.ClaimPendingAsync(10, "claimant-a", lease)).Count);
        Assert.AreEqual(0, (await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)).Count);

        await AdvanceClockAsync(lease);

        CollectionAssert.AreEqual(new[] { message.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));
    }

    /// <summary>
    /// Verifies that a session with a leased row contributes no rows to another claim, while
    /// other sessions are still handed out.
    /// </summary>
    [TestMethod]
    public async Task ClaimPendingAsync_skips_every_row_of_a_session_with_a_leased_row()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        var a2 = CreateMessage("A");
        var a3 = CreateMessage("A");
        var b1 = CreateMessage("B");
        await outbox.StoreBatchAsync([a1, a2, a3, b1]);

        CollectionAssert.AreEqual(new[] { a1.Id }, Ids(await claiming.ClaimPendingAsync(1, "claimant-a", LongLease)));

        CollectionAssert.AreEqual(new[] { b1.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));
    }

    /// <summary>
    /// Verifies that a recorded failure counts an attempt, keeps the row and its session out of
    /// claims until the retry delay has passed, and then hands the session out again in order.
    /// </summary>
    [TestMethod]
    public async Task RecordDispatchFailureAsync_backs_off_the_row_and_its_session()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        var a2 = CreateMessage("A");
        var b1 = CreateMessage("B");
        await outbox.StoreBatchAsync([a1, a2, b1]);

        CollectionAssert.AreEqual(new[] { a1.Id, a2.Id, b1.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-a", LongLease)));
        await outbox.MarkAsDispatchedAsync(b1.Id);
        var retryDelay = TimeSpan.FromSeconds(2);
        await claiming.RecordDispatchFailureAsync(a1.Id, "claimant-a", "boom", retryDelay, park: false);
        await claiming.ReleaseClaimsAsync("claimant-a", [a2.Id]);

        Assert.AreEqual(0, (await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)).Count, "A1 waits for its retry and A2 waits behind it.");

        await AdvanceClockAsync(retryDelay);

        var retried = await claiming.ClaimPendingAsync(10, "claimant-b", LongLease);
        CollectionAssert.AreEqual(new[] { a1.Id, a2.Id }, Ids(retried));
        Assert.AreEqual(1, retried[0].AttemptCount);
        Assert.AreEqual(0, retried[1].AttemptCount);
    }

    /// <summary>
    /// Verifies that a parked row is never claimed again and holds back the rest of its session,
    /// but not other sessions.
    /// </summary>
    [TestMethod]
    public async Task RecordDispatchFailureAsync_with_park_blocks_the_session_until_requeued()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        var a2 = CreateMessage("A");
        await outbox.StoreBatchAsync([a1, a2]);

        Assert.AreEqual(2, (await claiming.ClaimPendingAsync(10, "claimant-a", LongLease)).Count);
        await claiming.RecordDispatchFailureAsync(a1.Id, "claimant-a", "poison", TimeSpan.Zero, park: true);
        await claiming.ReleaseClaimsAsync("claimant-a", [a2.Id]);
        var b1 = CreateMessage("B");
        await outbox.StoreAsync(b1);

        await AdvanceClockAsync(TimeSpan.FromSeconds(1));

        CollectionAssert.AreEqual(new[] { b1.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));
    }

    /// <summary>
    /// Verifies that failure reports and releases from a claimant that does not hold the lease
    /// are ignored.
    /// </summary>
    [TestMethod]
    public async Task Failure_reports_and_releases_from_another_claimant_are_ignored()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        await outbox.StoreAsync(a1);
        Assert.AreEqual(1, (await claiming.ClaimPendingAsync(10, "claimant-a", LongLease)).Count);

        await claiming.RecordDispatchFailureAsync(a1.Id, "claimant-b", "not mine", TimeSpan.FromMinutes(30), park: true);
        await claiming.ReleaseClaimsAsync("claimant-b", [a1.Id]);
        Assert.AreEqual(0, (await claiming.ClaimPendingAsync(10, "claimant-c", LongLease)).Count, "The lease of claimant-a still holds.");

        await claiming.ReleaseClaimsAsync("claimant-a", [a1.Id]);
        var reclaimed = await claiming.ClaimPendingAsync(10, "claimant-c", LongLease);
        CollectionAssert.AreEqual(new[] { a1.Id }, Ids(reclaimed));
        Assert.AreEqual(0, reclaimed[0].AttemptCount, "The foreign failure report must not count an attempt.");
    }

    /// <summary>
    /// Verifies that rows without a session are blocked only by their own state.
    /// </summary>
    [TestMethod]
    public async Task Rows_without_a_session_fail_independently()
    {
        var (outbox, claiming) = await CreateAsync();
        var first = CreateMessage(sessionId: null);
        var second = CreateMessage(sessionId: null);
        await outbox.StoreBatchAsync([first, second]);

        Assert.AreEqual(2, (await claiming.ClaimPendingAsync(10, "claimant-a", LongLease)).Count);
        await claiming.RecordDispatchFailureAsync(first.Id, "claimant-a", "boom", TimeSpan.FromMinutes(30), park: false);
        await claiming.ReleaseClaimsAsync("claimant-a", [second.Id]);

        CollectionAssert.AreEqual(new[] { second.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));
    }

    /// <summary>
    /// Verifies that dispatched rows are never claimed and cannot be failed or released back.
    /// </summary>
    [TestMethod]
    public async Task Dispatched_rows_are_not_claimed_failed_or_released()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        var a2 = CreateMessage("A");
        await outbox.StoreBatchAsync([a1, a2]);
        Assert.AreEqual(2, (await claiming.ClaimPendingAsync(10, "claimant-a", LongLease)).Count);

        await outbox.MarkAsDispatchedAsync(a1.Id);
        await claiming.RecordDispatchFailureAsync(a1.Id, "claimant-a", "late", TimeSpan.FromMinutes(30), park: true);
        await claiming.ReleaseClaimsAsync("claimant-a", [a1.Id, a2.Id]);

        CollectionAssert.AreEqual(new[] { a2.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));
    }

    /// <summary>
    /// Verifies that the fenced checkpoint marks the claimant's rows dispatched, is idempotent,
    /// and leaves rows claimed by someone else untouched.
    /// </summary>
    [TestMethod]
    public async Task MarkClaimedAsDispatchedAsync_checkpoints_only_the_claimants_rows()
    {
        var (outbox, claiming) = await CreateAsync();
        var mine = CreateMessage("A");
        var theirs = CreateMessage("B");
        await outbox.StoreBatchAsync([mine, theirs]);
        Assert.AreEqual(1, (await claiming.ClaimPendingAsync(1, "claimant-a", LongLease)).Count);
        Assert.AreEqual(1, (await claiming.ClaimPendingAsync(1, "claimant-b", LongLease)).Count);

        var checkpointed = await claiming.MarkClaimedAsDispatchedAsync("claimant-a", [mine.Id, theirs.Id]);

        CollectionAssert.AreEqual(new[] { mine.Id }, checkpointed.ToArray());
        CollectionAssert.AreEqual(new[] { theirs.Id }, Ids(await outbox.GetPendingAsync(10)));
        CollectionAssert.AreEqual(new[] { mine.Id }, (await claiming.MarkClaimedAsDispatchedAsync("claimant-a", [mine.Id])).ToArray(), "Repeating the checkpoint succeeds.");
    }

    /// <summary>
    /// Verifies the fence after a lease expired and another claimant took the rows over: the
    /// previous claimant's checkpoint, failure report and release are all ignored, and the new
    /// claimant's checkpoint applies.
    /// </summary>
    [TestMethod]
    public async Task A_claimant_whose_lease_was_taken_over_can_no_longer_checkpoint_fail_or_release_the_rows()
    {
        var (outbox, claiming) = await CreateAsync();
        var a1 = CreateMessage("A");
        var a2 = CreateMessage("A");
        await outbox.StoreBatchAsync([a1, a2]);
        var lease = TimeSpan.FromSeconds(1);
        Assert.AreEqual(2, (await claiming.ClaimPendingAsync(10, "claimant-a", lease)).Count);
        await AdvanceClockAsync(lease);
        CollectionAssert.AreEqual(new[] { a1.Id, a2.Id }, Ids(await claiming.ClaimPendingAsync(10, "claimant-b", LongLease)));

        Assert.AreEqual(0, (await claiming.MarkClaimedAsDispatchedAsync("claimant-a", [a1.Id])).Count, "A's checkpoint is fenced out.");
        await claiming.RecordDispatchFailureAsync(a2.Id, "claimant-a", "stale", TimeSpan.FromMinutes(30), park: true);
        await claiming.ReleaseClaimsAsync("claimant-a", [a1.Id, a2.Id]);

        CollectionAssert.AreEqual(new[] { a1.Id, a2.Id }, Ids(await outbox.GetPendingAsync(10)), "Nothing was marked dispatched by A.");
        Assert.AreEqual(0, (await claiming.ClaimPendingAsync(10, "claimant-c", LongLease)).Count, "B's lease still holds.");
        CollectionAssert.AreEqual(new[] { a1.Id }, (await claiming.MarkClaimedAsDispatchedAsync("claimant-b", [a1.Id])).ToArray());
        await claiming.ReleaseClaimsAsync("claimant-b", [a2.Id]);
        var reclaimed = await claiming.ClaimPendingAsync(10, "claimant-c", LongLease);
        CollectionAssert.AreEqual(new[] { a2.Id }, Ids(reclaimed));
        Assert.AreEqual(0, reclaimed[0].AttemptCount, "A's stale failure report must not count an attempt or park the row.");
    }

    /// <summary>
    /// Verifies that a claimant whose lease expired, but whose rows nobody claimed since, can
    /// still checkpoint them: no one else can have sent them.
    /// </summary>
    [TestMethod]
    public async Task MarkClaimedAsDispatchedAsync_applies_after_the_lease_expired_when_nobody_reclaimed_the_row()
    {
        var (outbox, claiming) = await CreateAsync();
        var message = CreateMessage("A");
        await outbox.StoreAsync(message);
        var lease = TimeSpan.FromSeconds(1);
        Assert.AreEqual(1, (await claiming.ClaimPendingAsync(10, "claimant-a", lease)).Count);
        await AdvanceClockAsync(lease);

        CollectionAssert.AreEqual(new[] { message.Id }, (await claiming.MarkClaimedAsDispatchedAsync("claimant-a", [message.Id])).ToArray());
        Assert.AreEqual(0, (await outbox.GetPendingAsync(10)).Count);
    }

    private async Task<(IOutbox Outbox, IOutboxClaiming Claiming)> CreateAsync()
    {
        var outbox = await CreateOutboxAsync();
        if (outbox is not IOutboxClaiming claiming)
        {
            Assert.Fail($"{outbox.GetType().Name} does not implement {nameof(IOutboxClaiming)}.");
            throw new InvalidOperationException();
        }

        return (outbox, claiming);
    }

    private static string[] Ids(System.Collections.Generic.IReadOnlyList<OutboxMessage> messages) =>
        messages.Select(message => message.Id).ToArray();

    private OutboxMessage CreateMessage(string? sessionId)
    {
        var order = _order++;
        return new OutboxMessage
        {
            Id = $"{_scope}-{order:D4}",
            MessageId = $"{_scope}-message-{order}",
            To = "conformance-endpoint",
            EventTypeId = "ConformanceEvent",
            SessionId = sessionId,
            CorrelationId = $"correlation-{order}",
            Payload = $"{{\"order\":{order}}}",
            CreatedAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(order),
        };
    }
}
