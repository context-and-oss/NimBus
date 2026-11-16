#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.PII;
using NimBus.MessageStore;
using NimBus.SDK;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Mcp.Operations;
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

    [TestMethod]
    public async Task Compose_denied_returns_Forbid_writes_accessDenied_audit_and_publishes_nothing()
    {
        // GH#402: a caller who is not Contributor on the producing endpoint gets a 403, not
        // an exception the problem-details middleware turns into a 500.
        var client = new RecordingServiceBusClient();
        var store = new InMemoryMessageStore();
        var sut = CreateSut(client, new DenyAllAuthorizationService(), store,
            new AuditLogService(NullLogger<AuditLogService>.Instance, store));

        var result = await sut.PostComposeNewEventAsync(new ResubmitWithChanges
        {
            EventTypeId = nameof(ComposedOrder),
            EventContent = "{\"OrderId\":\"order-9\",\"Total\":1}",
        });

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        Assert.AreEqual(0, client.Sender.SentMessages.Count, "A denied compose must publish nothing.");
        var audits = (await store.SearchAudits(new AuditFilter(), continuationToken: null, maxItemCount: 50)).Audits.ToList();
        Assert.AreEqual(1, audits.Count);
        Assert.AreEqual(MessageAuditType.Compose, audits[0].Audit.AuditType);
        Assert.IsTrue(audits[0].Audit.AccessDenied);
        Assert.AreEqual(Publisher, audits[0].Audit.EndpointId);
    }

    [TestMethod]
    public async Task Compose_row_holds_the_hash_and_property_names_and_no_payload_values()
    {
        var audit = new ResubmitWithChangesAuditTests.RecordingAudit();
        var sut = CreateSut(new RecordingServiceBusClient(), auditLog: audit);

        var result = await sut.PostComposeNewEventAsync(new ResubmitWithChanges
        {
            EventTypeId = nameof(ComposedOrder),
            EventContent = ComposedJson,
        });

        Assert.IsInstanceOfType<OkObjectResult>(result.Result);
        var row = audit.Rows.Single(r => r.Type == MessageAuditType.Compose);
        Assert.IsFalse(row.AccessDenied);
        AssertDescribesComposedEvent(row.Data);
    }

    [TestMethod]
    public async Task Compose_row_refused_for_a_missing_role_has_the_same_shape()
    {
        var audit = new ResubmitWithChangesAuditTests.RecordingAudit();
        var sut = CreateSut(new RecordingServiceBusClient(), new DenyAllAuthorizationService(), auditLog: audit);

        var result = await sut.PostComposeNewEventAsync(new ResubmitWithChanges
        {
            EventTypeId = nameof(ComposedOrder),
            EventContent = ComposedJson,
        });

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
        var row = audit.Rows.Single(r => r.Type == MessageAuditType.Compose);
        Assert.IsTrue(row.AccessDenied);
        AssertDescribesComposedEvent(row.Data);
    }

    private const string ComposedJson = "{\"OrderId\":\"order-composed-99\",\"Total\":12345.67}";

    private static void AssertDescribesComposedEvent(string? data)
    {
        Assert.IsNotNull(data, "The row must describe the composed event.");
        foreach (var value in new[] { "order-composed-99", "12345.67" })
            Assert.IsFalse(data.Contains(value, StringComparison.Ordinal), $"The row must not hold the payload value '{value}': {data}");

        var json = JObject.Parse(data);
        Assert.IsNull(json.Property("EventContent", StringComparison.OrdinalIgnoreCase), "The row must not hold the payload.");
        Assert.AreEqual("WebApp", json.Value<string>("channel"));
        Assert.AreEqual(nameof(ComposedOrder), json.Value<string>("eventTypeId"));
        Assert.AreEqual(OperatorPayloadEdit.Hash(ComposedJson), json.Value<string>("contentHash"));
        CollectionAssert.AreEqual(new[] { "OrderId", "Total" }, json["properties"]!.Values<string>().ToArray());
    }

    private static EventImplementation CreateSut(
        ServiceBusClient client,
        IEndpointAuthorizationService? authorization = null,
        InMemoryMessageStore? store = null,
        IAuditLogService? auditLog = null) =>
        new(
            applicationInsightsService: null!,
            new ComposePlatform(),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store ?? new InMemoryMessageStore(),
            authorization ?? new AllowAllAuthorizationService(),
            adminService: null!,
            client,
            auditLog ?? new NoOpAuditLogService(),
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

    private sealed class DenyAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(false);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() =>
            Task.FromResult(new CurrentUserAccess());

        public string? GetCurrentUserName() => "operator-mallory";
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
