#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Inbox;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic behavioral tests for <see cref="IInboxStore"/> implementations.
/// </summary>
[TestClass]
public abstract class InboxStoreConformanceTests
{
    private readonly string _scope = $"inbox-ct-{Guid.NewGuid():N}";

    private string EndpointId => $"{_scope}-endpoint";

    /// <summary>
    /// Creates an inbox store for the current test.
    /// </summary>
    /// <returns>The isolated store under test.</returns>
    protected abstract Task<IInboxStore> CreateStoreAsync();

    /// <summary>
    /// Advances the provider clock past the first record and returns a cutoff between
    /// the first and second records. Providers backed by a controllable clock should
    /// override this hook rather than waiting in real time.
    /// </summary>
    /// <returns>A cutoff later than the first persisted timestamp.</returns>
    protected virtual async Task<DateTimeOffset> AdvancePastFirstRecordAsync()
    {
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Verifies the basic not-seen, record, seen transition.
    /// </summary>
    [TestMethod]
    public async Task HasProcessedAsync_transitions_from_false_to_true_after_record()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("transition");

        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, messageId));

        await store.RecordProcessedAsync(EndpointId, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, messageId));
    }

    /// <summary>
    /// Verifies repeated and concurrent records are idempotent.
    /// </summary>
    [TestMethod]
    public async Task RecordProcessedAsync_is_idempotent_when_repeated_concurrently()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("concurrent");

        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => store.RecordProcessedAsync(EndpointId, messageId)));
        await store.RecordProcessedAsync(EndpointId, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, messageId));
    }

    /// <summary>
    /// Verifies a record for one endpoint never marks the message processed for another.
    /// NimBus fan-out forwards copies of one published message — same broker MessageId — to
    /// every subscribed endpoint, so endpoints sharing one physical store must be isolated.
    /// </summary>
    [TestMethod]
    public async Task Record_for_one_endpoint_does_not_mark_other_endpoints_processed()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("fan-out");
        var billingEndpoint = $"{_scope}-billing";
        var shippingEndpoint = $"{_scope}-shipping";

        await store.RecordProcessedAsync(billingEndpoint, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(billingEndpoint, messageId));
        Assert.IsFalse(await store.HasProcessedAsync(shippingEndpoint, messageId));

        await store.RecordProcessedAsync(shippingEndpoint, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(shippingEndpoint, messageId));
    }

    /// <summary>
    /// Verifies message identifiers are opaque, case-sensitive keys across providers.
    /// </summary>
    [TestMethod]
    public async Task Message_identifiers_are_case_sensitive()
    {
        var store = await CreateStoreAsync();
        var upperCaseMessageId = NewMessageId("Case-Sensitive");
        var lowerCaseMessageId = upperCaseMessageId.ToLowerInvariant();

        await store.RecordProcessedAsync(EndpointId, upperCaseMessageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, upperCaseMessageId));
        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, lowerCaseMessageId));

        await store.RecordProcessedAsync(EndpointId, lowerCaseMessageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, lowerCaseMessageId));
    }

    /// <summary>
    /// Verifies identifiers differing only by trailing whitespace stay distinct. Broker message
    /// ids are opaque byte-exact keys; a provider whose native string comparison pads trailing
    /// spaces (SQL Server NVARCHAR equality does, regardless of collation) would otherwise
    /// silently skip the distinct id "m1 " after "m1" was recorded.
    /// </summary>
    [TestMethod]
    public async Task Identifiers_differing_only_by_trailing_whitespace_are_distinct()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("padded");
        var paddedMessageId = messageId + " ";
        var paddedEndpointId = EndpointId + " ";

        await store.RecordProcessedAsync(EndpointId, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, messageId));
        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, paddedMessageId));
        Assert.IsFalse(await store.HasProcessedAsync(paddedEndpointId, messageId));

        await store.RecordProcessedAsync(EndpointId, paddedMessageId);
        await store.RecordProcessedAsync(paddedEndpointId, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, paddedMessageId));
        Assert.IsTrue(await store.HasProcessedAsync(paddedEndpointId, messageId));
        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, messageId));
    }

    /// <summary>
    /// Verifies identifiers at the documented maximum lengths (260-character endpoint,
    /// 512-character message id) round-trip on every provider, including providers that key
    /// on a derived identity hash rather than the raw identifier columns.
    /// </summary>
    [TestMethod]
    public async Task Maximum_length_identifiers_are_recorded_and_found()
    {
        var store = await CreateStoreAsync();
        var endpointId = $"{_scope}-max".PadRight(260, 'e');
        var messageId = NewMessageId("max").PadRight(512, 'm');

        Assert.AreEqual(260, endpointId.Length);
        Assert.AreEqual(512, messageId.Length);
        Assert.IsFalse(await store.HasProcessedAsync(endpointId, messageId));

        await store.RecordProcessedAsync(endpointId, messageId);
        await store.RecordProcessedAsync(endpointId, messageId);

        Assert.IsTrue(await store.HasProcessedAsync(endpointId, messageId));
        Assert.IsFalse(await store.HasProcessedAsync(endpointId, messageId[..511]));
    }

    /// <summary>
    /// Verifies cleanup removes only expired rows, preserves the original timestamp
    /// after repeated records, and is a no-op when no expired rows remain.
    /// </summary>
    [TestMethod]
    public async Task PurgeExpiredAsync_removes_expired_rows_and_retains_newer_rows()
    {
        var store = await CreateStoreAsync();
        var expiredMessageId = NewMessageId("expired");
        var retainedMessageId = NewMessageId("retained");

        await store.RecordProcessedAsync(EndpointId, expiredMessageId);
        var cutoff = await AdvancePastFirstRecordAsync();

        // A repeated record must not replace the first timestamp. If it did, the
        // expired row would incorrectly survive this purge.
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => store.RecordProcessedAsync(EndpointId, expiredMessageId)));
        await store.RecordProcessedAsync(EndpointId, retainedMessageId);

        Assert.AreEqual(1, await store.PurgeExpiredAsync(EndpointId, cutoff));
        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, expiredMessageId));
        Assert.IsTrue(await store.HasProcessedAsync(EndpointId, retainedMessageId));
        Assert.AreEqual(0, await store.PurgeExpiredAsync(EndpointId, cutoff));
    }

    /// <summary>
    /// Verifies cleanup is scoped to the requested endpoint. Retention is configured per
    /// subscriber, so one endpoint's purge must never delete another endpoint's records from a
    /// shared store — including an endpoint differing only by trailing whitespace, which a
    /// provider with padding string comparisons would otherwise sweep along.
    /// </summary>
    [TestMethod]
    public async Task PurgeExpiredAsync_is_scoped_to_the_requested_endpoint()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("purge-scope");
        var otherEndpoint = $"{_scope}-other";
        var paddedEndpoint = EndpointId + " ";

        await store.RecordProcessedAsync(EndpointId, messageId);
        await store.RecordProcessedAsync(otherEndpoint, messageId);
        await store.RecordProcessedAsync(paddedEndpoint, messageId);
        var cutoff = await AdvancePastFirstRecordAsync();

        Assert.AreEqual(1, await store.PurgeExpiredAsync(EndpointId, cutoff));
        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, messageId));
        Assert.IsTrue(
            await store.HasProcessedAsync(otherEndpoint, messageId),
            "Another endpoint's records must survive this endpoint's purge.");
        Assert.IsTrue(
            await store.HasProcessedAsync(paddedEndpoint, messageId),
            "An endpoint differing only by trailing whitespace must survive this endpoint's purge.");
    }

    /// <summary>
    /// Verifies pre-cancelled operations fail before reading or mutating state.
    /// </summary>
    [TestMethod]
    public async Task Operations_honor_pre_cancelled_tokens_without_recording()
    {
        var store = await CreateStoreAsync();
        var messageId = NewMessageId("cancelled");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.HasProcessedAsync(EndpointId, messageId, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.RecordProcessedAsync(EndpointId, messageId, cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => store.PurgeExpiredAsync(EndpointId, DateTimeOffset.MaxValue, cancellation.Token));

        Assert.IsFalse(await store.HasProcessedAsync(EndpointId, messageId));
    }

    private string NewMessageId(string suffix) => $"{_scope}-{suffix}";
}
