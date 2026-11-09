using NimBus.Core.Events;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using System;

namespace NimBus.SDK;

public interface ISubscriberClient : IServiceBusAdapter 
{
    void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> eventHandlerFactory) where TEvent : IEvent;
}
