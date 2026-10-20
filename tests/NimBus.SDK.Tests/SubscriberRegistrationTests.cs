#pragma warning disable CA1707, CA2007, CS0618
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.CircuitBreaker;
using NimBus.Core.Extensions;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

/// <summary>
/// Coverage for <see cref="ServiceCollectionExtensions.AddNimBusSubscriber"/>'s
/// one-endpoint-per-process invariant. <see cref="ISubscriberClient"/> is a
/// non-keyed singleton; a second registration against a different endpoint
/// used to be silently dropped by TryAddSingleton, binding the second
/// endpoint's handlers to a no-op. The guard now throws so the misuse is
/// loud at startup.
/// </summary>
[TestClass]
public class SubscriberRegistrationTests
{
    private const string FakeConnection =
        "Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=AAA=";

    [TestMethod]
    public void WithCircuitBreaker_registers_endpoint_singleton_and_pipeline_without_AddNimBus()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("Billing", builder =>
            builder.WithCircuitBreaker(options => options.MinimumThroughput = 7));

        using var provider = services.BuildServiceProvider();
        var breaker = provider.GetRequiredService<IEndpointCircuitBreaker>();
        var pipeline = provider.GetRequiredService<MessagePipeline>();

        Assert.AreEqual("Billing", breaker.Endpoint);
        Assert.AreSame(breaker, provider.GetRequiredService<IEndpointCircuitBreaker>());
        Assert.IsTrue(pipeline.HasBehaviors);
    }

    [TestMethod]
    public void WithCircuitBreaker_on_second_same_endpoint_registration_throws()
    {
        // Same-endpoint re-registration is benign for handlers, but a breaker
        // configured only on the later call would be silently discarded — the
        // operator would believe protection is active while nothing pauses.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("Billing", _ => { });

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusSubscriber("Billing", builder => builder.WithCircuitBreaker(_ => { })));

        StringAssert.Contains(exception.Message, "first AddNimBusSubscriber");
    }

    [TestMethod]
    public void Subscriber_without_WithCircuitBreaker_registers_no_breaker()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("Billing", _ => { });

        using var provider = services.BuildServiceProvider();

        Assert.IsNull(provider.GetService<IEndpointCircuitBreaker>());
    }

    [TestMethod]
    public void WithCircuitBreaker_with_preregistered_subscriber_client_fails_before_sidecars()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<ISubscriberClient>(new FakeSubscriberClient());

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusSubscriber("Billing", builder => builder.WithCircuitBreaker(_ => { })));

        StringAssert.Contains(exception.Message, "WithCircuitBreaker");
        Assert.IsNull(services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(IEndpointCircuitBreaker)));
    }

    [TestMethod]
    public async Task Subscriber_client_registered_after_WithCircuitBreaker_fails_startup_validation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("Billing", builder => builder.WithCircuitBreaker(_ => { }));
        services.AddSingleton<ISubscriberClient>(new FakeSubscriberClient());

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .OfType<CircuitBreakerSubscriberStartupValidator>()
            .Single();

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => validator.StartAsync(CancellationToken.None));
        StringAssert.Contains(exception.Message, "WithCircuitBreaker");
    }

    [TestMethod]
    public async Task Circuit_breaker_startup_validation_accepts_the_NimBus_composed_subscriber()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("Billing", builder => builder.WithCircuitBreaker(_ => { }));

        using var provider = services.BuildServiceProvider();
        var validator = provider.GetServices<IHostedService>()
            .OfType<CircuitBreakerSubscriberStartupValidator>()
            .Single();

        await validator.StartAsync(CancellationToken.None);
    }

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_different_endpoint_throws()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });

        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            services.AddNimBusSubscriber("EndpointB", _ => { }));

        StringAssert.Contains(ex.Message, "EndpointA");
        StringAssert.Contains(ex.Message, "EndpointB");
    }

    [TestMethod]
    public void Second_AddNimBusSubscriber_with_same_endpoint_is_benign()
    {
        // Idempotent registration is harmless — TryAdd would keep the first
        // ISubscriberClient anyway. The guard only fires on a *different*
        // endpoint, where the silent-drop bug was real.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", _ => { });
        services.AddNimBusSubscriber("EndpointA", _ => { });
    }

    [TestMethod]
    public void AddNimBusSubscriber_WithFailureDispositions_WiresRegisteredClassifier()
    {
        var classifier = new SpyFailureDispositionClassifier();
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber(
            "EndpointA",
            builder => builder.WithFailureDispositions(classifier));

        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var adapter = GetPrivateField(subscriber, "_serviceBusAdapter");
        var handler = GetPrivateField(adapter, "_messageHandler");
        var wired = GetPrivateField(handler, "_failureDispositionClassifier");

        Assert.AreSame(classifier, wired);
    }

    [TestMethod]
    public void ConfigurePermanentFailureClassifier_wires_it_as_the_disposition_classifier()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber(
            "EndpointA",
            builder => builder.ConfigurePermanentFailureClassifier(c => c.AddPermanentExceptionType<TimeoutException>()));

        var wired = WiredDispositionClassifier(services);

        var permanent = wired as DefaultPermanentFailureClassifier;
        Assert.IsNotNull(permanent, "ConfigurePermanentFailureClassifier must wire a DefaultPermanentFailureClassifier.");
        Assert.AreEqual(FailureDisposition.DeadLetter, permanent.Classify(new TimeoutException(), "E", "EndpointA"));
        Assert.AreEqual(FailureDisposition.Retry, permanent.Classify(new InvalidOperationException(), "E", "EndpointA"));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void WithFailureDispositions_takes_precedence_over_ConfigurePermanentFailureClassifier(bool explicitFirst)
    {
        var explicitClassifier = new SpyFailureDispositionClassifier();
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddNimBusSubscriber("EndpointA", builder =>
        {
            if (explicitFirst)
                builder.WithFailureDispositions(explicitClassifier).ConfigurePermanentFailureClassifier(_ => { });
            else
                builder.ConfigurePermanentFailureClassifier(_ => { }).WithFailureDispositions(explicitClassifier);
        });

        Assert.AreSame(explicitClassifier, WiredDispositionClassifier(services));
    }

    private static object WiredDispositionClassifier(ServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var adapter = GetPrivateField(subscriber, "_serviceBusAdapter");
        var handler = GetPrivateField(adapter, "_messageHandler");
        return GetPrivateField(handler, "_failureDispositionClassifier");
    }

    [TestMethod]
    public async Task AddNimBusSubscriber_ScopedHandler_UsesPerMessageScopeWithValidateScopes()
    {
        var probes = new List<ScopedRegistrationProbe>();
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddScoped(_ =>
        {
            var probe = new ScopedRegistrationProbe();
            probes.Add(probe);
            return probe;
        });
        services.AddNimBusSubscriber(
            "EndpointA",
            builder => builder.AddHandler<ScopedRegistrationEvent, ScopedRegistrationHandler>());

        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
        });
        var subscriber = provider.GetRequiredService<ISubscriberClient>();
        var eventHandlerProvider = (EventHandlerProvider)GetPrivateField(subscriber, "_eventHandlerProvider");

        await eventHandlerProvider.Handle(
            MessageContextStub.ForEventType(nameof(ScopedRegistrationEvent), "{}"));

        Assert.AreEqual(1, probes.Count);
        Assert.IsTrue(probes[0].IsDisposed);
    }

    [TestMethod]
    public void AddNimBusOutboxDispatcher_without_OutboxDispatcherSender_throws_actionable_message()
    {
        // The fail-fast message must name the real registration path.
        // OutboxDispatcherSender is NOT registered by AddNimBusPublisher — the
        // consumer registers it themselves as a singleton.
        var services = new ServiceCollection();
        services.AddSingleton(new ServiceBusClient(FakeConnection));
        services.AddSingleton<IOutbox>(new NoopOutbox());
        services.AddNimBusOutboxDispatcher();

        using var provider = services.BuildServiceProvider();

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => provider.GetServices<IHostedService>().ToList());

        StringAssert.Contains(ex.Message, "OutboxDispatcherSender");
        Assert.IsFalse(
            ex.Message.Contains("Register AddNimBusPublisher before", StringComparison.Ordinal),
            "Message must not claim AddNimBusPublisher registers OutboxDispatcherSender.");
        StringAssert.Contains(ex.Message, "AddSingleton");
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(field, $"Expected private field '{fieldName}' on {target.GetType().Name}.");
        return field.GetValue(target)!;
    }

    private sealed class SpyFailureDispositionClassifier : IFailureDispositionClassifier
    {
        public FailureDisposition Classify(Exception exception, string eventTypeId, string? endpointName) =>
            FailureDisposition.Retry;
    }

    public sealed class ScopedRegistrationEvent : Event
    {
    }

    public sealed class ScopedRegistrationHandler : IEventHandler<ScopedRegistrationEvent>
    {
        private readonly ScopedRegistrationProbe _probe;

        public ScopedRegistrationHandler(ScopedRegistrationProbe probe)
        {
            _probe = probe;
        }

        public Task Handle(
            ScopedRegistrationEvent message,
            IEventHandlerContext context,
            CancellationToken cancellationToken = default)
        {
            Assert.IsFalse(_probe.IsDisposed);
            return Task.CompletedTask;
        }
    }

    public sealed class ScopedRegistrationProbe : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopOutbox : IOutbox
    {
        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(Array.Empty<OutboxMessage>());
        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSubscriberClient : ISubscriberClient
    {
        public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> eventHandlerFactory) where TEvent : IEvent { }
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
