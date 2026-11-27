#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
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

    private const string EndpointId = "CrmEndpoint";
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 15, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Posted_comment_is_returned_by_the_message_audit_query()
    {
        var store = await StoreWithHistoryAsync();
        var sut = CreateSut(store, RoleAuthorizationService.OnEndpoint(EndpointId, AccessRole.Contributor), user: "alice@corp");

        var post = await sut.PostMessageAuditAsync(
            new MessageAudit { Comment = "Waiting for the ERP team to fix the customer record." },
            EventId);
        Assert.IsInstanceOfType<OkResult>(post);

        var result = await sut.GetMessageAuditsEventIdAsync(EventId);

        var audit = result.Value?.Single();
        Assert.IsNotNull(audit, "the posted audit must be returned");
        Assert.AreEqual("Waiting for the ERP team to fix the customer record.", audit.Comment);
        Assert.AreEqual("alice@corp", audit.AuditorName);
        Assert.AreEqual(MessageAuditAuditType.Comment, audit.AuditType);
        Assert.AreEqual(Now.UtcDateTime, audit.AuditTimestamp);
    }

    [TestMethod]
    public async Task Forged_auditor_timestamp_and_type_are_replaced_by_the_server()
    {
        var store = await StoreWithHistoryAsync();
        var context = ContextFor("alice@corp");
        var sut = CreateSut(store, RoleAuthorizationService.OnEndpoint(EndpointId, AccessRole.Contributor), context);

        var post = await sut.PostMessageAuditAsync(
            new MessageAudit
            {
                AuditorName = "someone-else@corp",
                AuditTimestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                AuditType = MessageAuditAuditType.GrantRole,
                Comment = "x",
            },
            EventId);
        Assert.IsInstanceOfType<OkResult>(post);

        var audit = (await sut.GetMessageAuditsEventIdAsync(EventId)).Value?.Single();
        Assert.IsNotNull(audit);
        Assert.AreEqual(MessageAuditAuditType.Comment, audit.AuditType);
        Assert.AreEqual("x", audit.Comment);
        Assert.AreEqual(AuditLogService.ResolveAuditorName(context), audit.AuditorName);
        Assert.AreNotEqual("someone-else@corp", audit.AuditorName);
        Assert.AreEqual(Now.UtcDateTime, audit.AuditTimestamp);
    }

    [TestMethod]
    public async Task Contributor_on_another_endpoint_is_forbidden_and_nothing_is_stored()
    {
        var store = await StoreWithHistoryAsync();
        var sut = CreateSut(store, RoleAuthorizationService.OnEndpoint("ErpEndpoint", AccessRole.Contributor));

        var post = await sut.PostMessageAuditAsync(new MessageAudit { Comment = "x" }, EventId);

        Assert.IsInstanceOfType<ForbidResult>(post);
        Assert.IsFalse((await store.GetMessageAudits(EventId)).Any());
    }

    [TestMethod]
    public async Task Reader_on_the_endpoint_is_forbidden_and_nothing_is_stored()
    {
        var store = await StoreWithHistoryAsync();
        var sut = CreateSut(store, RoleAuthorizationService.OnEndpoint(EndpointId, AccessRole.Reader));

        var post = await sut.PostMessageAuditAsync(new MessageAudit { Comment = "x" }, EventId);

        Assert.IsInstanceOfType<ForbidResult>(post);
        Assert.IsFalse((await store.GetMessageAudits(EventId)).Any());
    }

    [TestMethod]
    public async Task Site_wide_contributor_may_comment()
    {
        var store = await StoreWithHistoryAsync();
        var sut = CreateSut(store, RoleAuthorizationService.SiteWide(AccessRole.Contributor), user: "carol@corp");

        var post = await sut.PostMessageAuditAsync(new MessageAudit { Comment = "x" }, EventId);

        Assert.IsInstanceOfType<OkResult>(post);
        var audit = (await sut.GetMessageAuditsEventIdAsync(EventId)).Value?.Single();
        Assert.IsNotNull(audit);
        Assert.AreEqual(MessageAuditAuditType.Comment, audit.AuditType);
        Assert.AreEqual("carol@corp", audit.AuditorName);
        Assert.AreEqual(Now.UtcDateTime, audit.AuditTimestamp);
    }

    [TestMethod]
    public async Task Comment_on_an_event_without_history_is_not_found_and_nothing_is_stored()
    {
        // No endpoint can be resolved, so there is nothing to authorize against: a site-wide
        // check here would let any Contributor write rows for arbitrary event ids.
        var store = new InMemoryMessageStore();
        var sut = CreateSut(store, RoleAuthorizationService.SiteWide(AccessRole.Owner));

        var post = await sut.PostMessageAuditAsync(new MessageAudit { Comment = "x" }, EventId);

        Assert.IsInstanceOfType<NotFoundResult>(post);
        Assert.IsFalse((await store.GetMessageAudits(EventId)).Any());
    }

    [TestMethod]
    public void Event_implementation_defaults_to_the_system_clock()
    {
        // Constructing without a TimeProvider must not throw; the default is TimeProvider.System.
        var sut = CreateSut(new InMemoryMessageStore(), RoleAuthorizationService.SiteWide(AccessRole.Owner), timeProvider: null);
        Assert.IsNotNull(sut);
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
        var sut = CreateSut(store, RoleAuthorizationService.SiteWide(AccessRole.Owner));

        var result = await sut.GetMessageAuditsEventIdAsync(EventId);

        var audit = result.Value?.Single();
        Assert.IsNotNull(audit);
        Assert.AreEqual(MessageAuditAuditType.Skip, audit.AuditType);
        Assert.AreEqual("Duplicate order, already handled manually.", audit.Comment);
    }

    // The event failed on EndpointId: the request was delivered to it and it answered with an error.
    private static async Task<InMemoryMessageStore> StoreWithHistoryAsync()
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = "msg-request",
            From = "PublisherEndpoint",
            To = EndpointId,
            EndpointId = EndpointId,
            EndpointRole = EndpointRole.Subscriber,
            MessageType = NimBus.Core.Messages.MessageType.EventRequest,
            SessionId = "session-1",
            EnqueuedTimeUtc = Now.UtcDateTime.AddMinutes(-2),
        });
        await store.StoreMessage(new MessageEntity
        {
            EventId = EventId,
            MessageId = "msg-error",
            From = EndpointId,
            To = "Manager",
            EndpointId = EndpointId,
            EndpointRole = EndpointRole.Subscriber,
            MessageType = NimBus.Core.Messages.MessageType.ErrorResponse,
            SessionId = "session-1",
            EnqueuedTimeUtc = Now.UtcDateTime.AddMinutes(-1),
        });
        return store;
    }

    private static DefaultHttpContext ContextFor(string user) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "test")),
    };

    private static EventImplementation CreateSut(
        InMemoryMessageStore store,
        IEndpointAuthorizationService authorization,
        string user = "test-user") =>
        CreateSut(store, authorization, ContextFor(user));

    private static EventImplementation CreateSut(
        InMemoryMessageStore store,
        IEndpointAuthorizationService authorization,
        HttpContext context) =>
        CreateSut(store, authorization, context, new FakeTimeProvider(Now));

    private static EventImplementation CreateSut(
        InMemoryMessageStore store,
        IEndpointAuthorizationService authorization,
        TimeProvider? timeProvider) =>
        CreateSut(store, authorization, ContextFor("test-user"), timeProvider);

    private static EventImplementation CreateSut(
        InMemoryMessageStore store,
        IEndpointAuthorizationService authorization,
        HttpContext context,
        TimeProvider? timeProvider) =>
        new(
            applicationInsightsService: null!,
            new EmptyPlatform(),
            managerClient: null!,
            handoffClientFactory: null!,
            NullLogger<EventImplementation>.Instance,
            store,
            authorization,
            adminService: null!,
            serviceBusClient: null!,
            new NoOpAuditLogService(),
            handoffSettlement: null!,
            new HttpContextAccessor { HttpContext = context },
            PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance,
            timeProvider: timeProvider);

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

    /// <summary>
    /// A site-wide role plus per-endpoint grants; a site-wide role covers every endpoint. An
    /// endpoint grant comes with site-wide Reader, which reading the audits back requires and
    /// which never satisfies the Contributor check.
    /// </summary>
    private sealed class RoleAuthorizationService(AccessRole siteRole, Dictionary<string, AccessRole> endpointRoles)
        : IEndpointAuthorizationService
    {
        public static RoleAuthorizationService SiteWide(AccessRole role) => new(role, new(StringComparer.OrdinalIgnoreCase));

        public static RoleAuthorizationService OnEndpoint(string endpointId, AccessRole role) =>
            new(AccessRole.Reader, new(StringComparer.OrdinalIgnoreCase) { [endpointId] = role });

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) =>
            Task.FromResult(siteRole >= required
                || (endpointId != null && endpointRoles.TryGetValue(endpointId, out var role) && role >= required));

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = siteRole,
            IsPiiReader = true,
        });

        public string? GetCurrentUserName() => "test-user";
    }
}
