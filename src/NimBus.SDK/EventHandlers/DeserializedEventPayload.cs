using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using NimBus.Core.Messages;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// Carries an event payload already parsed for one message to the typed handler of that same
/// message, so <see cref="EventJsonHandler{TEvent}"/> doesn't deserialize it a second time.
/// <see cref="RoutingKeyGuardContextHandler"/> sets it and clears it when the message has been
/// handled.
/// </summary>
/// <remarks>
/// Entries are keyed weakly by the <see cref="IMessageContext"/> instance, so a payload never
/// outlives its message context and is never seen by another message. Each entry records the
/// JSON it was parsed from; a reader gets the payload only for that same JSON.
/// </remarks>
internal static class DeserializedEventPayload
{
    private static readonly ConditionalWeakTable<IMessageContext, Entry> Payloads = new();

    /// <summary>Carries <paramref name="payload"/>, parsed from <paramref name="json"/>, for <paramref name="context"/>.</summary>
    internal static void Set(IMessageContext context, string json, object payload)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(payload);
        Payloads.AddOrUpdate(context, new Entry(json, payload));
    }

    /// <summary>
    /// The payload carried for <paramref name="context"/>, when one was parsed from exactly
    /// <paramref name="json"/> and is a <typeparamref name="T"/>.
    /// </summary>
    internal static bool TryGet<T>(IMessageContext context, string json, [MaybeNullWhen(false)] out T payload)
    {
        if (Payloads.TryGetValue(context, out var entry)
            && string.Equals(entry.Json, json, StringComparison.Ordinal)
            && entry.Payload is T typed)
        {
            payload = typed;
            return true;
        }

        payload = default;
        return false;
    }

    /// <summary>Drops whatever is carried for <paramref name="context"/>.</summary>
    internal static void Clear(IMessageContext context) => Payloads.Remove(context);

    private sealed record Entry(string Json, object Payload);
}
