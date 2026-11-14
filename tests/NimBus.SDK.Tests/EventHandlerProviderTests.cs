#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;

namespace NimBus.SDK.Tests;

[TestClass]
public class EventHandlerProviderTests
{
    private const string DynamicEventType = "dynamic.event.v1";

    [TestMethod]
    public async Task Handle_HeaderBodyTypeMismatch_RejectsWithoutInvokingEitherHandler()
    {
        var provider = new EventHandlerProvider();
        var headerCalls = 0;
        var bodyCalls = 0;
        provider.RegisterHandler("header.event.v1", () => new DelegateEventJsonHandler((_, _) =>
        {
            headerCalls++;
            return Task.CompletedTask;
        }));
        provider.RegisterHandler("body.event.v1", () => new DelegateEventJsonHandler((_, _) =>
        {
            bodyCalls++;
            return Task.CompletedTask;
        }));

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => provider.Handle(
            MessageContextStub.ForEventTypes("header.event.v1", "body.event.v1", "{}")));

        Assert.AreEqual(0, headerCalls);
        Assert.AreEqual(0, bodyCalls);
        StringAssert.Contains(exception.Message, "header.event.v1", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "body.event.v1", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Handle_EmptyBodyType_RoutesUsingAuthoritativeContextType()
    {
        var provider = new EventHandlerProvider();
        var calls = 0;
        provider.RegisterHandler("header.event.v1", () => new DelegateEventJsonHandler((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }));

        await provider.Handle(MessageContextStub.ForEventTypes("header.event.v1", string.Empty, "{}"));

        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Handle_MissingContextType_DoesNotFallbackToBodyType()
    {
        var provider = new EventHandlerProvider();
        var calls = 0;
        provider.RegisterHandler("body.event.v1", () => new DelegateEventJsonHandler((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }));

        await Assert.ThrowsExactlyAsync<EventHandlerNotFoundException>(() => provider.Handle(
            MessageContextStub.ForEventTypes(string.Empty, "body.event.v1", "{}")));

        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Handle_InvalidBodyForKnownContextType_IsPermanentFailure()
    {
        var provider = new EventHandlerProvider();
        var calls = 0;
        provider.RegisterHandler("header.event.v1", () => new DelegateEventJsonHandler((_, _) =>
        {
            calls++;
            return Task.CompletedTask;
        }));
        var invalidBody = new InvalidMessageException("MessageContent is null.");

        var exception = await Assert.ThrowsExactlyAsync<PermanentFailureException>(() => provider.Handle(
            MessageContextStub.WithInvalidContent("header.event.v1", invalidBody)));

        Assert.AreSame(invalidBody, exception.InnerException);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task TypedHandler_ScopedDependency_IsResolvedPerMessageAndDisposedAsynchronously()
    {
        var services = new ServiceCollection();
        var probes = new List<ScopedProbe>();
        services.AddScoped(_ =>
        {
            var probe = new ScopedProbe();
            probes.Add(probe);
            return probe;
        });
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddHandler<ScopedEvent, ScopedHandler>();
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = new EventHandlerProvider(root.GetRequiredService<IServiceScopeFactory>());
        Register(builder, root, provider);

        await provider.Handle(MessageContextStub.ForEventType(nameof(ScopedEvent), "{}"));
        await provider.Handle(MessageContextStub.ForEventType(nameof(ScopedEvent), "{}"));

        Assert.AreEqual(2, probes.Count);
        Assert.AreNotSame(probes[0], probes[1]);
        Assert.IsTrue(probes.All(probe => probe.IsDisposed));
    }

    [TestMethod]
    public async Task ScopedDynamicHandler_DependencyIsResolvedPerMessageAndDisposedAsynchronously()
    {
        var services = new ServiceCollection();
        var probes = new List<ScopedProbe>();
        services.AddScoped(_ =>
        {
            var probe = new ScopedProbe();
            probes.Add(probe);
            return probe;
        });
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddScopedDynamicHandler(DynamicEventType, serviceProvider =>
            new ScopedDynamicHandler(serviceProvider.GetRequiredService<ScopedProbe>()));
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = new EventHandlerProvider(root.GetRequiredService<IServiceScopeFactory>());
        Register(builder, root, provider);

        await provider.Handle(MessageContextStub.ForEventType(DynamicEventType, "{}"));
        await provider.Handle(MessageContextStub.ForEventType(DynamicEventType, "{}"));

        Assert.AreEqual(2, probes.Count);
        Assert.AreNotSame(probes[0], probes[1]);
        Assert.IsTrue(probes.All(probe => probe.IsDisposed));
    }

    [TestMethod]
    public async Task DiAwareDynamicHandler_FactoryIsInvokedOnceAndHandlerRetainsStateAcrossMessages()
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;
        StatefulDynamicHandler? handler = null;
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddDynamicHandler(DynamicEventType, _ =>
        {
            factoryCalls++;
            handler = new StatefulDynamicHandler();
            return handler;
        });
        await using var root = services.BuildServiceProvider();
        var provider = new EventHandlerProvider(root.GetRequiredService<IServiceScopeFactory>());
        Register(builder, root, provider);

        Assert.AreEqual(1, factoryCalls);
        Assert.IsNotNull(handler);

        await provider.Handle(MessageContextStub.ForEventType(DynamicEventType, "{}"));
        await provider.Handle(MessageContextStub.ForEventType(DynamicEventType, "{}"));

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(2, handler.HandleCalls);
    }

    [TestMethod]
    public async Task ScopedDynamicFallbackHandler_DependencyIsResolvedPerMessageAndDisposedAsynchronously()
    {
        var services = new ServiceCollection();
        var probes = new List<ScopedProbe>();
        services.AddScoped(_ =>
        {
            var probe = new ScopedProbe();
            probes.Add(probe);
            return probe;
        });
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddScopedDynamicFallbackHandler(serviceProvider =>
            new ScopedDynamicHandler(serviceProvider.GetRequiredService<ScopedProbe>()));
        await using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var provider = new EventHandlerProvider(root.GetRequiredService<IServiceScopeFactory>());
        Register(builder, root, provider);

        await provider.Handle(MessageContextStub.ForEventType("unregistered.event.v1", "{}"));
        await provider.Handle(MessageContextStub.ForEventType("another.unregistered.event.v1", "{}"));

        Assert.AreEqual(2, probes.Count);
        Assert.AreNotSame(probes[0], probes[1]);
        Assert.IsTrue(probes.All(probe => probe.IsDisposed));
    }

    [TestMethod]
    public async Task DiAwareFallbackHandler_FactoryIsInvokedOnceAndHandlerRetainsStateAcrossMessages()
    {
        var services = new ServiceCollection();
        var factoryCalls = 0;
        StatefulDynamicHandler? handler = null;
        var builder = new NimBusSubscriberBuilder(services);
        builder.AddDynamicFallbackHandler(_ =>
        {
            factoryCalls++;
            handler = new StatefulDynamicHandler();
            return handler;
        });
        await using var root = services.BuildServiceProvider();
        var provider = new EventHandlerProvider(root.GetRequiredService<IServiceScopeFactory>());
        Register(builder, root, provider);

        Assert.AreEqual(1, factoryCalls);
        Assert.IsNotNull(handler);

        await provider.Handle(MessageContextStub.ForEventType("unregistered.event.v1", "{}"));
        await provider.Handle(MessageContextStub.ForEventType("another.unregistered.event.v1", "{}"));

        Assert.AreEqual(1, factoryCalls);
        Assert.AreEqual(2, handler.HandleCalls);
    }

    private static void Register(
        NimBusSubscriberBuilder builder,
        IServiceProvider root,
        EventHandlerProvider provider)
    {
        foreach (var registration in builder.HandlerRegistrations)
            registration.Register(root, provider);
    }

    public sealed class ScopedEvent : Event
    {
    }

    public sealed class ScopedHandler : IEventHandler<ScopedEvent>
    {
        private readonly ScopedProbe _probe;

        public ScopedHandler(ScopedProbe probe)
        {
            _probe = probe;
        }

        public Task Handle(ScopedEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(_probe.IsDisposed);
            return Task.CompletedTask;
        }
    }

    public sealed class ScopedDynamicHandler : IEventJsonHandler
    {
        private readonly ScopedProbe _probe;

        public ScopedDynamicHandler(ScopedProbe probe)
        {
            _probe = probe;
        }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(_probe.IsDisposed);
            return Task.CompletedTask;
        }
    }

    public sealed class StatefulDynamicHandler : IEventJsonHandler
    {
        public int HandleCalls { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    public sealed class ScopedProbe : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
