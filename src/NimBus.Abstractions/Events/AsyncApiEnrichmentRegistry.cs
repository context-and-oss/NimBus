using System;
using System.Collections.Generic;

namespace NimBus.Core.Events;

/// <summary>
/// Records fluent AsyncAPI enrichment keyed by event CLR <see cref="Type"/>. Populated by the SDK
/// fluent publisher path (<c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c>) and read by the exporter when
/// building the document. Has no DI dependency so it can live in <c>NimBus.Abstractions</c> and be
/// shared by the SDK, the CommandLine exporter, and the WebApp.
/// </summary>
public sealed class AsyncApiEnrichmentRegistry
{
    private readonly Dictionary<Type, AsyncApiMessageOptions> _entries = new();

    /// <summary>Gets (creating if absent) the enrichment options for <paramref name="eventType"/>.</summary>
    public AsyncApiMessageOptions For(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!_entries.TryGetValue(eventType, out var options))
        {
            options = new AsyncApiMessageOptions();
            _entries[eventType] = options;
        }

        return options;
    }

    /// <summary>Tries to get the recorded enrichment options for <paramref name="eventType"/>.</summary>
    public bool TryGet(Type eventType, out AsyncApiMessageOptions options)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        return _entries.TryGetValue(eventType, out options!);
    }

    /// <summary>All recorded enrichment entries.</summary>
    public IReadOnlyDictionary<Type, AsyncApiMessageOptions> Entries => _entries;
}
