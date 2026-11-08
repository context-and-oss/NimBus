#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.Testing;

namespace NimBus.SDK.Tests;

/// <summary>
/// How handlers reach the dispatch table: assembly scanning (fed an exact type set through a
/// fake <see cref="Assembly"/>), explicit overrides, request handlers resolved from DI, and
/// the registration guards on <see cref="EventHandlerProvider"/>.
/// </summary>
[TestClass]
public class SubscriberHandlerRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    // ── Assembly scanning ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Scan_RegistersConcreteHandlers_AndSkipsAbstractAndOpenGenericTypes()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        builder.AddHandlersFromAssembly(new TypeSetAssembly(
            typeof(ScanCancelledHandler),
            typeof(ScanAbstractHandler),
            typeof(ScanOpenGenericHandler<>),
            typeof(ScanShipped)));

        var registration = builder.HandlerRegistrations.Single();
        Assert.AreEqual(nameof(ScanCancelled), registration.EventTypeId);
        Assert.AreEqual(typeof(ScanCancelledHandler), registration.HandlerType);
        Assert.IsFalse(registration.IsExplicit);
    }

    [TestMethod]
    public void Scan_TwoHandlersForOneEvent_FailsAndSaysToRegisterTheChoiceFirst()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => builder.AddHandlersFromAssembly(
            new TypeSetAssembly(typeof(ScanShippedHandlerA), typeof(ScanShippedHandlerB))));

        StringAssert.Contains(exception.Message, nameof(ScanShippedHandlerA), StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, nameof(ScanShippedHandlerB), StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "before scanning", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Scan_AfterAnExplicitHandler_KeepsTheExplicitChoice()
    {
        var services = SubscriberServices();
        services.AddNimBusSubscriber("ScanEndpoint", sub =>
        {
            sub.AddHandler<ScanShipped, ScanShippedHandlerA>();
            sub.AddHandlersFromAssembly(new TypeSetAssembly(typeof(ScanShippedHandlerA), typeof(ScanShippedHandlerB)));
        });

        var handled = await DispatchAsync(services, nameof(ScanShipped));

        CollectionAssert.AreEqual(new[] { nameof(ScanShippedHandlerA) }, handled);
    }

    [TestMethod]
    public async Task ExplicitHandler_AfterASingleScannedHandler_ReplacesIt()
    {
        var services = SubscriberServices();
        services.AddNimBusSubscriber("ScanEndpoint", sub =>
        {
            sub.AddHandlersFromAssembly(new TypeSetAssembly(typeof(ScanShippedHandlerB)));
            sub.AddHandler<ScanShipped, ScanShippedHandlerA>();
        });

        var handled = await DispatchAsync(services, nameof(ScanShipped));

        CollectionAssert.AreEqual(new[] { nameof(ScanShippedHandlerA) }, handled);
    }

    [TestMethod]
    public void Scan_TheSameAssemblyTwice_IsIdempotent()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        var assembly = new TypeSetAssembly(typeof(ScanCancelledHandler));

        builder.AddHandlersFromAssemblies(assembly, assembly);

        Assert.AreEqual(1, builder.HandlerRegistrations.Count);
    }

    [TestMethod]
    public void Scan_WithUnloadableTypes_RegistersTheTypesThatDidLoad()
    {
        // A handler assembly with a missing optional dependency throws
        // ReflectionTypeLoadException from GetTypes(); the loadable handlers still count.
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        builder.AddHandlersFromAssembly(new TypeSetAssembly(typeof(ScanCancelledHandler)) { FailToLoadSomeTypes = true });

        Assert.AreEqual(nameof(ScanCancelled), builder.HandlerRegistrations.Single().EventTypeId);
    }

    [TestMethod]
    public void Scan_ConflictingWithADynamicHandler_Fails()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());
        builder.AddDynamicHandler(nameof(ScanCancelled), () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask));

        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => builder.AddHandlersFromAssembly(new TypeSetAssembly(typeof(ScanCancelledHandler))));

        StringAssert.Contains(exception.Message, "dynamic handler", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Scan_RejectsNullAssemblies()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddHandlersFromAssembly(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => builder.AddHandlersFromAssemblies(null!));
    }

    // ── Request handlers resolved from DI ──────────────────────────────────────────────

    [TestMethod]
    public async Task RequestHandler_RegisteredThroughTheBuilder_RepliesThroughTheRegisteredDispatcher()
    {
        var replies = new RecordingReplyDispatcher();
        var services = SubscriberServices();
        services.AddSingleton<IReplyDispatcher>(replies);
        services.AddNimBusSubscriber("ScanEndpoint", sub => sub.AddRequestHandler<ScanPing, ScanPong, ScanPingHandler>());
        await using var provider = services.BuildServiceProvider();
        var handlers = GetEventHandlerProvider(provider);

        await handlers.Handle(new InMemoryMessageContext(
            new Message
            {
                EventId = "event-1",
                MessageId = "request-1",
                CorrelationId = "correlation-1",
                SessionId = "session-1",
                EventTypeId = nameof(ScanPing),
                MessageType = MessageType.EventRequest,
                ReplyTo = "CrmEndpoint",
                ReplyToSessionId = "reply-session",
                MessageContent = new MessageContent
                {
                    EventContent = new EventContent
                    {
                        EventTypeId = nameof(ScanPing),
                        EventJson = JsonConvert.SerializeObject(new ScanPing { Text = "hi" }),
                    },
                },
            },
            new InMemorySessionState()));

        var reply = replies.Sent.Single();
        Assert.AreEqual("CrmEndpoint", reply.ReplyTo);
        Assert.AreEqual("reply-session", reply.ReplySessionId);
        Assert.AreEqual("correlation-1", reply.CorrelationId);
        Assert.AreEqual("hi", JsonConvert.DeserializeObject<ScanPong>(reply.PayloadJson!)!.Echo);
    }

    // ── EventHandlerProvider registration guards ───────────────────────────────────────

    [TestMethod]
    public void Provider_RejectsInvalidRegistrations()
    {
        var provider = new EventHandlerProvider();

        Assert.ThrowsExactly<ArgumentException>(() => provider.RegisterHandler(" ", () => new DelegateEventJsonHandler((_, _) => Task.CompletedTask)));
        Assert.ThrowsExactly<ArgumentNullException>(() => provider.RegisterHandler("scan.event.v1", (Func<IEventJsonHandler>)null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => provider.RegisterHandler(null!, () => new object()));
        Assert.ThrowsExactly<ArgumentNullException>(() => provider.RegisterHandler(typeof(ScanShipped), (Func<object>)null!));
        Assert.ThrowsExactly<ArgumentException>(() => provider.RegisterHandler(typeof(string), () => new object()));
        Assert.ThrowsExactly<ArgumentNullException>(() => provider.RegisterFallbackHandler((Func<IEventJsonHandler>)null!));
    }

    [TestMethod]
    public async Task Provider_FallbackHandlesTypesWithoutASpecificHandler_ButNotTheOnesWithOne()
    {
        var provider = new EventHandlerProvider();
        var specific = 0;
        var fallback = 0;
        provider.RegisterHandler("scan.known.v1", () => new DelegateEventJsonHandler((_, _) => { specific++; return Task.CompletedTask; }));
        provider.RegisterFallbackHandler(() => new DelegateEventJsonHandler((_, _) => { fallback++; return Task.CompletedTask; }));

        await provider.Handle(MessageContextStub.ForEventType("scan.known.v1", "{}"));
        await provider.Handle(MessageContextStub.ForEventType("scan.unknown.v1", "{}"));

        Assert.AreEqual(1, specific);
        Assert.AreEqual(1, fallback);
    }

    [TestMethod]
    public async Task Provider_WithoutAnyHandler_ReportsTheMissingType()
    {
        var provider = new EventHandlerProvider();

        var exception = await Assert.ThrowsExactlyAsync<EventHandlerNotFoundException>(
            () => provider.Handle(MessageContextStub.ForEventType("scan.unknown.v1", "{}")));

        StringAssert.Contains(exception.Message, "scan.unknown.v1", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Provider_NullContext_IsRejected()
    {
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => new EventHandlerProvider().Handle(null!));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────

    private static ServiceCollection SubscriberServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<HandledLog>();
        return services;
    }

    private static async Task<string[]> DispatchAsync(ServiceCollection services, string eventTypeId)
    {
        await using var provider = services.BuildServiceProvider();
        await GetEventHandlerProvider(provider).Handle(MessageContextStub.ForEventType(eventTypeId, "{}"));
        return provider.GetRequiredService<HandledLog>().Handled.ToArray();
    }

    private static EventHandlerProvider GetEventHandlerProvider(IServiceProvider provider)
    {
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var field = subscriber.GetType().GetField("_eventHandlerProvider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SubscriberClient no longer holds _eventHandlerProvider.");
        return (EventHandlerProvider)field.GetValue(subscriber)!;
    }

    /// <summary>An assembly whose GetTypes() returns exactly the given types.</summary>
    private sealed class TypeSetAssembly : Assembly
    {
        private readonly Type[] _types;

        public TypeSetAssembly(params Type[] types) => _types = types;

        public bool FailToLoadSomeTypes { get; init; }

        public override Type[] GetTypes() => FailToLoadSomeTypes
            ? throw new ReflectionTypeLoadException(_types.Append(null).ToArray()!, new Exception[] { new TypeLoadException("missing dependency") })
            : _types;
    }

    private sealed class HandledLog
    {
        public List<string> Handled { get; } = new();
    }

    private sealed class RecordingReplyDispatcher : IReplyDispatcher
    {
        public List<ReplyMessage> Sent { get; } = new();

        public Task SendReplyAsync(ReplyMessage reply, CancellationToken cancellationToken = default)
        {
            Sent.Add(reply);
            return Task.CompletedTask;
        }
    }

    public sealed class ScanShipped : Event
    {
    }

    public sealed class ScanCancelled : Event
    {
    }

    public sealed class ScanPing : Event
    {
        public string Text { get; set; } = string.Empty;
    }

    public sealed class ScanPong
    {
        public string Echo { get; set; } = string.Empty;
    }

    private sealed class ScanShippedHandlerA(HandledLog log) : IEventHandler<ScanShipped>
    {
        public Task Handle(ScanShipped message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            log.Handled.Add(nameof(ScanShippedHandlerA));
            return Task.CompletedTask;
        }
    }

    private sealed class ScanShippedHandlerB(HandledLog log) : IEventHandler<ScanShipped>
    {
        public Task Handle(ScanShipped message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            log.Handled.Add(nameof(ScanShippedHandlerB));
            return Task.CompletedTask;
        }
    }

    private sealed class ScanCancelledHandler : IEventHandler<ScanCancelled>
    {
        public Task Handle(ScanCancelled message, IEventHandlerContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private abstract class ScanAbstractHandler : IEventHandler<ScanShipped>
    {
        public abstract Task Handle(ScanShipped message, IEventHandlerContext context, CancellationToken cancellationToken = default);
    }

    private sealed class ScanOpenGenericHandler<TEvent> : IEventHandler<TEvent>
        where TEvent : IEvent
    {
        public Task Handle(TEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ScanPingHandler : IRequestHandler<ScanPing, ScanPong>
    {
        public Task<ScanPong> Handle(ScanPing request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScanPong { Echo = request.Text });
    }
}
