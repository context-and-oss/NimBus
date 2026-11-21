#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The event's audit trail as the WebApp returns it from
/// <see cref="EventImplementation.GetMessageAuditsEventIdAsync"/>. Tests that assert only the
/// stored <see cref="MessageAuditEntity"/> cannot catch the read projection dropping a field,
/// so these read the audits back through the API.
/// </summary>
[TestClass]
public sealed class EventImplementationMessageAuditTests
{
    private const string EventId = "evt-1";

    [TestMethod]
    public async Task Posted_comment_is_returned_by_the_message_audit_query()
    {
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store);
        var timestamp = new DateTime(2026, 11, 21, 10, 30, 0, DateTimeKind.Utc);

        var post = await sut.PostMessageAuditAsync(
            new MessageAudit
            {
                AuditorName = "alice",
                AuditTimestamp = timestamp,
                AuditType = MessageAuditAuditType.Comment,
                Comment = "Waiting for the ERP team to fix the customer record.",
            },
            EventId);
        Assert.IsInstanceOfType<OkResult>(post);

        var result = await sut.GetMessageAuditsEventIdAsync(EventId);

        var audit = result.Value?.Single();
        Assert.IsNotNull(audit, "the posted audit must be returned");
        Assert.AreEqual("Waiting for the ERP team to fix the customer record.", audit.Comment);
        Assert.AreEqual("alice", audit.AuditorName);
        Assert.AreEqual(MessageAuditAuditType.Comment, audit.AuditType);
        Assert.AreEqual(timestamp, audit.AuditTimestamp);
    }

    [TestMethod]
    public async Task Stored_comment_on_a_non_comment_audit_is_returned_by_the_message_audit_query()
    {
        // Resubmit and skip rows carry the operator's reason in Comment too.
        var store = new InMemoryMessageStore();
        await store.StoreMessageAudit(EventId, new MessageAuditEntity
        {
            AuditorName = "bob",
            AuditTimestamp = DateTime.UtcNow,
            AuditType = MessageAuditType.Skip,
            Comment = "Duplicate order, already handled manually.",
        });
        var sut = CreateSut(store);

        var result = await sut.GetMessageAuditsEventIdAsync(EventId);

        var audit = result.Value?.Single();
        Assert.IsNotNull(audit);
        Assert.AreEqual(MessageAuditAuditType.Skip, audit.AuditType);
        Assert.AreEqual("Duplicate order, already handled manually.", audit.Comment);
    }

    private static EventImplementation CreateSut(InMemoryMessageStore store) =>
        new(
            applicationInsightsService: null!,
            new EmptyPlatform(),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            new AllowAllAuthorizationService(),
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);

    private sealed class EmptyPlatform : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
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

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
