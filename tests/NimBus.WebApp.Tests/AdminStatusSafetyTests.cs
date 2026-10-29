#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class AdminStatusSafetyTests
{
    private const string EndpointId = "endpoint-a";
    private const string EventId = "event-a";
    private const string SessionId = "session-a";
    private static readonly string[] TerminalStatusNames =
    {
        nameof(StoreResolutionStatus.Completed),
        nameof(StoreResolutionStatus.Skipped),
    };

    [TestMethod]
    [DataRow("delete-by-status", "{}")]
    [DataRow("delete-by-status", "{\"statuses\":null}")]
    [DataRow("delete-by-status", "{\"statuses\":[]}")]
    [DataRow("delete-by-status", "{\"statuses\":[\"NotAStatus\"]}")]
    [DataRow("delete-by-status", "{\"statuses\":[999]}")]
    [DataRow("skip", "{}")]
    [DataRow("skip", "{\"statuses\":null}")]
    [DataRow("skip", "{\"statuses\":[]}")]
    [DataRow("skip", "{\"statuses\":[\"Completed\"]}")]
    [DataRow("skip", "{\"statuses\":[\"Skipped\"]}")]
    [DataRow("skip", "{\"statuses\":[\"Pending\"]}")]
    [DataRow("skip", "{\"statuses\":[\"TooManyRequests\"]}")]
    [DataRow("skip", "{\"statuses\":[\"Published\"]}")]
    [DataRow("skip", "{\"statuses\":[\"Deferred\"]}")]
    public async Task Authenticated_http_request_with_invalid_statuses_returns_bad_request_without_mutation(
        string operation,
        string json)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);

        using var host = await CreateHost(store);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/endpoint/{EndpointId}/{operation}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        using var client = host.GetTestServer().CreateClient();
        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Authenticated_http_request_with_valid_skip_status_sends_a_skip_for_the_matching_event()
    {
        var store = new InMemoryMessageStore();
        var skip = BulkSkipHarness.Create(EndpointId, store, new FakePlatform(EndpointId));
        await skip.SeedFailedAsync(EventId);

        using var host = await CreateHost(store, skip.Sut);
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/admin/endpoint/{EndpointId}/skip")
        {
            Content = new StringContent(
                "{\"statuses\":[\"Failed\"]}",
                Encoding.UTF8,
                "application/json"),
        };
        using var client = host.GetTestServer().CreateClient();

        using var response = await client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        CollectionAssert.AreEqual(new[] { EventId }, skip.Manager.Skipped.ToArray());
    }

    [TestMethod]
    public async Task Skip_leaves_prefix_sibling_endpoint_events_untouched()
    {
        const string siblingEndpointId = EndpointId + "b";
        const string siblingEventId = "event-b";
        var store = new InMemoryMessageStore();
        var skip = BulkSkipHarness.Create(EndpointId, store, new FakePlatform(EndpointId));
        await skip.SeedFailedAsync(EventId);
        await store.UploadFailedMessage(
            siblingEventId,
            SessionId,
            siblingEndpointId,
            new UnresolvedEvent
            {
                EventId = siblingEventId,
                SessionId = SessionId,
                EndpointId = siblingEndpointId,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });

        var result = await skip.Sut.SkipMessagesAsync(
            EndpointId,
            new List<string> { nameof(StoreResolutionStatus.Failed) },
            before: null);

        Assert.AreEqual(1, result.Succeeded);
        CollectionAssert.AreEqual(new[] { EventId }, skip.Manager.Skipped.ToArray());
        Assert.AreEqual(StoreResolutionStatus.Failed, (await store.GetEvent(siblingEndpointId, siblingEventId))!.ResolutionStatus);
        Assert.IsNull(await store.GetEvent(EndpointId, siblingEventId), "The sibling's event must not be rewritten under the skipped endpoint.");
    }

    [TestMethod]
    public async Task Controller_rejects_null_delete_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminDeleteByStatusAsync(
            EndpointId,
            new DeleteByStatusRequest { Statuses = null! });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    public async Task Controller_rejects_empty_delete_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminDeleteByStatusPreviewAsync(
            EndpointId,
            new DeleteByStatusRequest { Statuses = new List<AdminDeleteStatus>() });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    public async Task Controller_rejects_empty_skip_statuses_before_calling_service()
    {
        var service = new ThrowingAdminService();
        var sut = CreateController(service);

        var result = await sut.PostAdminSkipAsync(
            EndpointId,
            new SkipRequest { Statuses = new List<AdminSkipSourceStatus>() });

        Assert.IsInstanceOfType<BadRequestObjectResult>(result.Result);
        Assert.AreEqual(0, service.CallCount);
    }

    [TestMethod]
    [DataRow("delete-preview")]
    [DataRow("delete")]
    [DataRow("skip-preview")]
    [DataRow("skip")]
    public async Task Service_boundary_rejects_empty_statuses_without_mutating_event(string operation)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(() => InvokeOperation(sut, operation, new List<string>()));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored!.ResolutionStatus);
    }

    [TestMethod]
    [DataRow("NotAStatus")]
    [DataRow("1")]
    public async Task Service_boundary_rejects_unknown_delete_status_without_mutating_event(string status)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.DeleteByStatusAsync(EndpointId, new List<string> { status }));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored!.ResolutionStatus);
    }

    [TestMethod]
    public async Task Service_boundary_rejects_null_statuses_without_mutating_event()
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.DeleteByStatusAsync(EndpointId, null!));
        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.SkipMessagesAsync(EndpointId, null!, before: null));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored!.ResolutionStatus);
    }

    [TestMethod]
    [DataRow(nameof(StoreResolutionStatus.Failed))]
    [DataRow(nameof(StoreResolutionStatus.DeadLettered))]
    [DataRow(nameof(StoreResolutionStatus.Unsupported))]
    public void Skip_validation_accepts_each_supported_source_status(string status)
    {
        var valid = AdminStatusValidation.TryNormalizeSkipStatuses(
            new[] { status },
            out var normalized,
            out var error);

        Assert.IsTrue(valid, error);
        CollectionAssert.AreEqual(new[] { status }, normalized);
    }

    // A skip settles neither of these. A Deferred event's parked copy replays and runs once its
    // session's block clears; a Pending, TooManyRequests or Published message is still on its way.
    [TestMethod]
    [DataRow(nameof(StoreResolutionStatus.Deferred))]
    [DataRow(nameof(StoreResolutionStatus.Pending))]
    [DataRow(nameof(StoreResolutionStatus.TooManyRequests))]
    [DataRow(nameof(StoreResolutionStatus.Published))]
    public void Skip_validation_rejects_statuses_a_skip_cannot_settle(string status)
    {
        var valid = AdminStatusValidation.TryNormalizeSkipStatuses(
            new[] { status },
            out _,
            out var error);

        Assert.IsFalse(valid);
        StringAssert.Contains(error, status, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Bulk_skip_source_statuses_are_ones_the_skip_command_accepts()
    {
        // Bulk skip sends each skip as a Web UI operator command, which refuses any other status.
        foreach (var status in SkipRules.BulkSkipSourceStatuses)
        {
            Assert.IsTrue(
                OperatorCommandCoordinator.IsActionable(new UnresolvedEvent { ResolutionStatus = status }, OperatorChannel.WebApp),
                $"{status} cannot be skipped by the operator command.");
        }
    }

    [TestMethod]
    public void Generated_admin_status_contract_matches_message_store_status_rules()
    {
        // The skip enum keeps every non-terminal status for compatibility; the service rejects
        // the ones a skip cannot settle.
        var allStoreStatuses = Enum.GetNames<StoreResolutionStatus>();
        var nonTerminalStoreStatuses = allStoreStatuses
            .Except(TerminalStatusNames, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEquivalent(allStoreStatuses, Enum.GetNames<AdminDeleteStatus>());
        CollectionAssert.AreEquivalent(nonTerminalStoreStatuses, Enum.GetNames<AdminSkipSourceStatus>());
    }

    [TestMethod]
    [DataRow(nameof(StoreResolutionStatus.Completed))]
    [DataRow(nameof(StoreResolutionStatus.Skipped))]
    public async Task Service_boundary_rejects_terminal_status_without_mutating_event(string status)
    {
        var store = new InMemoryMessageStore();
        await SeedFailedEvent(store);
        var sut = CreateAdminService(store);

        await Assert.ThrowsExactlyAsync<ArgumentException>(
            () => sut.SkipMessagesAsync(
                EndpointId,
                new List<string> { status },
                before: null));

        var stored = await store.GetEvent(EndpointId, EventId);
        Assert.AreEqual(StoreResolutionStatus.Failed, stored!.ResolutionStatus);
    }

    private static Task InvokeOperation(AdminService service, string operation, List<string> statuses) =>
        operation switch
        {
            "delete-preview" => AsTask(service.DeleteByStatusPreviewAsync(EndpointId, statuses)),
            "delete" => service.DeleteByStatusAsync(EndpointId, statuses),
            "skip-preview" => AsTask(service.SkipMessagesPreviewAsync(EndpointId, statuses, before: null)),
            "skip" => service.SkipMessagesAsync(EndpointId, statuses, before: null),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unknown operation."),
        };

    private static async Task AsTask<T>(Task<T> task) => await task;

    private static async Task SeedFailedEvent(InMemoryMessageStore store)
    {
        await store.UploadFailedMessage(
            EventId,
            SessionId,
            EndpointId,
            new UnresolvedEvent
            {
                EventId = EventId,
                SessionId = SessionId,
                EndpointId = EndpointId,
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1),
            });
    }

    private static Task<IHost> CreateHost(InMemoryMessageStore store) => CreateHost(store, CreateAdminService(store));

    private static async Task<IHost> CreateHost(InMemoryMessageStore store, AdminService adminService)
    {
        var platform = new FakePlatform(EndpointId);

        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IPlatform>(platform);
                    services.AddSingleton<IAdminService>(adminService);
                    // Not exercised by these status-validation routes, but the controller
                    // takes it, so activation needs something to resolve.
                    services.AddSingleton<ISubscriptionAdminService>(new ThrowingSubscriptionAdminService());
                    // Same: the heartbeat routes are not under test here, but the
                    // controller's constructor has to resolve.
                    services.AddScoped<NimBus.WebApp.Services.Heartbeat.IHeartbeatService>(sp =>
                        new NimBus.WebApp.Services.Heartbeat.HeartbeatService(
                            platform,
                            store,
                            store,
                            new UnusedHeartbeatMessageSender(),
                            NullLogger<NimBus.WebApp.Services.Heartbeat.HeartbeatService>.Instance));
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IAuditLogService, NoOpAuditLogService>();
                    // Real resolution over the same in-memory store: the injected
                    // EIP_Management claim authorizes via the compat union.
                    services.AddSingleton<IAccessControlSnapshotProvider>(
                        new AccessControlSnapshotProvider(store, NullLogger<AccessControlSnapshotProvider>.Instance));
                    services.AddScoped<IEndpointAuthorizationService, EndpointAuthorizationService>();
                    services.AddScoped<IAdminApiController, AdminImplementation>();
                    services.AddControllers()
                        .AddApplicationPart(typeof(AdminApiController).Assembly)
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        var identity = new ClaimsIdentity(
                            new[] { new Claim("groups", "EIP_Management") },
                            authenticationType: "Test");
                        context.User = new ClaimsPrincipal(identity);
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        return await builder.StartAsync();
    }

    private static AdminImplementation CreateController(IAdminService service)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim("groups", "EIP_Management") },
                authenticationType: "Test")),
        };

        var accessor = new HttpContextAccessor { HttpContext = context };
        var platform = new FakePlatform(EndpointId);
        var configuration = new ConfigurationBuilder().Build();
        var authorizationService = new EndpointAuthorizationService(
            accessor,
            platform,
            NullLogger<EndpointAuthorizationService>.Instance,
            configuration,
            new AccessControlSnapshotProvider(new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance));

        return new AdminImplementation(
            accessor,
            service,
            subscriptionAdminService: null!,
            platform,
            configuration,
            new NoOpAuditLogService(),
            authorizationService,
            heartbeatService: null!);
    }

    private static AdminService CreateAdminService(InMemoryMessageStore store) =>
        new(
            platform: null!,
            messageStore: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: null!,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);

    private sealed class UnusedHeartbeatMessageSender : NimBus.WebApp.Services.Heartbeat.IHeartbeatMessageSender
    {
        public System.Threading.Tasks.Task SendAsync(
            string topicName,
            NimBus.Core.Messages.Message message,
            System.Threading.CancellationToken cancellationToken = default)
            => throw new NotSupportedException("These tests never exercise a heartbeat route.");
    }

    private sealed class ThrowingSubscriptionAdminService : ISubscriptionAdminService
    {
        private static Task<T> Unexpected<T>() =>
            throw new AssertFailedException("The controller reached the subscription admin for invalid input.");

        public Task<IEnumerable<ServiceBusTopicOverview>> GetTopicOverviewAsync() => Unexpected<IEnumerable<ServiceBusTopicOverview>>();
        public Task<SubscriptionActionResult> DeleteTopicAsync(string topicName) => Unexpected<SubscriptionActionResult>();
        public Task<IEnumerable<ServiceBusSubscriptionInfo>> GetSubscriptionsAsync(string topicName) => Unexpected<IEnumerable<ServiceBusSubscriptionInfo>>();
        public Task<DeadLetterOverview> GetResolverDeadLettersAsync(string subscriptionName, System.Threading.CancellationToken cancellationToken = default) => Unexpected<DeadLetterOverview>();
        public Task<BulkOperationResult> ResubmitResolverDeadLettersAsync(string subscriptionName, bool all, string? reason, System.Threading.CancellationToken cancellationToken = default) => Unexpected<BulkOperationResult>();
        public Task<SubscriptionActionResult> SetSubscriptionStatusAsync(string topicName, string subscriptionName, bool enable) => Unexpected<SubscriptionActionResult>();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string topicName, string subscriptionName) => Unexpected<BulkOperationResult>();
        public Task<BulkOperationResult> PurgeDeadLetterQueueAsync(string topicName, string subscriptionName) => Unexpected<BulkOperationResult>();
        public Task<SubscriptionActionResult> RecreateSubscriptionAsync(string topicName, string subscriptionName) => Unexpected<SubscriptionActionResult>();
        public Task<SubscriptionActionResult> DeleteSubscriptionAsync(string topicName, string subscriptionName) => Unexpected<SubscriptionActionResult>();
        public Task<SubscriptionActionResult> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName) => Unexpected<SubscriptionActionResult>();
        public Task<SubscriptionActionResult> RestoreRulesAsync(string topicName, string subscriptionName) => Unexpected<SubscriptionActionResult>();
    }

    private sealed class ThrowingAdminService : IAdminService
    {
        public int CallCount { get; private set; }

        private Task<T> Unexpected<T>()
        {
            CallCount++;
            throw new AssertFailedException("The controller called IAdminService for invalid input.");
        }

        public Task<PlatformConfig> GetPlatformConfigAsync(IPlatform platform) => Unexpected<PlatformConfig>();
        public Task<TopologyAuditResult> AuditTopologyAsync(string endpointName) => Unexpected<TopologyAuditResult>();
        public Task<TopologyCleanupResult> RemoveDeprecatedTopologyAsync(string endpointName) => Unexpected<TopologyCleanupResult>();
        public Task<BulkResubmitPreview> PreviewFailedMessagesAsync(string endpointId) => Unexpected<BulkResubmitPreview>();
        public Task<BulkOperationResult> BulkResubmitFailedAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<int> GetDeadLetteredCountAsync(string endpointId) => Unexpected<int>();
        public Task<DeadLetterQueueCounts?> GetDeadLetterQueueCountsAsync(string endpointId) => Unexpected<DeadLetterQueueCounts?>();
        public Task<BulkOperationResult> DeleteDeadLetteredAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<SessionPurgePreview> PreviewSessionPurgeAsync(string endpointId, string sessionId) => Unexpected<SessionPurgePreview>();
        public Task<SessionPurgeResult> PurgeSessionAsync(string endpointId, string sessionId) => Unexpected<SessionPurgeResult>();
        public Task<bool> DeleteEventAsync(string endpointId, string eventId) => Unexpected<bool>();
        public Task<BulkOperationResult> DeleteAllEventsAsync(string endpointId) => Unexpected<BulkOperationResult>();
        public Task<PurgePreview> PurgeSubscriptionPreviewAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<PurgePreview>();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string endpointId, string subscription, List<string> states, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteMessagesByToPreviewAsync(string toField) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteMessagesByToAsync(string toField) => Unexpected<BulkOperationResult>();
        public Task<int> DeleteByStatusPreviewAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<int> CountSessionBlockingEventsAsync(string endpointId, List<string> statuses) => Unexpected<int>();
        public Task<BulkOperationResult> DeleteByStatusAsync(string endpointId, List<string> statuses) => Unexpected<BulkOperationResult>();
        public Task<int> SkipMessagesPreviewAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<int>();
        public Task<BulkOperationResult> SkipMessagesAsync(string endpointId, List<string> statuses, DateTime? before) => Unexpected<BulkOperationResult>();
        public Task<CopyResult> CopyEndpointDataAsync(string endpointId, string targetConnectionString, DateTime? from, DateTime? to, List<string> statuses, int? batchSize) => Unexpected<CopyResult>();
        public Task<DeferredReprocessResult> ReprocessDeferredAsync(string endpointId, string sessionId) => Unexpected<DeferredReprocessResult>();
        public Task<StalePendingPreview> PreviewStalePendingAsync(string endpointId, DateTime? enqueuedBefore, int maxRows) => Unexpected<StalePendingPreview>();
        public Task<StalePendingReconcileResult> ReconcileStalePendingAsync(string endpointId, DateTime enqueuedBefore, int? maxRepairs, string auditorName, string? note) => Unexpected<StalePendingReconcileResult>();
    }

    private sealed class NoOpAuditLogService : IAuditLogService
    {
        public Task LogAuditAsync(
            MessageAuditType type,
            HttpContext context,
            bool accessDenied = false,
            string? data = null,
            string? eventId = null,
            string? endpointId = null,
            string? eventTypeId = null,
            string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePlatform : IPlatform
    {
        public FakePlatform(string endpointId) => Endpoints = new[] { new FakeEndpoint(endpointId) };

        public IEnumerable<IEndpoint> Endpoints { get; }
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id) => Id = id;

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();
        public IEnumerable<IEventType> EventTypesConsumed => Enumerable.Empty<IEventType>();
        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }
}
