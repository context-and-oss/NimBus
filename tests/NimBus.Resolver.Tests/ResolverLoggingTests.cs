#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;
using static NimBus.Resolver.Tests.ResolverTestMessages;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver's operator-facing log lines. On SQL deployments the store cannot log, so these
/// are the only attribution for an ignored stale copy, a store retry or a dead-lettered
/// message: each outcome must be logged at the level operators alert on.
/// </summary>
[TestClass]
public class ResolverLoggingTests
{
    private const string Endpoint = "BillingEndpoint";

    [TestMethod]
    public async Task AppliedOutcome_IsLoggedAsInformation()
    {
        var logger = new RecordingLogger();
        var service = new ResolverService(new FakeCosmosDbClient(), logger: logger);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        Assert.IsTrue(logger.Has(LogLevel.Information, "Updated Endpoint"));
    }

    [TestMethod]
    public async Task StaleCopy_IsLoggedAsAWarningWithItsAttribution()
    {
        var logger = new RecordingLogger();
        var service = new ResolverService(new FakeCosmosDbClient { PendingUploadResult = false }, logger: logger);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, throttleRetryCount: 2, deliveryCount: 3));

        var entry = logger.Entries.Single(e => e.Level == LogLevel.Warning && e.Message.Contains("Ignored stale", StringComparison.Ordinal));
        StringAssert.Contains(entry.Message, "ThrottleRetryCount:2");
        StringAssert.Contains(entry.Message, "DeliveryCount:3");
    }

    [TestMethod]
    public async Task StaleCopyAuditFailure_IsLoggedAsAWarning()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient
        {
            PendingUploadResult = false,
            StoreAuditException = new InvalidOperationException("audit store down"),
        };
        var service = new ResolverService(store, logger: logger);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        Assert.IsTrue(logger.Has(LogLevel.Warning, "Failed to record the stale-copy audit"));
    }

    [TestMethod]
    public async Task StoreRetry_IsLoggedAsAWarningThenTheRescheduleAsInformation()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient { StoreMessageException = new StorageProviderTransientException("sql failover") };
        var service = new ResolverService(store, logger: logger);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        Assert.IsTrue(logger.Has(LogLevel.Warning, "Store write failed"));
        Assert.IsTrue(logger.Has(LogLevel.Information, "Scheduling redelivery"));
    }

    [TestMethod]
    public async Task ExhaustedStoreRetries_AreLoggedAsAnError()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient { StoreMessageException = new StorageProviderTransientException("sql failover") };
        var service = new ResolverService(store, logger: logger);

        await service.Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint, throttleRetryCount: 9));

        Assert.IsTrue(logger.Has(LogLevel.Error, "exhausted the delivery budget"));
    }

    [TestMethod]
    public async Task UnschedulableRetry_IsLoggedBeforeAbandoning()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient { StoreMessageException = new StorageProviderTransientException("sql failover") };
        var message = CreateMessageContext(MessageType.EventRequest, to: Endpoint);
        message.ScheduleRedeliveryException = new TransientException("scheduler unavailable");
        var service = new ResolverService(store, logger: logger);

        await service.Handle(message);

        Assert.IsTrue(logger.Has(LogLevel.Information, "Failed to schedule redelivery"));
        Assert.AreEqual(1, message.AbandonCalls);
    }

    [TestMethod]
    public async Task UnexpectedAndTransientFailures_AreLoggedAsErrors()
    {
        var logger = new RecordingLogger();

        await new ResolverService(new FakeCosmosDbClient { StoreMessageException = new InvalidOperationException("boom") }, logger: logger)
            .Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));
        await new ResolverService(new FakeCosmosDbClient { StoreMessageException = new TransientException("blip") }, logger: logger)
            .Handle(CreateMessageContext(MessageType.EventRequest, to: Endpoint));

        Assert.IsTrue(logger.Has(LogLevel.Error, "add to DeadLetter"));
        Assert.IsTrue(logger.Has(LogLevel.Error, "Transient exception"));
    }

    [TestMethod]
    public async Task NotificationFailure_IsLoggedAsAWarning()
    {
        var logger = new RecordingLogger();
        var notifier = new ResolverHeartbeatTests.RecordingNotifier
        {
            HeartbeatException = new InvalidOperationException("hub down"),
            ServiceHealthException = new InvalidOperationException("hub down"),
        };
        var store = new FakeCosmosDbClient();
        var service = new ResolverService(store, notifier, logger, metadataStore: store, serviceHealthStore: store);

        await service.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint));
        await service.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.EventRequest, to: Constants.ResolverId, from: Constants.ManagerId));

        Assert.IsTrue(logger.Has(LogLevel.Warning, "heartbeat notification failed"));
        Assert.IsTrue(logger.Has(LogLevel.Warning, "service health notification failed"));
    }

    [TestMethod]
    public async Task HeartbeatTraffic_IsLoggedAtTheExpectedLevels()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient();
        var withStores = new ResolverService(store, logger: logger, metadataStore: store, serviceHealthStore: store);
        var withoutStores = new ResolverService(store, logger: logger);

        await withStores.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint));
        await withStores.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.EventRequest, to: Constants.ResolverId, from: Constants.ManagerId));
        await withoutStores.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint));
        await withoutStores.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.EventRequest, to: Constants.ResolverId, from: Constants.ManagerId));
        var anonymous = ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: "");
        anonymous.MessageContent.EventContent!.EventJson = "{}";
        await withStores.Handle(anonymous);

        Assert.IsTrue(logger.Has(LogLevel.Information, "Updated Heartbeat"));
        Assert.IsTrue(logger.Has(LogLevel.Information, "Answered liveness probe"));
        Assert.IsTrue(logger.Has(LogLevel.Warning, "heartbeat store not configured"));
        Assert.IsTrue(logger.Has(LogLevel.Warning, "service health store not configured"));
        Assert.IsTrue(logger.Has(LogLevel.Warning, "Heartbeat response without endpoint"));
    }

    [TestMethod]
    public async Task HeartbeatStoreOutage_IsLoggedAndLeftForRedelivery()
    {
        var logger = new RecordingLogger();
        var store = new FakeCosmosDbClient
        {
            SetHeartbeatException = new StorageProviderTransientException("sql failover"),
            SetServiceHealthException = new StorageProviderTransientException("sql failover"),
        };
        var service = new ResolverService(store, logger: logger, metadataStore: store, serviceHealthStore: store);

        await service.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.ResolutionResponse, to: Constants.ResolverId, from: Endpoint));
        await service.Handle(ResolverHeartbeatTests.CreateHeartbeatContext(MessageType.EventRequest, to: Constants.ResolverId, from: Constants.ManagerId));

        Assert.IsTrue(logger.Has(LogLevel.Information, "will reprocess heartbeat"));
        Assert.IsTrue(logger.Has(LogLevel.Information, "will reprocess liveness probe"));
    }

    private sealed class RecordingLogger : ILogger<ResolverService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public bool Has(LogLevel level, string fragment) =>
            Entries.Any(e => e.Level == level && e.Message.Contains(fragment, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
