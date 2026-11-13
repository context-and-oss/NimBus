#pragma warning disable CA1707, CA2007
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.SDK.Extensions;
using NimBus.Testing;

namespace NimBus.SDK.Tests;

/// <summary>
/// How <c>WithCircuitBreaker</c> composes with an <c>AddNimBus</c> middleware pipeline.
/// </summary>
[TestClass]
public sealed class CircuitBreakerCompositionTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Recorder_runs_inside_custom_middleware_whichever_registration_comes_first(bool addNimBusFirst)
    {
        // AddNimBus adds its registry and pipeline unconditionally while WithCircuitBreaker
        // only fills in what is missing, so both orders must end in one pipeline that runs
        // the custom behaviors and, innermost, the recorder.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        void AddNimBus() => services.AddNimBus(nimbus => nimbus.AddPipelineBehavior<StateProbeBehavior>());
        void AddSubscriber() => services.AddNimBusSubscriber("Billing", subscriber =>
            subscriber.WithCircuitBreaker(circuit =>
            {
                circuit.MinimumThroughput = 1;
                circuit.FailurePercentageThreshold = 100;
            }));
        if (addNimBusFirst)
        {
            AddNimBus();
            AddSubscriber();
        }
        else
        {
            AddSubscriber();
            AddNimBus();
        }

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<MessagePipeline>();
        var probe = provider.GetRequiredService<StateProbeBehavior>();

        await Assert.ThrowsExactlyAsync<TransientException>(() =>
            pipeline.Execute(CreateContext(), (_, _) => throw new TransientException("down")));

        Assert.AreEqual(CircuitState.Closed, probe.StateBeforeHandler);
        Assert.AreEqual(
            CircuitState.Open,
            probe.StateAfterHandlerFailed,
            "The recorder must have counted the failure before it reached the custom behavior.");
    }

    [TestMethod]
    public async Task Failure_thrown_by_custom_middleware_before_the_handler_is_not_counted()
    {
        // The recorder is innermost: a rejection raised by middleware never reached the
        // endpoint's dependencies, so it must not open the circuit.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBus(nimbus => nimbus.AddPipelineBehavior<RejectingBehavior>());
        services.AddNimBusSubscriber("Billing", subscriber =>
            subscriber.WithCircuitBreaker(circuit =>
            {
                circuit.MinimumThroughput = 1;
                circuit.FailurePercentageThreshold = 100;
            }));

        using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<MessagePipeline>();

        await Assert.ThrowsExactlyAsync<TransientException>(() =>
            pipeline.Execute(CreateContext(), (_, _) => Task.CompletedTask));

        Assert.AreEqual(CircuitState.Closed, provider.GetRequiredService<IEndpointCircuitBreaker>().State);
    }

    private static InMemoryMessageContext CreateContext() => new(
        new Message
        {
            EventId = "event-1",
            MessageId = "message-1",
            SessionId = "session-1",
            To = "Billing",
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
            },
        },
        new InMemorySessionState());

    private sealed class StateProbeBehavior(IEndpointCircuitBreaker breaker) : IMessagePipelineBehavior
    {
        public CircuitState? StateBeforeHandler { get; private set; }

        public CircuitState? StateAfterHandlerFailed { get; private set; }

        public async Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default)
        {
            StateBeforeHandler = breaker.State;
            try
            {
                await next(context, cancellationToken);
            }
            catch
            {
                StateAfterHandlerFailed = breaker.State;
                throw;
            }
        }
    }

    private sealed class RejectingBehavior : IMessagePipelineBehavior
    {
        public Task Handle(IMessageContext context, MessagePipelineDelegate next, CancellationToken cancellationToken = default) =>
            throw new TransientException("rejected by middleware");
    }
}
