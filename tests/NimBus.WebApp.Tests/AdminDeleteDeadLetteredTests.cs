#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Core;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using DeadLetteredPreview = NimBus.WebApp.ManagementApi.Response2;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #286: a DeadLettered row stands for a message the endpoint dead-lettered on its own
/// subscription. Operations → Delete dead-lettered deleted the rows and left every message in
/// the subscription's dead-letter queue, which Service Bus never empties on its own. It now
/// completes the queue messages of the rows it deletes, matched on their EventId, and leaves
/// every other message for Admin → Subscriptions → Purge dead letters.
/// </summary>
[TestClass]
public sealed class AdminDeleteDeadLetteredTests
{
    private const string Endpoint = "Crm";
    private static readonly string[] Orphan = { "orphan-1" };

    [TestMethod]
    public async Task Delete_dead_lettered_completes_the_queue_messages_of_the_rows_it_deletes()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        await harness.SeedDeadLetteredAsync("evt-2");
        harness.Bus.DeadLetters.Add("evt-1", "orphan-1", "evt-2");

        var result = await harness.Sut.DeleteDeadLetteredAsync(Endpoint);

        Assert.AreEqual(4, result.Succeeded, string.Join("; ", result.Errors ?? new List<string>()));
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual((Endpoint, Endpoint, SubQueue.DeadLetter), harness.Bus.Receivers.Single(),
            "The messages are completed on the dead-letter queue of the endpoint's own subscription.");
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "evt-1"));
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "evt-2"));
    }

    // A message with no stored row may be one NimBus never showed as dead-lettered, such as one
    // Service Bus dead-lettered after too many deliveries. Only Purge dead letters removes those.
    [TestMethod]
    public async Task Delete_dead_lettered_leaves_queue_messages_that_match_no_row_in_the_queue()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        harness.Bus.DeadLetters.Add("evt-1", "orphan-1");

        await harness.Sut.DeleteDeadLetteredAsync(Endpoint);

        CollectionAssert.AreEqual(Orphan, harness.Bus.DeadLetters.RemainingEventIds.ToArray());
        Assert.AreEqual(1, harness.Bus.DeadLetters.Abandoned, "The unmatched message is released, not left locked.");
    }

    [TestMethod]
    public async Task Delete_dead_lettered_does_not_touch_the_queue_when_no_row_is_dead_lettered()
    {
        var harness = new Harness();
        harness.Bus.DeadLetters.Add("orphan-1");

        var result = await harness.Sut.DeleteDeadLetteredAsync(Endpoint);

        Assert.AreEqual(0, result.Processed);
        Assert.AreEqual(0, harness.Bus.Receivers.Count);
    }

    [TestMethod]
    public async Task Delete_dead_lettered_keeps_the_rows_when_the_dead_letter_queue_cannot_be_read()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        harness.Bus.DeadLetters.Failure = new ServiceBusException("Subscription is ReceiveDisabled", ServiceBusFailureReason.MessagingEntityDisabled);

        var result = await harness.Sut.DeleteDeadLetteredAsync(Endpoint);

        Assert.AreEqual(0, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        StringAssert.Contains(result.Errors!.Single(), "dead-letter queue", StringComparison.Ordinal);
        Assert.AreEqual(ResolutionStatus.DeadLettered, (await harness.Store.GetEvent(Endpoint, "evt-1"))!.ResolutionStatus,
            "Deleting the row while its message stays in the queue is the bug: keep it so a rerun finds it.");
    }

    [TestMethod]
    public async Task Delete_dead_lettered_counts_the_queue_messages_it_removed_before_a_failure()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        await harness.SeedDeadLetteredAsync("evt-2");
        harness.Bus.DeadLetters.Add("evt-1", "evt-2");
        harness.Bus.DeadLetters.FailCompletion = 2;

        var result = await harness.Sut.DeleteDeadLetteredAsync(Endpoint);

        Assert.AreEqual(1, result.Succeeded, "The message completed before the failure is gone and must be counted.");
        Assert.AreEqual(1, result.Failed);
        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, "evt-2"), "The rows stay for a rerun.");
    }

    [TestMethod]
    public async Task Dead_letter_queue_counts_give_the_total_and_the_messages_of_stored_rows()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        harness.Bus.DeadLetters.Add("evt-1", "orphan-1", "orphan-2");
        harness.Admin.DeadLetterCount = 3;

        var counts = await harness.Sut.GetDeadLetterQueueCountsAsync(Endpoint);

        Assert.AreEqual(new DeadLetterQueueCounts(3, 1), counts);
        Assert.AreEqual((Endpoint, Endpoint), harness.Admin.Requested.Single());
        Assert.AreEqual(3, harness.Bus.DeadLetters.RemainingEventIds.Count, "Counting only peeks.");
    }

    [TestMethod]
    public async Task Dead_letter_queue_counts_are_unknown_when_service_bus_cannot_be_read()
    {
        var harness = new Harness();
        harness.Admin.Fail = true;

        Assert.IsNull(await harness.Sut.GetDeadLetterQueueCountsAsync(Endpoint));
    }

    [TestMethod]
    public async Task Preview_reports_the_stored_rows_and_the_dead_letter_queue()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        harness.Bus.DeadLetters.Add("evt-1", "orphan-1", "orphan-2");
        harness.Admin.DeadLetterCount = 3;

        var response = await harness.CreateController().GetAdminDeadletteredPreviewAsync(Endpoint);

        var preview = (DeadLetteredPreview)((OkObjectResult)response.Result!).Value!;
        Assert.AreEqual(1, preview.Count);
        Assert.AreEqual(3L, preview.QueueCount);
        Assert.AreEqual(1, preview.MatchingQueueCount);
    }

    [TestMethod]
    public async Task Delete_dead_lettered_is_audited_with_what_it_removed()
    {
        var harness = new Harness();
        await harness.SeedDeadLetteredAsync("evt-1");
        harness.Bus.DeadLetters.Add("evt-1");

        await harness.CreateController().PostAdminDeleteDeadletteredAsync(Endpoint);

        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.PurgeMessages, audit.Type);
        Assert.AreEqual(Endpoint, audit.EndpointId);
        StringAssert.Contains(audit.Data, "\"operation\":\"delete-deadlettered\"", StringComparison.Ordinal);
        StringAssert.Contains(audit.Data, "\"Succeeded\":2", StringComparison.Ordinal);
    }

    // The messages Delete dead-lettered leaves behind go through Admin → Subscriptions → Purge
    // dead letters, audited like the other subscription actions.
    [TestMethod]
    public async Task Purge_dead_letters_empties_the_queue_and_is_audited_as_a_subscription_action()
    {
        var harness = new Harness();
        harness.Management.SeedSubscription(Endpoint, Endpoint, requiresSession: true);
        harness.Bus.DeadLetters.Add("orphan-1");

        var response = await harness.CreateController().PostAdminServicebusSubscriptionDeadlettersPurgeAsync(Endpoint, Endpoint);

        var result = (NimBus.WebApp.ManagementApi.BulkOperationResult)((OkObjectResult)response.Result!).Value!;
        Assert.AreEqual(1, result.Succeeded);
        Assert.AreEqual(0, harness.Bus.DeadLetters.RemainingEventIds.Count);
        var audit = harness.Audit.Logged.Single();
        Assert.AreEqual(MessageAuditType.ManageSubscription, audit.Type);
        StringAssert.Contains(audit.Data, "\"action\":\"purge-dead-letters\"", StringComparison.Ordinal);
    }

    private sealed class Harness
    {
        private readonly IPlatform _platform = new SingleEndpointPlatform();

        public Harness()
        {
            Subscriptions = new SubscriptionAdminService(
                _platform,
                Management,
                new FakeTopologyRebuilder(),
                sbClient: Bus,
                NullLogger<SubscriptionAdminService>.Instance);
            Sut = new AdminService(
                platform: _platform,
                messageStore: Store,
                capabilities: null!,
                sbAdmin: Admin,
                sbClient: Bus,
                managerClient: null!,
                subscriptionAdmin: null!,
                logger: NullLogger<AdminService>.Instance);
        }

        public InMemoryMessageStore Store { get; } = new();

        public FakeDeadLetterBus Bus { get; } = new();

        public FakeServiceBusManagement Management { get; } = new();

        public SubscriptionAdminService Subscriptions { get; }

        public RuntimeAdministration Admin { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public AdminService Sut { get; }

        public async Task SeedDeadLetteredAsync(string eventId) =>
            await OperatorCommandTestRows.SeedFailedRowAsync(Store, Endpoint, eventId, "sess-" + eventId, "term-" + eventId,
                ResolutionStatus.DeadLettered);

        /// <summary>The admin API as a Site Owner calls it.</summary>
        public AdminImplementation CreateController()
        {
            var accessor = new HttpContextAccessor
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim("groups", "EIP_Management") }, authenticationType: "Test")),
                },
            };
            var configuration = new ConfigurationBuilder().Build();
            var authorization = new EndpointAuthorizationService(
                accessor,
                _platform,
                NullLogger<EndpointAuthorizationService>.Instance,
                configuration,
                new AccessControlSnapshotProvider(Store, NullLogger<AccessControlSnapshotProvider>.Instance));

            return new AdminImplementation(
                accessor,
                Sut,
                subscriptionAdminService: Subscriptions,
                _platform,
                configuration,
                Audit,
                authorization,
                heartbeatService: null!);
        }
    }

    private sealed class SingleEndpointPlatform : Platform
    {
        public SingleEndpointPlatform() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }

    private sealed class RuntimeAdministration : ServiceBusAdministrationClient
    {
        public long DeadLetterCount { get; set; }

        public bool Fail { get; set; }

        public ConcurrentQueue<(string Topic, string Subscription)> Requested { get; } = new();

        public override Task<Response<SubscriptionRuntimeProperties>> GetSubscriptionRuntimePropertiesAsync(
            string topicName, string subscriptionName, CancellationToken cancellationToken = default)
        {
            Requested.Enqueue((topicName, subscriptionName));
            if (Fail)
                throw new ServiceBusException("Namespace unreachable", ServiceBusFailureReason.ServiceCommunicationProblem);

            var properties = ServiceBusModelFactory.SubscriptionRuntimeProperties(
                topicName, subscriptionName, deadLetterMessageCount: DeadLetterCount, totalMessageCount: DeadLetterCount);
            return Task.FromResult(Response.FromValue(properties, FakeResponse.Instance));
        }
    }

    private sealed record AuditCall(MessageAuditType Type, bool AccessDenied, string? EndpointId, string? Data);

    private sealed class RecordingAudit : IAuditLogService
    {
        private readonly ConcurrentQueue<AuditCall> _logged = new();

        public IReadOnlyList<AuditCall> Logged => _logged.ToList();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            _logged.Enqueue(new AuditCall(type, accessDenied, endpointId, data));
            return Task.CompletedTask;
        }

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, CancellationToken cancellationToken = default)
        {
            _logged.Enqueue(new AuditCall(type, false, endpointId, data));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeResponse : Response
    {
        public static FakeResponse Instance { get; } = new();

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream { get; set; }

        public override string ClientRequestId { get; set; } = string.Empty;

        public override void Dispose()
        {
        }

        protected override bool ContainsHeader(string name) => false;

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            yield break;
        }

        protected override bool TryGetHeader(string name, [NotNullWhen(true)] out string? value)
        {
            value = null;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, [NotNullWhen(true)] out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
