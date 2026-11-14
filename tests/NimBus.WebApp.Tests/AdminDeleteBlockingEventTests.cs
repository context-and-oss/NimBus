#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
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
using AdminDeleteStatus = NimBus.WebApp.ManagementApi.AdminDeleteStatus;
using CountResponse = NimBus.WebApp.ManagementApi.CountResponse;
using DeleteByStatusRequest = NimBus.WebApp.ManagementApi.DeleteByStatusRequest;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #288: deleting the stored event that blocks its session left the session blocked by an
/// event the UI no longer shows, and Skip on the message could no longer release it. The deletes
/// now keep such an event (a Failed row, or Pending with a hand-off) and point to Skip.
/// </summary>
[TestClass]
public sealed class AdminDeleteBlockingEventTests
{
    private const string Endpoint = "Crm";
    private static readonly List<string> FailedPendingUnsupported = new() { "Failed", "Pending", "Unsupported" };

    [TestMethod]
    public async Task Delete_by_status_keeps_events_that_block_their_session_and_deletes_the_rest()
    {
        var harness = await Harness.SeededAsync();

        var result = await harness.Sut.DeleteByStatusAsync(Endpoint, FailedPendingUnsupported);

        Assert.AreEqual(2, result.Succeeded);
        Assert.AreEqual(2, result.Failed);
        StringAssert.Contains(result.Errors!.Single(), "block", StringComparison.Ordinal);
        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, "failed-1"), "A Failed event blocks its session.");
        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, "handoff-1"), "A pending hand-off blocks its session.");
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "pending-1"));
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "unsupported-1"));
    }

    [TestMethod]
    public async Task Delete_by_status_preview_counts_what_it_deletes_and_what_it_keeps()
    {
        var harness = await Harness.SeededAsync();

        Assert.AreEqual(2, await harness.Sut.DeleteByStatusPreviewAsync(Endpoint, FailedPendingUnsupported));
        Assert.AreEqual(2, await harness.Sut.CountSessionBlockingEventsAsync(Endpoint, FailedPendingUnsupported));
    }

    [TestMethod]
    public async Task Delete_single_event_refuses_an_event_that_blocks_its_session()
    {
        var harness = await Harness.SeededAsync();

        await Assert.ThrowsExactlyAsync<EventBlocksSessionException>(() => harness.Sut.DeleteEventAsync(Endpoint, "failed-1"));

        Assert.IsNotNull(await harness.Store.GetEvent(Endpoint, "failed-1"));
    }

    [TestMethod]
    public async Task Delete_single_event_still_deletes_an_event_that_does_not()
    {
        var harness = await Harness.SeededAsync();

        Assert.IsTrue(await harness.Sut.DeleteEventAsync(Endpoint, "unsupported-1"));
        Assert.IsNull(await harness.Store.GetEvent(Endpoint, "unsupported-1"));
    }

    [TestMethod]
    public async Task The_api_refuses_to_delete_an_event_that_blocks_its_session_with_409()
    {
        var harness = await Harness.SeededAsync();

        var response = await harness.CreateController().DeleteAdminEventAsync(Endpoint, "failed-1");

        var conflict = (ConflictObjectResult)response;
        StringAssert.Contains((string)conflict.Value!, "Skip", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task The_delete_by_status_preview_reports_the_events_it_keeps()
    {
        var harness = await Harness.SeededAsync();

        var response = await harness.CreateController().PostAdminDeleteByStatusPreviewAsync(
            Endpoint,
            new DeleteByStatusRequest
            {
                Statuses = new List<AdminDeleteStatus> { AdminDeleteStatus.Failed, AdminDeleteStatus.Pending, AdminDeleteStatus.Unsupported },
            });

        var preview = (CountResponse)((OkObjectResult)response.Result!).Value!;
        Assert.AreEqual(2, preview.Count);
        Assert.AreEqual(2, preview.BlockingCount);
    }

    private sealed class Harness
    {
        private readonly IPlatform _platform = new SingleEndpointPlatform();

        private Harness()
        {
            Sut = new AdminService(
                platform: _platform,
                messageStore: Store,
                capabilities: null!,
                sbAdmin: null!,
                sbClient: null!,
                managerClient: null!,
                subscriptionAdmin: null!,
                logger: NullLogger<AdminService>.Instance);
        }

        public InMemoryMessageStore Store { get; } = new();

        public AdminService Sut { get; }

        /// <summary>
        /// One event per case, each in a session of its own: a failure and a pending hand-off,
        /// which block their sessions, and a plain Pending and an Unsupported event, which don't.
        /// </summary>
        public static async Task<Harness> SeededAsync()
        {
            var harness = new Harness();
            await OperatorCommandTestRows.SeedFailedRowAsync(harness.Store, Endpoint, "failed-1", "sess-1", "msg-1");
            await OperatorCommandTestRows.SeedFailedRowAsync(harness.Store, Endpoint, "pending-1", "sess-2", "msg-2", ResolutionStatus.Pending);
            await OperatorCommandTestRows.SeedFailedRowAsync(harness.Store, Endpoint, "unsupported-1", "sess-3", "msg-3", ResolutionStatus.Unsupported);
            await harness.Store.UploadPendingMessage("handoff-1", "sess-4", Endpoint, new UnresolvedEvent
            {
                EventId = "handoff-1",
                SessionId = "sess-4",
                EndpointId = Endpoint,
                LastMessageId = "msg-4",
                MessageType = MessageType.PendingHandoffResponse,
                ResolutionStatus = ResolutionStatus.Pending,
                PendingSubStatus = "Handoff",
                UpdatedAt = DateTime.UtcNow,
                EnqueuedTimeUtc = DateTime.UtcNow,
                MessageContent = new MessageContent(),
            });
            return harness;
        }

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
                subscriptionAdminService: null!,
                _platform,
                configuration,
                new NoAudit(),
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

    private sealed class NoAudit : IAuditLogService
    {
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task LogRequiredAuditAsync(MessageAuditType type, HttpContext context, string? data = null, string? eventId = null,
            string? endpointId = null, string? eventTypeId = null, System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
