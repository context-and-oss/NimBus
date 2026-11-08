using System;
using Microsoft.Extensions.DependencyInjection;
using NimBus.Core;
using NimBus.Core.Events;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Registers an <see cref="IAsyncApiDocumentProvider"/> so the very application host that ran the
/// fluent <c>AddNimBusPublisher(endpoint, b =&gt; b.Publish&lt;T&gt;(o =&gt; o.AsyncApi…))</c>
/// registrations can export its own enriched AsyncAPI document from the same container.
/// </summary>
public static class AsyncApiDocumentServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IAsyncApiDocumentProvider"/> that serializes <paramref name="platform"/>
    /// with the fluent enrichment recorded in this container. The SDK stays exporter-agnostic: the
    /// composition root supplies <paramref name="serialize"/> — normally an adapter over
    /// <c>AsyncApiExporter.Serialize</c> — whose parameter order matches the exporter overload
    /// <c>(IPlatform, AsyncApiFormat, AsyncApiEnrichmentRegistry?)</c>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="platform">The platform to export.</param>
    /// <param name="serialize">Serializer delegate, e.g. <c>(p, f, r) =&gt; AsyncApiExporter.Serialize(p, f, r)</c>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddNimBusAsyncApiDocument(
        this IServiceCollection services,
        IPlatform platform,
        Func<IPlatform, AsyncApiFormat, AsyncApiEnrichmentRegistry?, string> serialize)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(serialize);

        // Resolve-or-create the SAME registry the fluent publisher path accumulates into, so the
        // provider sees every Publish<T>(o => o.AsyncApi…) registration.
        var registry = ServiceCollectionExtensions.GetOrCreateEnrichmentRegistry(services);

        services.AddSingleton<IAsyncApiDocumentProvider>(new AsyncApiDocumentProvider(platform, registry, serialize));
        return services;
    }

    private sealed class AsyncApiDocumentProvider : IAsyncApiDocumentProvider
    {
        private readonly IPlatform _platform;
        private readonly AsyncApiEnrichmentRegistry _registry;
        private readonly Func<IPlatform, AsyncApiFormat, AsyncApiEnrichmentRegistry?, string> _serialize;

        public AsyncApiDocumentProvider(
            IPlatform platform,
            AsyncApiEnrichmentRegistry registry,
            Func<IPlatform, AsyncApiFormat, AsyncApiEnrichmentRegistry?, string> serialize)
        {
            _platform = platform;
            _registry = registry;
            _serialize = serialize;
        }

        public string GetDocument(AsyncApiFormat format) => _serialize(_platform, format, _registry);
    }
}
