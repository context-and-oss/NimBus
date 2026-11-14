#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests.Simulation;

/// <summary>
/// The Settings → Simulation API (plan Task 7): site Owner only, 403 outside an allowed environment
/// (with <c>GET</c> still reporting why), audited writes, 400s that list every violation, and
/// 409 for transitions refused in the current state.
/// </summary>
[TestClass]
public sealed class SimulationApiTests
{
    [TestMethod]
    public async Task Non_owner_is_forbidden_and_the_denial_is_audited()
    {
        var (controller, audit, _) = Create(owner: false);

        Assert.IsInstanceOfType<ForbidResult>((await controller.GetAdminSimulationAsync()).Result);
        Assert.IsInstanceOfType<ForbidResult>((await controller.PostAdminSimulationStartAsync()).Result);
        Assert.IsInstanceOfType<ForbidResult>((await controller.PutAdminSimulationSettingsAsync(new Api.SimulationSettings())).Result);
        Assert.IsInstanceOfType<ForbidResult>((await controller.PutAdminSimulationConfigAsync(new Api.SimulationConfig())).Result);

        Assert.AreEqual(3, audit.Entries.Count, "Each denied write is audited; the read is not.");
        Assert.IsTrue(audit.Entries.All(e => e.AccessDenied));
        CollectionAssert.AreEquivalent(
            new[] { MessageAuditType.ControlSimulation, MessageAuditType.UpdateSimulationSettings, MessageAuditType.UpdateSimulationConfig },
            audit.Entries.Select(e => e.Type).ToArray());
    }

    [TestMethod]
    [DataRow("", Api.SimulationBlockReason.EnvironmentMissing)]
    [DataRow("test", Api.SimulationBlockReason.NotAllowed)]
    [DataRow("Production", Api.SimulationBlockReason.Production)]
    public async Task Each_blocked_reason_forbids_writes_and_is_reported_by_get(string environment, Api.SimulationBlockReason expected)
    {
        var (controller, audit, _) = Create(environment: environment);

        var status = (Api.SimulationStatus)((OkObjectResult)(await controller.GetAdminSimulationAsync()).Result!).Value!;
        Assert.IsFalse(status.Allowed);
        Assert.AreEqual(expected, status.BlockedReason);

        foreach (var write in new Func<Task<ActionResult<Api.SimulationStatus>>>[]
                 {
                     () => controller.PostAdminSimulationStartAsync(),
                     () => controller.PostAdminSimulationPauseAsync(),
                     () => controller.PostAdminSimulationStopAsync(),
                     () => controller.PutAdminSimulationSettingsAsync(new Api.SimulationSettings { Enabled = true, AutoStopMinutes = 60, RateCeilingPerMinute = 100 }),
                     () => controller.PutAdminSimulationConfigAsync(new Api.SimulationConfig { Speed = 1 }),
                 })
        {
            var result = (ObjectResult)(await write()).Result!;
            Assert.AreEqual(StatusCodes.Status403Forbidden, result.StatusCode);
        }

        Assert.AreEqual(5, audit.Entries.Count(e => e.AccessDenied));
    }

    [TestMethod]
    public async Task A_valid_settings_update_is_applied_and_audited_with_its_data()
    {
        var (controller, audit, sut) = Create();

        var response = await controller.PutAdminSimulationSettingsAsync(new Api.SimulationSettings
        {
            Enabled = true,
            AutoStopMinutes = 15,
            RateCeilingPerMinute = 50,
            OwnedEndpoints = new List<string> { SimulationTestPlatform.Warehouse },
        });

        var status = (Api.SimulationStatus)((OkObjectResult)response.Result!).Value!;
        Assert.AreEqual(15, status.Settings.AutoStopMinutes);
        CollectionAssert.AreEqual(new[] { SimulationTestPlatform.Warehouse }, sut.Service.Settings.OwnedEndpoints.ToArray());
        var entry = audit.Entries.Single();
        Assert.AreEqual(MessageAuditType.UpdateSimulationSettings, entry.Type);
        Assert.IsFalse(entry.AccessDenied);
        StringAssert.Contains(entry.Data, SimulationTestPlatform.Warehouse, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Start_is_audited_with_its_action()
    {
        var (controller, audit, sut) = Create();

        var response = await controller.PostAdminSimulationStartAsync();

        var status = (Api.SimulationStatus)((OkObjectResult)response.Result!).Value!;
        Assert.AreEqual(Api.SimulationRunState.Running, status.State);
        StringAssert.Contains(audit.Entries.Single().Data, "start", StringComparison.Ordinal);
        await sut.Service.StopAsync();
    }

    [TestMethod]
    public async Task An_out_of_range_config_is_rejected_listing_every_violation()
    {
        var (controller, _, _) = Create();

        var response = await controller.PutAdminSimulationConfigAsync(new Api.SimulationConfig
        {
            Speed = 7,
            Publishers = new List<Api.SimulationPublisherConfig>
            {
                new()
                {
                    EndpointId = SimulationTestPlatform.Storefront,
                    EventTypes = new List<Api.SimulationEventTypeConfig> { new() { EventTypeId = "OrderPlaced", Enabled = true, RatePerMinute = 100_000 } },
                },
            },
            Subscribers = new List<Api.SimulationSubscriberConfig>
            {
                new()
                {
                    EndpointId = SimulationTestPlatform.Billing,
                    Failure = new Api.SimulationFailure { Mode = Api.SimulationFailureMode.Random, Rate = 0, FailAttempts = 1, LatencyMinMs = 0, LatencyMaxMs = 20_000 },
                },
            },
        });

        var problem = (Api.ProblemDetails)((BadRequestObjectResult)response.Result!).Value!;
        Assert.AreEqual(4, problem.Errors.Count, string.Join("\n", problem.Errors));
    }

    [TestMethod]
    public async Task Start_while_stopping_is_a_conflict()
    {
        var (controller, _, sut) = Create();
        await sut.Service.StartAsync();
        sut.Hosts.BlockStops();
        var stop = sut.Service.StopAsync();
        while (sut.Service.State != SimulationState.Stopping)
            await Task.Delay(5);

        var response = await controller.PostAdminSimulationStartAsync();

        var conflict = (ConflictObjectResult)response.Result!;
        Assert.IsTrue(((Api.ProblemDetails)conflict.Value!).Errors.Single().Contains("Stopping", StringComparison.Ordinal));
        sut.Hosts.ReleaseStops();
        await stop;
    }

    private static (SimulationImplementation Controller, RecordingAuditLogService Audit, SimulationServiceTests.Sut Sut) Create(
        bool owner = true,
        string environment = "dev")
    {
        var sut = new SimulationServiceTests.Sut(environment: environment);
        var audit = new RecordingAuditLogService();
        var controller = new SimulationImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            sut.Service,
            new FixedAuthorizationService(owner),
            audit);
        return (controller, audit, sut);
    }

    private sealed class FixedAuthorizationService(bool authorized) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(authorized && required == AccessRole.Owner);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "test-owner";
    }

    private sealed class RecordingAuditLogService : IAuditLogService
    {
        public List<(MessageAuditType Type, bool AccessDenied, string Data)> Entries { get; } = new();

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false,
            string? data = null, string? eventId = null, string? endpointId = null,
            string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((type, accessDenied, data ?? string.Empty));
            return Task.CompletedTask;
        }
    }
}
