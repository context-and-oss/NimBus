using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Settings → Simulation API. Site Owner only, like every other <c>/api/admin/*</c> operation.
/// Outside an allowed environment every operation but <c>GET</c> returns 403; <c>GET</c>
/// reports <c>allowed: false</c> with the reason so the UI can explain it.
/// </summary>
public class SimulationImplementation : Api.ISimulationApiController
{
    private static readonly string[] SettingsBodyRequired = ["A settings body is required."];
    private static readonly string[] ConfigBodyRequired = ["A config body is required."];

    private readonly ISimulationService _simulation;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly IAuditLogService _auditLogService;
    private readonly HttpContext _context;

    /// <summary>Creates the controller implementation.</summary>
    public SimulationImplementation(
        IHttpContextAccessor contextAccessor,
        ISimulationService simulation,
        IEndpointAuthorizationService authorizationService,
        IAuditLogService auditLogService)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _simulation = simulation ?? throw new ArgumentNullException(nameof(simulation));
        _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
        _auditLogService = auditLogService ?? throw new ArgumentNullException(nameof(auditLogService));
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.SimulationStatus>> GetAdminSimulationAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        return new OkObjectResult(SimulationApiMapper.ToApi(await _simulation.GetStatusAsync(_context.RequestAborted)));
    }

    /// <inheritdoc />
    public Task<ActionResult<Api.SimulationStatus>> PutAdminSimulationSettingsAsync(Api.SimulationSettings body) =>
        RunAsync(
            MessageAuditType.UpdateSimulationSettings,
            JsonConvert.SerializeObject(body),
            () =>
            {
                if (body is null)
                    return Task.FromResult(SimulationCommandResult.Invalid(SettingsBodyRequired));
                return _simulation.UpdateSettingsAsync(SimulationApiMapper.FromApi(body), _context.RequestAborted);
            });

    /// <inheritdoc />
    public Task<ActionResult<Api.SimulationStatus>> PutAdminSimulationConfigAsync(Api.SimulationConfig body) =>
        RunAsync(
            MessageAuditType.UpdateSimulationConfig,
            JsonConvert.SerializeObject(body),
            () =>
            {
                if (body is null)
                    return Task.FromResult(SimulationCommandResult.Invalid(ConfigBodyRequired));
                return Task.FromResult(_simulation.UpdateConfig(SimulationApiMapper.FromApi(body)));
            });

    /// <inheritdoc />
    public Task<ActionResult<Api.SimulationStatus>> PostAdminSimulationStartAsync() =>
        RunAsync(MessageAuditType.ControlSimulation, ActionData("start"), () => _simulation.StartAsync(_context.RequestAborted));

    /// <inheritdoc />
    public Task<ActionResult<Api.SimulationStatus>> PostAdminSimulationPauseAsync() =>
        RunAsync(MessageAuditType.ControlSimulation, ActionData("pause"), () => _simulation.PauseAsync(_context.RequestAborted));

    /// <inheritdoc />
    public Task<ActionResult<Api.SimulationStatus>> PostAdminSimulationStopAsync() =>
        RunAsync(MessageAuditType.ControlSimulation, ActionData("stop"), () => _simulation.StopAsync(_context.RequestAborted));

    private async Task<ActionResult<Api.SimulationStatus>> RunAsync(
        MessageAuditType auditType,
        string data,
        Func<Task<SimulationCommandResult>> command)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(auditType, _context, accessDenied: true, data: data);
            return new ForbidResult();
        }

        var (allowed, reason) = _simulation.EvaluateEnvironment();
        if (!allowed)
        {
            await _auditLogService.LogAuditAsync(auditType, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { request = data, blockedReason = reason?.ToString() }));
            return new ObjectResult(Problem($"Simulation is not allowed in this environment ({reason}).")) { StatusCode = StatusCodes.Status403Forbidden };
        }

        var result = await command();
        await _auditLogService.LogAuditAsync(auditType, _context,
            data: JsonConvert.SerializeObject(new { request = data, outcome = result.Status.ToString(), errors = result.Errors }));

        return result.Status switch
        {
            SimulationCommandStatus.Ok => new OkObjectResult(SimulationApiMapper.ToApi(await _simulation.GetStatusAsync(_context.RequestAborted))),
            SimulationCommandStatus.Invalid => new BadRequestObjectResult(Problem(result.Errors)),
            SimulationCommandStatus.Conflict => new ConflictObjectResult(Problem(result.Errors)),
            _ => new ObjectResult(Problem(result.Errors)) { StatusCode = StatusCodes.Status500InternalServerError },
        };
    }

    private static string ActionData(string action) => JsonConvert.SerializeObject(new { action });

    private static Api.SimulationProblem Problem(string error) => Problem(new[] { error });

    private static Api.SimulationProblem Problem(IEnumerable<string> errors) => new() { Errors = errors.ToList() };

    private Task<bool> IsSiteOwnerAsync() => _authorizationService.HasRoleAsync(AccessRole.Owner);
}

/// <summary>Maps between the generated API contract and the simulation domain model.</summary>
internal static class SimulationApiMapper
{
    public static Api.SimulationStatus ToApi(SimulationStatusSnapshot status) => new()
    {
        Allowed = status.Allowed,
        BlockedReason = status.BlockedReason switch
        {
            SimulationBlockedReason.EnvironmentMissing => Api.SimulationBlockReason.EnvironmentMissing,
            SimulationBlockedReason.NotAllowed => Api.SimulationBlockReason.NotAllowed,
            SimulationBlockedReason.Production => Api.SimulationBlockReason.Production,
            _ => null,
        },
        Environment = status.Environment,
        ProductionNames = SimulationEnvironmentPolicy.ProductionNames.OrderBy(n => n, StringComparer.Ordinal).ToList(),
        AllowedEnvironments = status.AllowedEnvironments.ToList(),
        Enabled = status.Enabled,
        State = status.State switch
        {
            SimulationState.Running => Api.SimulationRunState.Running,
            SimulationState.Pausing => Api.SimulationRunState.Pausing,
            SimulationState.Paused => Api.SimulationRunState.Paused,
            SimulationState.Stopping => Api.SimulationRunState.Stopping,
            _ => Api.SimulationRunState.Stopped,
        },
        StartedAt = status.StartedAt?.UtcDateTime,
        AutoStopAt = status.AutoStopAt?.UtcDateTime,
        Capped = status.Capped,
        MaxRateCeilingPerMinute = status.MaxRateCeilingPerMinute,
        SessionPrefix = status.SessionPrefix,
        Settings = ToApi(status.Settings),
        Config = ToApi(status.Config),
        Counters = new Api.SimulationCounters
        {
            Published = status.Counters.Published,
            HandledOk = status.Counters.HandledOk,
            HandlerErrors = status.Counters.HandlerErrors,
            Poisoned = status.Counters.Poisoned,
            PublishErrors = status.Counters.PublishErrors,
            AbandonedSends = status.Counters.AbandonedSends,
            ThroughputPerMinute = status.Counters.ThroughputPerMinute,
        },
        Recent = status.Recent.Select(d => new Api.SimulationDelivery
        {
            At = d.At.UtcDateTime,
            EndpointId = d.EndpointId,
            EventTypeId = d.EventTypeId,
            SessionId = d.SessionId,
            MessageId = d.MessageId,
            Outcome = d.Outcome switch
            {
                SimulatedDeliveryOutcome.Threw => Api.SimulationDeliveryOutcome.Threw,
                SimulatedDeliveryOutcome.Poisoned => Api.SimulationDeliveryOutcome.Poisoned,
                SimulatedDeliveryOutcome.Unsupported => Api.SimulationDeliveryOutcome.Unsupported,
                _ => Api.SimulationDeliveryOutcome.Completed,
            },
            Attempt = d.Attempt,
            LatencyMs = d.LatencyMs,
            Error = d.Error,
        }).ToList(),
        Endpoints = status.Endpoints.Select(e => new Api.SimulationEndpoint
        {
            EndpointId = e.EndpointId,
            Produces = e.Produces.ToList(),
            Consumes = e.Consumes.ToList(),
            Owned = e.Owned,
            LiveInstanceWarning = e.LiveInstanceWarning,
            EffectiveMode = e.EffectiveMode is SimulatedFailureMode mode ? ToApi(mode) : null,
        }).ToList(),
    };

    public static Api.SimulationSettings ToApi(SimulationSettings settings) => new()
    {
        Enabled = settings.Enabled,
        AutoStopMinutes = settings.AutoStopMinutes,
        RateCeilingPerMinute = settings.RateCeilingPerMinute,
        OwnedEndpoints = settings.OwnedEndpoints.ToList(),
    };

    public static SimulationSettings FromApi(Api.SimulationSettings settings) => new(
        settings.Enabled,
        settings.AutoStopMinutes,
        settings.RateCeilingPerMinute,
        (settings.OwnedEndpoints ?? new List<string>()).ToList());

    public static Api.SimulationConfig ToApi(SimulationConfig config) => new()
    {
        Speed = config.Speed,
        Publishers = config.Publishers.Select(p => new Api.SimulationPublisherConfig
        {
            EndpointId = p.EndpointId,
            EventTypes = p.EventTypes.Select(e => new Api.SimulationEventTypeConfig
            {
                EventTypeId = e.EventTypeId,
                Enabled = e.Enabled,
                RatePerMinute = e.RatePerMinute,
            }).ToList(),
        }).ToList(),
        Subscribers = config.Subscribers.Select(s => new Api.SimulationSubscriberConfig
        {
            EndpointId = s.EndpointId,
            Failure = new Api.SimulationFailure
            {
                Mode = ToApi(s.Failure.Mode),
                Rate = s.Failure.Rate,
                FailAttempts = s.Failure.FailAttempts,
                LatencyMinMs = s.Failure.LatencyMinMs,
                LatencyMaxMs = s.Failure.LatencyMaxMs,
                ExceptionMessage = s.Failure.ExceptionMessage,
                EventTypeIds = s.Failure.EventTypeIds.ToList(),
                SessionPattern = s.Failure.SessionPattern,
                RevertAfterMinutes = s.Failure.RevertAfterMinutes,
            },
        }).ToList(),
    };

    public static SimulationConfig FromApi(Api.SimulationConfig config) => new(
        config.Speed,
        (config.Publishers ?? new List<Api.SimulationPublisherConfig>())
            .Select(p => new SimulationPublisherConfig(
                p?.EndpointId!,
                (p?.EventTypes ?? new List<Api.SimulationEventTypeConfig>())
                    .Select(e => new SimulationEventTypeConfig(e?.EventTypeId!, e?.Enabled ?? false, e?.RatePerMinute ?? 0))
                    .ToList()))
            .ToList(),
        (config.Subscribers ?? new List<Api.SimulationSubscriberConfig>())
            .Select(s => new SimulationSubscriberConfig(s?.EndpointId!, FromApi(s?.Failure)))
            .ToList());

    private static SimulatedFailure FromApi(Api.SimulationFailure? failure) =>
        failure is null
            ? null!
            : new SimulatedFailure
            {
                Mode = failure.Mode switch
                {
                    Api.SimulationFailureMode.Random => SimulatedFailureMode.Random,
                    Api.SimulationFailureMode.Transient => SimulatedFailureMode.Transient,
                    Api.SimulationFailureMode.Slow => SimulatedFailureMode.Slow,
                    Api.SimulationFailureMode.Poison => SimulatedFailureMode.Poison,
                    Api.SimulationFailureMode.NoHandler => SimulatedFailureMode.NoHandler,
                    _ => SimulatedFailureMode.Healthy,
                },
                Rate = failure.Rate,
                FailAttempts = failure.FailAttempts,
                LatencyMinMs = failure.LatencyMinMs,
                LatencyMaxMs = failure.LatencyMaxMs,
                ExceptionMessage = failure.ExceptionMessage,
                EventTypeIds = (failure.EventTypeIds ?? new List<string>()).ToList(),
                SessionPattern = string.IsNullOrEmpty(failure.SessionPattern) ? null : failure.SessionPattern,
                RevertAfterMinutes = failure.RevertAfterMinutes,
            };

    private static Api.SimulationFailureMode ToApi(SimulatedFailureMode mode) => mode switch
    {
        SimulatedFailureMode.Random => Api.SimulationFailureMode.Random,
        SimulatedFailureMode.Transient => Api.SimulationFailureMode.Transient,
        SimulatedFailureMode.Slow => Api.SimulationFailureMode.Slow,
        SimulatedFailureMode.Poison => Api.SimulationFailureMode.Poison,
        SimulatedFailureMode.NoHandler => Api.SimulationFailureMode.NoHandler,
        _ => Api.SimulationFailureMode.Healthy,
    };
}
