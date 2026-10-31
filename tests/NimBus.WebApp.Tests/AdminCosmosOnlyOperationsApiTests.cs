#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Tests.Mcp;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #285: Delete by To and Copy endpoint data are Cosmos-only. On another store the admin
/// service throws <see cref="NotSupportedException"/>, which the API let escape as a 500, so the
/// Operations card showed nothing. The API now answers 400 with the reason.
/// </summary>
[TestClass]
public sealed class AdminCosmosOnlyOperationsApiTests
{
    private const string Endpoint = "Crm";
    private const string CosmosOnly = "supported only when the Cosmos DB storage provider is active";

    [TestMethod]
    public async Task Delete_by_To_preview_answers_400_on_a_store_without_Cosmos()
    {
        var response = await NonCosmosController().PostAdminDeleteByToPreviewAsync(new DeleteByToRequest { ToField = Endpoint });

        StringAssert.Contains(BadRequestMessage(response.Result), CosmosOnly, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Delete_by_To_answers_400_on_a_store_without_Cosmos()
    {
        var response = await NonCosmosController().PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = Endpoint });

        StringAssert.Contains(BadRequestMessage(response.Result), CosmosOnly, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Copy_answers_400_on_a_store_without_Cosmos()
    {
        var response = await NonCosmosController().PostAdminCopyAsync(Endpoint, new CopyRequest { TargetConnectionString = "AccountEndpoint=x" });

        StringAssert.Contains(BadRequestMessage(response.Result), CosmosOnly, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task A_non_owner_still_gets_403_from_all_three()
    {
        var controller = NonCosmosController(owner: false);

        Assert.IsInstanceOfType<ForbidResult>((await controller.PostAdminDeleteByToPreviewAsync(new DeleteByToRequest { ToField = Endpoint })).Result);
        Assert.IsInstanceOfType<ForbidResult>((await controller.PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = Endpoint })).Result);
        Assert.IsInstanceOfType<ForbidResult>((await controller.PostAdminCopyAsync(Endpoint, new CopyRequest { TargetConnectionString = "x" })).Result);
    }

    [TestMethod]
    public async Task Copy_with_an_unknown_endpoint_still_gets_404()
    {
        var response = await NonCosmosController().PostAdminCopyAsync("Unknown", new CopyRequest { TargetConnectionString = "x" });

        Assert.IsInstanceOfType<NotFoundObjectResult>(response.Result);
    }

    [TestMethod]
    public async Task On_a_Cosmos_store_all_three_still_answer_200_with_their_payloads()
    {
        var fake = InterfaceFake<IAdminService>.Create()
            .On(nameof(IAdminService.DeleteMessagesByToPreviewAsync), _ => Task.FromResult(4))
            .On(nameof(IAdminService.DeleteMessagesByToAsync), _ => Task.FromResult(new BulkOperationResult { Processed = 4, Succeeded = 4 }))
            .On(nameof(IAdminService.CopyEndpointDataAsync), _ => Task.FromResult(new CopyResult { EventsCopied = 2, MessagesCopied = 3 }));
        var controller = Controller(fake.Instance);

        var preview = (OkObjectResult)(await controller.PostAdminDeleteByToPreviewAsync(new DeleteByToRequest { ToField = Endpoint })).Result!;
        var delete = (OkObjectResult)(await controller.PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = Endpoint })).Result!;
        var copy = (OkObjectResult)(await controller.PostAdminCopyAsync(Endpoint, new CopyRequest { TargetConnectionString = "x" })).Result!;

        Assert.AreEqual(4, ((CountResponse)preview.Value!).Count);
        Assert.AreEqual(4, ((BulkOperationResult)delete.Value!).Succeeded);
        Assert.AreEqual(3, ((CopyResult)copy.Value!).MessagesCopied);
    }

    [TestMethod]
    public async Task Other_failures_are_not_turned_into_400()
    {
        var fake = InterfaceFake<IAdminService>.Create()
            .On(nameof(IAdminService.DeleteMessagesByToPreviewAsync), _ => Task.FromException<int>(new InvalidOperationException("boom")))
            .On(nameof(IAdminService.DeleteMessagesByToAsync), _ => Task.FromException<BulkOperationResult>(new InvalidOperationException("boom")))
            .On(nameof(IAdminService.CopyEndpointDataAsync), _ => Task.FromException<CopyResult>(new InvalidOperationException("boom")));
        var controller = Controller(fake.Instance);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.PostAdminDeleteByToPreviewAsync(new DeleteByToRequest { ToField = Endpoint }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.PostAdminDeleteByToAsync(new DeleteByToRequest { ToField = Endpoint }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => controller.PostAdminCopyAsync(Endpoint, new CopyRequest { TargetConnectionString = "x" }));
    }

    private static string BadRequestMessage(IActionResult? result)
    {
        Assert.IsInstanceOfType<BadRequestObjectResult>(result);
        return (string)((BadRequestObjectResult)result).Value!;
    }

    private static readonly IPlatform Platform = new SingleEndpointPlatform();

    /// <summary>The real admin service on a store that can't copy across accounts, as on SQL Server.</summary>
    private static AdminImplementation NonCosmosController(bool owner = true)
    {
        var store = new InMemoryMessageStore();
        var adminService = new AdminService(
            platform: Platform,
            messageStore: store,
            capabilities: new NoCrossAccountCopy(),
            sbAdmin: null!,
            sbClient: null!,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance);
        return Controller(adminService, owner, store);
    }

    private static AdminImplementation Controller(IAdminService adminService, bool owner = true, InMemoryMessageStore? store = null)
    {
        var claims = owner ? new[] { new Claim("groups", "EIP_Management") } : Array.Empty<Claim>();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test")),
            },
        };
        var configuration = new ConfigurationBuilder().Build();
        var authorization = new EndpointAuthorizationService(
            accessor,
            Platform,
            NullLogger<EndpointAuthorizationService>.Instance,
            configuration,
            new AccessControlSnapshotProvider(store ?? new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance));

        return new AdminImplementation(
            accessor,
            adminService,
            subscriptionAdminService: null!,
            Platform,
            configuration,
            new NoAudit(),
            authorization,
            heartbeatService: null!);
    }

    private sealed class NoCrossAccountCopy : IStorageProviderCapabilities
    {
        public bool SupportsCrossAccountCopy => false;
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
