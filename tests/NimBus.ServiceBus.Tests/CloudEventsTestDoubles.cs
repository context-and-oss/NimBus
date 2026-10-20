#pragma warning disable CA1707, CA1515, CA2007
using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Minimal inbound Service Bus message double used by the CloudEvents consume /
/// dispatch tests. Unlike the private double in MessageContextTests it exposes a
/// settable <see cref="ContentType"/> and lets arbitrary (prefixed) application
/// property names be set, so a message authored by a NON-NimBus CloudEvents
/// producer can be simulated.
/// </summary>
internal sealed class FakeCloudEventMessage : IServiceBusMessage
{
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    public byte[] Body { get; set; } = Array.Empty<byte>();
    public string LockToken { get; set; } = "lock-1";
    public string SessionId { get; set; }
    public string MessageId { get; set; }
    public string CorrelationId { get; set; }
    public int DeliveryCount { get; set; } = 1;
    public long SequenceNumber { get; set; } = 1;
    public DateTime EnqueuedTimeUtc { get; set; } = new(2026, 7, 8, 0, 0, 0, DateTimeKind.Utc);
    public string ContentType { get; set; }

    ServiceBusReceivedMessage IServiceBusMessage.Message => null;

    public string GetUserProperty(UserPropertyName name) => GetUserProperty(name.ToString());

    public string GetUserProperty(string name) =>
        Properties.TryGetValue(name, out var value) ? value : null;

    public IReadOnlyCollection<string> GetUserPropertyNames() => Properties.Keys;
}

/// <summary>Inert session double — the CloudEvents consume path never touches the session.</summary>
internal sealed class InertServiceBusSession : IServiceBusSession
{
    public Task CompleteAsync(IServiceBusMessage message, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeadLetterAsync(IServiceBusMessage message, string reason, string? description, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetStateAsync(SessionState state, CancellationToken ct = default) => Task.CompletedTask;
    public Task<SessionState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(new SessionState());
    public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledTime, CancellationToken ct = default) => Task.CompletedTask;
}
