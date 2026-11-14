using Azure.Messaging.ServiceBus;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

/// <summary>
/// Processes deferred messages from the session-enabled deferred subscription.
/// </summary>
public class DeferredMessageProcessor : IDeferredMessageProcessor
{
    private readonly ServiceBusClient _serviceBusClient;
    private readonly string _deferredSubscriptionName;
    private const int BatchSize = 100;

    // A session receiver, unlike a session processor, never renews its session lock, and the
    // Deferred subscription's lock is 30 s. The drain renews the lock once less than this is
    // left, which covers the 5 s receive wait and a replay's send and completion.
    private static readonly TimeSpan SessionLockRenewalMargin = TimeSpan.FromSeconds(10);

    public DeferredMessageProcessor(ServiceBusClient serviceBusClient, string? deferredSubscriptionName = null)
    {
        _serviceBusClient = serviceBusClient ?? throw new ArgumentNullException(nameof(serviceBusClient));
        _deferredSubscriptionName = deferredSubscriptionName ?? Constants.DeferredSubscriptionName;
    }

    /// <summary>
    /// Processes all deferred messages for the specified session.
    /// Messages are retrieved from the session-enabled deferred subscription using AcceptSessionAsync,
    /// sorted by DeferralSequence, and re-published to the main topic for normal processing,
    /// keeping DeferralSequence so the endpoint recognises each replay.
    /// The drain stops early when the endpoint parks one of its replays again, because the
    /// session is blocked again; that copy and everything after it stay parked for the next
    /// unblock.
    /// </summary>
    public async Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("Session ID is required", nameof(sessionId));
        if (string.IsNullOrEmpty(topicName))
            throw new ArgumentException("Topic name is required", nameof(topicName));

        using var replaySpan = NimBusActivitySources.DeferredProcessor.StartActivity(
            "NimBus.DeferredProcessor.Replay", ActivityKind.Internal);
        if (replaySpan is not null)
        {
            replaySpan.SetTag(MessagingAttributes.NimBusEndpoint, topicName);
            replaySpan.SetTag(MessagingAttributes.NimBusSessionKey, sessionId);
        }

        int totalReplayed = 0;
        try
        {
            ServiceBusSessionReceiver receiver;
            try
            {
                // Accept the specific session - only receives messages for this sessionId
                receiver = await _serviceBusClient.AcceptSessionAsync(topicName, _deferredSubscriptionName, sessionId, cancellationToken: cancellationToken);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.SessionCannotBeLocked)
            {
                // No messages for this session - nothing to process. Graceful no-op:
                // the span ends as Ok with batch_size=0 in the finally block below.
                replaySpan?.SetStatus(ActivityStatusCode.Ok);
                return;
            }

            await using (receiver)
            await using (var sender = _serviceBusClient.CreateSender(topicName))
            {
                try
                {
                    // MessageIds of this drain's replays. A parked message whose ParentMessageId
                    // is one of them is a replay the endpoint parked again.
                    var replayedMessageIds = new HashSet<string>(StringComparer.Ordinal);
                    var sessionBlockedAgain = false;

                    // Receive messages in batches until we've processed all messages for this session
                    while (!sessionBlockedAgain && !cancellationToken.IsCancellationRequested)
                    {
                        await RenewSessionLockIfExpiringAsync(receiver, cancellationToken);
                        var messages = await receiver.ReceiveMessagesAsync(BatchSize, TimeSpan.FromSeconds(5), cancellationToken);
                        if (messages == null || messages.Count == 0)
                            break;

                        // Sort by DeferralSequence to ensure FIFO ordering
                        var orderedMessages = messages.OrderBy(m => GetDeferralSequence(m)).ToList();

                        var batchTimestamp = Stopwatch.GetTimestamp();
                        var batchReplayed = 0;

                        for (var i = 0; i < orderedMessages.Count; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var message = orderedMessages[i];
                            await RenewSessionLockIfExpiringAsync(receiver, cancellationToken);

                            if (IsReplayParkedAgain(message, replayedMessageIds))
                            {
                                // A replay failed or handed off, so the session is blocked again and
                                // the endpoint parks every later replay straight back here. Draining
                                // on would replay the same messages in a tight loop until the block
                                // clears. Leave this copy and everything after it for the next unblock.
                                for (var j = i; j < orderedMessages.Count; j++)
                                    await receiver.AbandonMessageAsync(orderedMessages[j], cancellationToken: cancellationToken);

                                sessionBlockedAgain = true;
                                break;
                            }

                            // Re-publish to main topic for normal processing
                            var republishedMessage = CreateRepublishedMessage(message, sessionId, topicName);
                            replayedMessageIds.Add(republishedMessage.MessageId);
                            await sender.SendMessageAsync(republishedMessage, cancellationToken);

                            // Complete the deferred message
                            await receiver.CompleteMessageAsync(message, cancellationToken);
                            batchReplayed++;
                        }

                        if (batchReplayed > 0)
                        {
                            var batchElapsedMs = Stopwatch.GetElapsedTime(batchTimestamp).TotalMilliseconds;
                            var endpointTag = BuildEndpointTag(topicName);
                            NimBusMeters.DeferredReplayed.Add(batchReplayed, endpointTag);
                            NimBusMeters.DeferredReplayDuration.Record(batchElapsedMs, endpointTag);
                            totalReplayed += batchReplayed;
                        }

                        // A short receive does not mean the session is drained: the SDK
                        // can return a partial batch while more messages are available.
                        // Keep receiving until an empty batch confirms the drain is done.
                    }
                }
                catch (ServiceBusException ex) when (ex.IsTransient)
                {
                    throw new Core.Messages.Exceptions.TransientException("Transient error processing deferred messages", ex);
                }
            }

            replaySpan?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (replaySpan is not null)
        {
            replaySpan.SetStatus(ActivityStatusCode.Error, ex.Message);
            replaySpan.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
            throw;
        }
        finally
        {
            replaySpan?.SetTag(MessagingAttributes.NimBusDeferredBatchSize, totalReplayed);
        }
    }

    // Without renewal a drain that outlasts the lock fails partway with SessionLockLost, and a
    // lock lost between a replay's send and its completion gets that message replayed twice.
    private static async Task RenewSessionLockIfExpiringAsync(ServiceBusSessionReceiver receiver, CancellationToken cancellationToken)
    {
        if (receiver.SessionLockedUntil - DateTimeOffset.UtcNow <= SessionLockRenewalMargin)
            await receiver.RenewSessionLockAsync(cancellationToken);
    }

    private static KeyValuePair<string, object?>[] BuildEndpointTag(string endpoint) =>
        new[] { new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint) };

    private static int GetDeferralSequence(ServiceBusReceivedMessage message)
    {
        if (message.ApplicationProperties.TryGetValue(UserPropertyName.DeferralSequence.ToString(), out var value))
        {
            if (value is int intValue)
                return intValue;
            if (int.TryParse(value?.ToString(), out var parsed))
                return parsed;
        }
        return 0;
    }

    // Only a replay the endpoint parked again carries one of this drain's replay ids: the
    // endpoint stamps the id of the delivery it parks as the parked copy's ParentMessageId.
    private static bool IsReplayParkedAgain(ServiceBusReceivedMessage message, HashSet<string> replayedMessageIds) =>
        message.ApplicationProperties.TryGetValue(UserPropertyName.ParentMessageId.ToString(), out var parentMessageId)
        && parentMessageId?.ToString() is { } id
        && replayedMessageIds.Contains(id);

    private static Azure.Messaging.ServiceBus.ServiceBusMessage CreateRepublishedMessage(ServiceBusReceivedMessage deferredMessage, string sessionId, string topicName)
    {
        var result = new Azure.Messaging.ServiceBus.ServiceBusMessage(deferredMessage.Body)
        {
            // Our own id instead of the broker's, so the drain can recognise this replay if
            // the endpoint parks it again (see IsReplayParkedAgain).
            MessageId = Guid.NewGuid().ToString(),
        };

        // Copy all application properties except OriginalSessionId, which the Deferred
        // subscription's filter matches on. DeferralSequence stays: it marks the delivery as
        // a replay, so the endpoint lets it through while newer events wait behind it.
        // We also drop "To" because the inbound deferred message's To is "Deferred"
        // (set by SendToDeferredSubscription so the message routes to the Deferred
        // subscription on the topic). Re-using that value would route the republish
        // back into the Deferred subscription instead of the main endpoint.
        foreach (var prop in deferredMessage.ApplicationProperties)
        {
            if (prop.Key != UserPropertyName.OriginalSessionId.ToString() &&
                prop.Key != UserPropertyName.To.ToString())
            {
                result.ApplicationProperties[prop.Key] = prop.Value;
            }
        }

        // Restore To to the destination endpoint so the main subscription's
        // `user.To = '<endpointId>'` filter matches. In NimBus's topology, topic
        // name equals endpoint name (one topic per endpoint).
        result.ApplicationProperties[UserPropertyName.To.ToString()] = topicName;

        // Set the session ID so it goes back to the correct session
        result.SessionId = sessionId;
        result.CorrelationId = deferredMessage.CorrelationId;

        return result;
    }
}
