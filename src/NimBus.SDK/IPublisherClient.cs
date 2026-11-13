using System;
using System.Collections.Generic;
using System.Threading;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK.EventHandlers;
using System.Threading.Tasks;

namespace NimBus.SDK;

public interface IPublisherClient
{
    Task Publish(IEvent @event);

    /// <summary>
    /// Publishes a pre-built, dynamically-typed message (no compiled IEvent). The caller sets
    /// EventTypeId + MessageContent.EventContent. Used by the agent REST API (spec 022).
    /// The message is sent as built: to route it by key (Spec 045), the caller sets
    /// <see cref="Message.RoutingKey"/> itself.
    /// </summary>
    Task Publish(IMessage message, CancellationToken cancellationToken = default);

    Task Publish(IEvent @event, string sessionId, string correlationId);

    Task Publish(IEvent @event, string sessionId, string correlationId, string? messageId);

    /// <summary>
    /// Publishes an event like <see cref="Publish(IEvent, string, string, string)"/>, and
    /// passes <paramref name="cancellationToken"/> to the underlying send so a caller can
    /// abandon a publish that is still in flight.
    /// </summary>
    /// <param name="event">The event to publish.</param>
    /// <param name="sessionId">Session id for the message.</param>
    /// <param name="correlationId">Correlation id for the message.</param>
    /// <param name="messageId">Message id, or <c>null</c> to derive the deterministic id.</param>
    /// <param name="cancellationToken">Cancels the publish.</param>
    /// <remarks>
    /// The default implementation checks the token and then delegates to the overload
    /// without a token, so existing implementers keep compiling; it cannot cancel a send
    /// that has already started. <see cref="PublisherClient"/> passes the token to the send.
    /// </remarks>
    Task Publish(IEvent @event, string sessionId, string correlationId, string messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Publish(@event, sessionId, correlationId, messageId);
    }

    /// <summary>
    /// Publishes a workflow follow-up using the inbound handler context's session,
    /// correlation, and lineage metadata. The outgoing parent is the inbound
    /// <see cref="IEventHandlerContext.MessageId"/>. The originating message is
    /// preserved across later hops and falls back to the inbound message for a
    /// first hop whose legacy lineage is absent.
    /// </summary>
    /// <param name="event">The command or event to publish.</param>
    /// <param name="context">The context of the inbound message causing the follow-up.</param>
    /// <param name="messageId">
    /// An explicit deterministic identifier for the logical transition. Derive it
    /// from durable workflow identity, transition name, and version or attempt, and
    /// reproduce the same value when retrying that transition.
    /// </param>
    /// <param name="cancellationToken">Cancellation token propagated to the configured sender.</param>
    /// <returns>A task that completes when the message has been sent or stored in the outbox.</returns>
    /// <exception cref="NotSupportedException">
    /// The publisher implementation does not support context-aware publishing.
    /// </exception>
    Task PublishFromContext(
        IEvent @event,
        IEventHandlerContext context,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Context-aware workflow publishing requires a compatible publisher implementation. Use PublisherClient or update the custom IPublisherClient implementation.");
    }

    /// <summary>
    /// Pre-release - use with care!
    /// Publish multiple messages at once. Make sure batch size enforced by Azure Service Bus is taken into account.
    /// </summary>
    /// <param name="events">List of events you want to publish. Make sure to make them before publishing</param>
    /// <param name="correlationId"></param>
    /// <returns></returns>
    Task PublishBatch(IEnumerable<IEvent> events, string? correlationId = null);
    /// <summary>
    /// Use to get batch of maximum possible size supported by Azure Service Bus
    /// </summary>
    /// <param name="events">Events you want to split into multiple batches</param>
    /// <returns>Batches of events</returns>
    IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events);

    /// <summary>
    /// Publishes any number of events, automatically split into pages that
    /// fit the Azure Service Bus batch size. Preferred over
    /// <see cref="GetBatches"/> + <see cref="PublishBatch"/> — the
    /// <see cref="PublisherClient"/> implementation builds and serializes
    /// each event exactly once. This default implementation delegates to
    /// GetBatches + PublishBatch so existing test doubles keep working.
    /// </summary>
    /// <param name="events">Events to publish, in order.</param>
    /// <param name="correlationId">Correlation id applied to every message; a new GUID when null.</param>
    async Task PublishBatches(IEnumerable<IEvent> events, string? correlationId = null)
    {
        foreach (var batch in GetBatches(new List<IEvent>(events)))
        {
            await PublishBatch(batch, correlationId);
        }
    }

    /// <summary>
    /// Sends a request and awaits a typed response with timeout.
    /// Uses Azure Service Bus sessions for reply correlation.
    /// </summary>
    Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TRequest : IEvent
        where TResponse : class
    {
        throw new NotSupportedException("Request/response requires a ServiceBusClient. Use PublisherClient with a ServiceBusClient constructor.");
    }
}
