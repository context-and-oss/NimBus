using Azure.Messaging.ServiceBus;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.ServiceBus;

public interface IServiceBusMessage
{
    string? GetUserProperty(UserPropertyName name);
    string? GetUserProperty(string name);

    /// <summary>
    /// All application-property names present on the inbound message. Used to
    /// enumerate CloudEvents context/extension attributes (which carry
    /// arbitrary, producer-defined names) in binary content mode. Defaults to
    /// an empty set so existing implementers are forward-compatible.
    /// </summary>
    IReadOnlyCollection<string> GetUserPropertyNames() => Array.Empty<string>();

    byte[] Body { get; }
    string LockToken { get; }
    string SessionId { get; }
    string MessageId { get; }
    string CorrelationId { get; }
    int DeliveryCount { get; }
    long SequenceNumber { get; }
    DateTime EnqueuedTimeUtc { get; }

    /// <summary>
    /// The AMQP content-type of the inbound message. Used to detect a
    /// structured CloudEvents envelope (<c>application/cloudevents+json</c>).
    /// Defaults to <c>null</c> so existing implementers are forward-compatible.
    /// </summary>
    string? ContentType => null;

    /// <summary>
    /// The AMQP reply-to address of the inbound message (the requesting endpoint's
    /// topic for request/reply). Defaults to <c>null</c> so existing implementers
    /// are forward-compatible.
    /// </summary>
    string? ReplyTo => null;

    /// <summary>
    /// The AMQP reply-to session id correlating a request to its awaited reply.
    /// Defaults to <c>null</c> so existing implementers are forward-compatible.
    /// </summary>
    string? ReplyToSessionId => null;

    internal ServiceBusReceivedMessage Message { get; }
}

public class ServiceBusMessage : IServiceBusMessage
{
    private readonly ServiceBusReceivedMessage _message;

    public ServiceBusMessage(ServiceBusReceivedMessage message)
    {
        _message = message;
    }


    public string LockToken => _message.LockToken;

    public string SessionId => _message.SessionId;

    public byte[] Body => _message.Body.ToArray();

    public string MessageId => _message.MessageId;

    public string CorrelationId => _message.CorrelationId;

    public int DeliveryCount => _message.DeliveryCount;

    public long SequenceNumber => _message.SequenceNumber;

    public DateTime EnqueuedTimeUtc => _message.EnqueuedTime.UtcDateTime;

    public string ContentType => _message.ContentType;

    public string ReplyTo => _message.ReplyTo;

    public string ReplyToSessionId => _message.ReplyToSessionId;

    ServiceBusReceivedMessage IServiceBusMessage.Message => _message;

    public string? GetUserProperty(UserPropertyName name)
    {
        return GetUserProperty(name.ToString());
    }

    public string? GetUserProperty(string name)
    {
        if (!_message.ApplicationProperties.ContainsKey(name))
            return null;

        return _message.ApplicationProperties[name]?.ToString();
    }

    public IReadOnlyCollection<string> GetUserPropertyNames() =>
        _message.ApplicationProperties.Keys.ToArray();
}
