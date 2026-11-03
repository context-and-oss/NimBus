#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.MessageStore;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Compose answers with the identity of what it published, so the WebApp can tell the
/// operator where to follow it: the message id every subscriber's Event Request carries,
/// and the session the event was published on.
/// </summary>
[TestClass]
public sealed class ComposeNewEventTests
{
    private const string Publisher = "ComposePublisherEp";

    [SessionKey(nameof(OrderId))]
    public sealed class ComposedOrder : NimBus.Core.Events.Event
    {
        public string OrderId { get; set; } = string.Empty;

        public decimal Total { get; set; }
    }

    // Endpoint ids are the class name.
    private sealed class ComposePublisherEp : NimBus.Core.Endpoints.Endpoint
    {
        public ComposePublisherEp() => Produces<ComposedOrder>();
    }

    private sealed class ComposePlatform : NimBus.Core.Platform
    {
        public ComposePlatform() => AddEndpoint(new ComposePublisherEp());
    }

    [TestMethod]
    public async Task Compose_returns_the_published_message_id_and_session()
    {
        var client = new RecordingServiceBusClient();
        var sut = CreateSut(client);

        var result = await sut.PostComposeNewEventAsync(new ResubmitWithChanges
        {
            EventTypeId = nameof(ComposedOrder),
            EventContent = "{\"OrderId\":\"order-42\",\"Total\":10.5}",
        });

        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200 with a body, got {result.Result?.GetType().Name}.");
        var body = ok.Value as ComposeNewEventResult;
        Assert.IsNotNull(body, "The 200 body must be a ComposeNewEventResult.");

        var sent = client.Sender.SentMessages;
        Assert.AreEqual(1, sent.Count);
        Assert.AreEqual(sent[0].MessageId, body.MessageId, "The returned id must be the one that was published.");
        Assert.AreEqual("order-42", body.SessionId);
        Assert.AreEqual(sent[0].SessionId, body.SessionId);
        Assert.AreEqual(nameof(ComposedOrder), body.EventTypeId);
        Assert.AreEqual(Publisher, body.PublishedBy);
        Assert.AreEqual(Publisher, client.LastSenderEntityPath);
    }

    [TestMethod]
    public async Task Compose_keeps_the_content_derived_message_id()
    {
        // Compose must publish the same id the SDK derives for the event, so duplicate
        // detection treats an identical re-compose exactly as before.
        var client = new RecordingServiceBusClient();
        var sut = CreateSut(client);
        var content = "{\"OrderId\":\"order-7\",\"Total\":1}";

        var result = await sut.PostComposeNewEventAsync(new ResubmitWithChanges
        {
            EventTypeId = nameof(ComposedOrder),
            EventContent = content,
        });

        var expected = PublisherClient.GetMessageId(
            Newtonsoft.Json.JsonConvert.DeserializeObject<ComposedOrder>(content)!);
        var ok = result.Result as OkObjectResult;
        Assert.IsNotNull(ok, $"Expected 200 with a body, got {result.Result?.GetType().Name}.");
        Assert.AreEqual(expected, ((ComposeNewEventResult)ok.Value!).MessageId);
        Assert.AreEqual(expected, client.Sender.SentMessages[0].MessageId);
    }

    private static EventImplementation CreateSut(ServiceBusClient client) =>
        new(
            applicationInsightsService: null!,
            new ComposePlatform(),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            new InMemoryMessageStore(),
            new AllowAllAuthorizationService(),
            adminService: null!,
            client,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            PayloadRedactionTests.NewRedaction(),
            NullEventJsonMasker.Instance);

    private sealed class RecordingServiceBusClient : ServiceBusClient
    {
        public RecordingServiceBusSender Sender { get; } = new();

        public string? LastSenderEntityPath { get; private set; }

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            LastSenderEntityPath = queueOrTopicName;
            return Sender;
        }

        public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options)
        {
            LastSenderEntityPath = queueOrTopicName;
            return Sender;
        }
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class RecordingServiceBusSender : ServiceBusSender
    {
        public List<ServiceBusMessage> SentMessages { get; } = new();

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.CompletedTask;
        }

        public override Task SendMessagesAsync(IEnumerable<ServiceBusMessage> messages, CancellationToken cancellationToken = default)
        {
            SentMessages.AddRange(messages);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess { SiteRole = AccessRole.Owner, IsPiiReader = true });

        public string? GetCurrentUserName() => "test-user";
    }
}
