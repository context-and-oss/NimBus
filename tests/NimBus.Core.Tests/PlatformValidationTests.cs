#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;

namespace NimBus.Core.Tests;

[TestClass]
public class PlatformValidationTests
{
    private sealed class TestCommand : Command
    {
        public Guid OrderId { get; set; }
    }

    private sealed class SecondCommand : Command
    {
        public Guid OrderId { get; set; }
    }

    private sealed class PlainEvent : Event
    {
        public Guid OrderId { get; set; }
    }

    private sealed class ProducerEndpoint : Endpoint
    {
        public ProducerEndpoint()
        {
            Produces<TestCommand>();
            Produces<SecondCommand>();
            Produces<PlainEvent>();
        }
    }

    private sealed class ConsumerEndpoint : Endpoint
    {
        public ConsumerEndpoint()
        {
            Consumes<TestCommand>();
            Consumes<PlainEvent>();
        }
    }

    private sealed class SecondConsumerEndpoint : Endpoint
    {
        public SecondConsumerEndpoint()
        {
            Consumes<TestCommand>();
            Consumes<PlainEvent>();
        }
    }

    private sealed class TestPlatform : Platform
    {
        public TestPlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints)
            {
                AddEndpoint(endpoint);
            }
        }
    }

    [TestMethod]
    public void ExactlyOneConsumer_NoErrors()
    {
        var platform = new TestPlatform(
            new SingleCommandProducer(),
            new SingleCommandConsumer());

        var errors = PlatformValidation.ValidateCommandConsumers(platform);

        Assert.AreEqual(0, errors.Count);
        PlatformValidation.EnsureCommandConsumers(platform);
    }

    private sealed class SingleCommandProducer : Endpoint
    {
        public SingleCommandProducer() => Produces<TestCommand>();
    }

    private sealed class SingleCommandConsumer : Endpoint
    {
        public SingleCommandConsumer() => Consumes<TestCommand>();
    }

    [TestMethod]
    public void ZeroConsumers_ReportsError()
    {
        var platform = new TestPlatform(new SingleCommandProducer());

        var errors = PlatformValidation.ValidateCommandConsumers(platform);

        Assert.AreEqual(1, errors.Count);
        StringAssert.Contains(errors[0], "TestCommand");
        StringAssert.Contains(errors[0], "no consuming endpoint");
    }

    [TestMethod]
    public void MultipleConsumers_ReportsErrorNamingAllConsumers()
    {
        var platform = new TestPlatform(
            new ProducerEndpoint(),
            new ConsumerEndpoint(),
            new SecondConsumerEndpoint());

        var errors = PlatformValidation.ValidateCommandConsumers(platform);

        // TestCommand has 2 consumers (error); SecondCommand has 0 (error); PlainEvent is exempt.
        Assert.AreEqual(2, errors.Count);
        var multiError = errors.Single(e => e.Contains("2 consumers", StringComparison.Ordinal));
        StringAssert.Contains(multiError, "TestCommand");
        StringAssert.Contains(multiError, nameof(ConsumerEndpoint));
        StringAssert.Contains(multiError, nameof(SecondConsumerEndpoint));
    }

    [TestMethod]
    public void PlainEventWithManyConsumers_IsNotValidated()
    {
        var platform = new TestPlatform(
            new PlainEventProducer(),
            new PlainEventConsumer(),
            new SecondPlainEventConsumer());

        var errors = PlatformValidation.ValidateCommandConsumers(platform);

        Assert.AreEqual(0, errors.Count);
    }

    private sealed class PlainEventProducer : Endpoint
    {
        public PlainEventProducer() => Produces<PlainEvent>();
    }

    private sealed class PlainEventConsumer : Endpoint
    {
        public PlainEventConsumer() => Consumes<PlainEvent>();
    }

    private sealed class SecondPlainEventConsumer : Endpoint
    {
        public SecondPlainEventConsumer() => Consumes<PlainEvent>();
    }

    [TestMethod]
    public void EnsureCommandConsumers_JoinsAllErrorsInOneException()
    {
        var platform = new TestPlatform(
            new ProducerEndpoint(),
            new ConsumerEndpoint(),
            new SecondConsumerEndpoint());

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => PlatformValidation.EnsureCommandConsumers(platform));

        StringAssert.Contains(ex.Message, "TestCommand");
        StringAssert.Contains(ex.Message, "SecondCommand");
    }

    [TestMethod]
    public void EventTypeWithoutClrType_IsSkipped()
    {
        var platform = new TestPlatform(new ClrLessEndpoint());

        var errors = PlatformValidation.ValidateCommandConsumers(platform);

        Assert.AreEqual(0, errors.Count);
    }

    [TestMethod]
    public void NullPlatform_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => PlatformValidation.ValidateCommandConsumers(null!));
    }

    private sealed class ClrLessEndpoint : IEndpoint
    {
        public string Id => nameof(ClrLessEndpoint);
        public string Name => Id;
        public string? Description => null;
        public string? Namespace => GetType().Namespace;
        public string SecurityGroupName => $"azu-endpoint-{Id}";
        public ISystem? System => null;
        public IEnumerable<IEventType> EventTypesProduced => new[] { new ClrLessEventType() };
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class ClrLessEventType : IEventType
    {
        public string Id => "config.loaded.event.v1";
        public string Name => Id;
        public string? Description => null;
        public string? Namespace => null;
        public IEnumerable<IProperty> Properties => Enumerable.Empty<IProperty>();
        public Type GetEventClassType() => null!;
        public IEvent? GetEventExample() => null;
    }
}
