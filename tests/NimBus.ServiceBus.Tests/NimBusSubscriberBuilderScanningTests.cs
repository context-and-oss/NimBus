#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.Testing;
using NimBus.Testing.Extensions;
using System;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public sealed class NimBusSubscriberBuilderScanningTests
{
    [TestInitialize]
    public void ResetCounters()
    {
        ScannedOrderHandler.HandleCount = 0;
        MultiHandler.FirstCount = 0;
        MultiHandler.SecondCount = 0;
        ExplicitOverrideHandler.HandleCount = 0;
    }

    [TestMethod]
    public async Task AddHandlersFromAssemblyContaining_DiscoversAndInvokesHandler()
    {
        var services = new ServiceCollection();
        services.AddNimBusTestTransport(sub =>
            sub.AddHandlersFromAssemblyContaining<NimBusSubscriberBuilderScanningTests>());

        var sp = services.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IPublisherClient>();
        var bus = sp.GetRequiredService<InMemoryMessageBus>();
        var handler = sp.GetRequiredService<IMessageHandler>();

        await publisher.Publish(new ScannedOrderEvent());
        await bus.DeliverAll(handler);

        Assert.AreEqual(1, ScannedOrderHandler.HandleCount);
    }

    [TestMethod]
    public async Task AddHandlersFromAssemblyContaining_RegistersOneHandlerForMultipleEvents()
    {
        var services = new ServiceCollection();
        services.AddNimBusTestTransport(sub =>
            sub.AddHandlersFromAssemblyContaining<NimBusSubscriberBuilderScanningTests>());

        var sp = services.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IPublisherClient>();
        var bus = sp.GetRequiredService<InMemoryMessageBus>();
        var handler = sp.GetRequiredService<IMessageHandler>();

        await publisher.Publish(new MultiFirstEvent());
        await publisher.Publish(new MultiSecondEvent());
        await bus.DeliverAll(handler);

        Assert.AreEqual(1, MultiHandler.FirstCount);
        Assert.AreEqual(1, MultiHandler.SecondCount);
    }

    [TestMethod]
    public async Task AddHandler_OverridesScannedRegistrationForSameEvent()
    {
        var scannedAssembly = CreateDynamicHandlerAssembly(typeof(OverrideScannedEvent), handlerCount: 1);
        var services = new ServiceCollection();
        services.AddNimBusTestTransport(sub =>
        {
            sub.AddHandlersFromAssembly(scannedAssembly);
            sub.AddHandler<OverrideScannedEvent, ExplicitOverrideHandler>();
        });

        var sp = services.BuildServiceProvider();
        var publisher = sp.GetRequiredService<IPublisherClient>();
        var bus = sp.GetRequiredService<InMemoryMessageBus>();
        var handler = sp.GetRequiredService<IMessageHandler>();

        await publisher.Publish(new OverrideScannedEvent());
        await bus.DeliverAll(handler);

        Assert.AreEqual(1, ExplicitOverrideHandler.HandleCount);
    }

    [TestMethod]
    public void AddHandlersFromAssembly_ThrowsWhenScanFindsDuplicateHandlersForEvent()
    {
        var duplicateAssembly = CreateDynamicHandlerAssembly(typeof(DuplicateScannedEvent), handlerCount: 2);
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            builder.AddHandlersFromAssembly(duplicateAssembly));

        StringAssert.Contains(ex.Message, nameof(DuplicateScannedEvent), StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, "Multiple handlers", StringComparison.Ordinal);
    }

    [TestMethod]
    public void AddHandlersFromAssembly_IsIdempotentForSameAssembly()
    {
        var builder = new NimBusSubscriberBuilder(new ServiceCollection());

        builder.AddHandlersFromAssemblyContaining<NimBusSubscriberBuilderScanningTests>();
        builder.AddHandlersFromAssemblyContaining<NimBusSubscriberBuilderScanningTests>();
    }

    [TestMethod]
    public void AddNimBusTestTransport_WithFailureDispositions_WiresClassifier()
    {
        var classifier = new RetryFailureDispositionClassifier();
        var services = new ServiceCollection();
        services.AddNimBusTestTransport(sub => sub.WithFailureDispositions(classifier));

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IMessageHandler>();
        var field = handler.GetType().GetField(
            "_failureDispositionClassifier",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.IsNotNull(field);
        Assert.AreSame(classifier, field.GetValue(handler));
    }

    private static AssemblyBuilder CreateDynamicHandlerAssembly(Type eventType, int handlerCount)
    {
        var assemblyName = new AssemblyName($"NimBus.DynamicHandlerTests.{Guid.NewGuid():N}");
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);
        var moduleBuilder = assemblyBuilder.DefineDynamicModule("Handlers");
        var handlerInterface = typeof(IEventHandler<>).MakeGenericType(eventType);
        var handleMethod = handlerInterface.GetMethod(nameof(IEventHandler<IEvent>.Handle));

        for (var i = 0; i < handlerCount; i++)
        {
            var typeBuilder = moduleBuilder.DefineType(
                $"DynamicHandler{i}",
                TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Sealed);
            typeBuilder.AddInterfaceImplementation(handlerInterface);
            typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

            var method = typeBuilder.DefineMethod(
                nameof(IEventHandler<IEvent>.Handle),
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot,
                typeof(Task),
                [eventType, typeof(IEventHandlerContext), typeof(CancellationToken)]);

            var il = method.GetILGenerator();
            il.EmitCall(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetMethod!, null);
            il.Emit(OpCodes.Ret);

            typeBuilder.DefineMethodOverride(method, handleMethod!);
            typeBuilder.CreateType();
        }

        return assemblyBuilder;
    }

    public sealed class ScannedOrderEvent : Event;

    public sealed class MultiFirstEvent : Event;

    public sealed class MultiSecondEvent : Event;

    public sealed class OverrideScannedEvent : Event;

    public sealed class DuplicateScannedEvent : Event;

    public sealed class ScannedOrderHandler : IEventHandler<ScannedOrderEvent>
    {
        public static int HandleCount { get; set; }

        public Task Handle(ScannedOrderEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCount++;
            return Task.CompletedTask;
        }
    }

    public sealed class MultiHandler :
        IEventHandler<MultiFirstEvent>,
        IEventHandler<MultiSecondEvent>
    {
        public static int FirstCount { get; set; }

        public static int SecondCount { get; set; }

        public Task Handle(MultiFirstEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            FirstCount++;
            return Task.CompletedTask;
        }

        public Task Handle(MultiSecondEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            SecondCount++;
            return Task.CompletedTask;
        }
    }

    public sealed class ExplicitOverrideHandler : IEventHandler<OverrideScannedEvent>
    {
        public static int HandleCount { get; set; }

        public Task Handle(OverrideScannedEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RetryFailureDispositionClassifier : IFailureDispositionClassifier
    {
        public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName) =>
            FailureDisposition.Retry;
    }
}
