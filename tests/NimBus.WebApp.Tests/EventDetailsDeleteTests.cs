#pragma warning disable CA1707, CA2007
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using ResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Event Details → Delete (<c>DELETE /api/event/invalid/{endpointId}/{eventId}/{sessionId}</c>) must
/// not remove the event its session is blocked on (#288), like Operations → Delete single event.
/// </summary>
[TestClass]
public sealed class EventDetailsDeleteTests
{
    [TestMethod]
    public async Task A_failed_event_is_kept_and_the_delete_refused()
    {
        var (sut, store) = Create();
        await store.UploadFailedMessage("event", "session", "Crm", Row());

        var result = await sut.DeleteEventInvalidIdAsync("Crm", "event", "session");

        var conflict = result as ConflictObjectResult;
        Assert.IsNotNull(conflict, $"Expected 409, got {result.GetType().Name}.");
        StringAssert.Contains((string)conflict.Value!, "blocks its session");
        Assert.IsNotNull(await store.GetEvent("Crm", "event"));
    }

    [TestMethod]
    public async Task A_pending_hand_off_is_kept_and_the_delete_refused()
    {
        var (sut, store) = Create();
        var row = Row();
        row.PendingSubStatus = "Handoff";
        await store.UploadPendingMessage("event", "session", "Crm", row);

        var result = await sut.DeleteEventInvalidIdAsync("Crm", "event", "session");

        Assert.IsInstanceOfType<ConflictObjectResult>(result);
        Assert.IsNotNull(await store.GetEvent("Crm", "event"));
    }

    [TestMethod]
    public async Task A_dead_lettered_event_is_deleted()
    {
        var (sut, store) = Create();
        await store.UploadDeadletteredMessage("event", "session", "Crm", Row());

        var result = await sut.DeleteEventInvalidIdAsync("Crm", "event", "session");

        Assert.IsInstanceOfType<OkResult>(result);
        Assert.IsNull(await store.GetEvent("Crm", "event"));
    }

    private static UnresolvedEvent Row() => new()
    {
        EndpointId = "Crm", EventId = "event", SessionId = "session", LastMessageId = "message",
        UpdatedAt = DateTime.UtcNow, EnqueuedTimeUtc = DateTime.UtcNow.AddMinutes(-1),
    };

    private static (EventImplementation Sut, InMemoryMessageStore Store) Create()
    {
        var store = new InMemoryMessageStore();
        var sut = new EventImplementation(null!, new Catalog(), null!, null!, NullLogger<EventImplementation>.Instance,
            store, new Contributor(), null!, new DeferredMessageInspectorTests.FakeClient(), new Audit(), null!,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, PayloadRedactionTests.NewRedaction(),
            NimBus.Core.Messages.PII.NullEventJsonMasker.Instance);
        return (sut, store);
    }

    private sealed class Catalog : Platform { public Catalog() { AddEndpoint(new Crm()); } }
    private sealed class Crm : NimBus.Core.Endpoints.Endpoint { }
    private sealed class Contributor : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string GetCurrentUserName() => "operator";
    }
    private sealed class Audit : IAuditLogService
    {
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
