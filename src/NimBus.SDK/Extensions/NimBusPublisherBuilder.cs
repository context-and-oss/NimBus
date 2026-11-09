using System;
using NimBus.Core.Events;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Fluent configuration surface for a publisher registration. Lets a host declare AsyncAPI
/// enrichment per event type — an alternative to <see cref="AsyncApiMessageAttribute"/> — that
/// the exporter merges into the generated document. Recording metadata only; it never changes
/// the send path.
/// </summary>
public sealed class NimBusPublisherBuilder
{
    internal NimBusPublisherBuilder(AsyncApiEnrichmentRegistry enrichment) => Enrichment = enrichment;

    internal AsyncApiEnrichmentRegistry Enrichment { get; }

    /// <summary>
    /// Declares AsyncAPI enrichment for event type <typeparamref name="T"/>. Repeated calls for the
    /// same type accumulate onto the same options instance.
    /// </summary>
    public NimBusPublisherBuilder Publish<T>(Action<PublishOptions> configure)
        where T : IEvent
    {
        ArgumentNullException.ThrowIfNull(configure);

        // Configure mutates the registry's own options instance, so multiple registrations
        // accumulate and the exporter merges the result with any attribute enrichment.
        configure(new PublishOptions(Enrichment.For(typeof(T))));
        return this;
    }
}

/// <summary>Per-publish options exposed to <see cref="NimBusPublisherBuilder.Publish{T}"/>.</summary>
public sealed class PublishOptions
{
    internal PublishOptions(AsyncApiMessageOptions asyncApi) => AsyncApi = asyncApi;

    /// <summary>AsyncAPI enrichment for this event type (title, tags, owner, examples, …).</summary>
    public AsyncApiMessageOptions AsyncApi { get; }
}
