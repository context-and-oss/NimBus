using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.Alerts;
using NimBus.Core;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AlertRules;
using Api = NimBus.WebApp.ManagementApi;
using AccessRole = NimBus.WebApp.Services.AccessRole;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// The alert rules API (Spec 053). Anyone who can read an endpoint sees its rules; only the
/// endpoint's Owners (and Site Owners) see where notices go, and only they create, change and
/// delete rules. Every write is audited, never with a secret. A rule is always loaded by the
/// endpoint in the URL and its id together, so a rule id from another endpoint is not found.
/// </summary>
public class AlertRulesImplementation : Api.IAlertRulesApiController
{
    /// <summary>The most rules one endpoint can have.</summary>
    public const int MaxRulesPerEndpoint = 20;

    /// <summary>The most targets one rule can have.</summary>
    public const int MaxTargetsPerRule = 10;

    /// <summary>The highest threshold (provisional, from the Phase 1 Cosmos DB measurement).</summary>
    public const int MaxThreshold = 100;

    /// <summary>The shortest window a failure-occurrences rule can count in.</summary>
    public const int MinWindowMinutes = 5;

    /// <summary>The longest window a failure-occurrences rule can count in (a day).</summary>
    public const int MaxWindowMinutes = 1440;

    /// <summary>The longest blocked or silent period a rule can wait for (a day).</summary>
    public const int MaxMinutes = 1440;

    /// <summary>The shortest silence a rule can watch for.</summary>
    public const int MinSilenceMinutes = 5;

    /// <summary>The highest deferred-event threshold of a blocked-session rule.</summary>
    public const int MaxDeferredThreshold = 1000;

    /// <summary>The most classification categories one rule can filter by.</summary>
    public const int MaxCategories = 8;

    private static readonly string[] KnownStatuses = ["Failed", "DeadLettered", "Unsupported"];

    /// <summary>The categories a rule can filter by: every classification category but unknown, in a fixed order.</summary>
    private static readonly string[] SelectableCategories = FailureClassificationQuestionSet.Categories
        .Where(c => c != ClassificationMatch.UnknownCategory)
        .Order(StringComparer.Ordinal)
        .ToArray();

    private readonly HttpContext _context;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IPlatform _platform;
    private readonly AlertRuleDelivery _delivery;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _time;
    private readonly IAlertRuleStore? _store;
    private readonly IAlertSignalStore? _signals;
    private readonly IMessageTrackingStore? _events;
    private readonly IAlertRuleClassifications? _classifications;

    /// <summary>Creates the controller implementation.</summary>
    public AlertRulesImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IPlatform platform,
        AlertRuleDelivery delivery,
        IConfiguration configuration,
        TimeProvider time,
        IAlertRuleStore? store = null,
        IAlertSignalStore? signals = null,
        IMessageTrackingStore? events = null,
        FailureClassificationReader? classificationReader = null)
        : this(contextAccessor, authorization, audit, platform, delivery, configuration, time, store, signals, events,
            classificationReader is null ? null : new ReaderAlertRuleClassifications(classificationReader))
    {
    }

    /// <summary>Creates the implementation over any classification source (tests).</summary>
    internal AlertRulesImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IPlatform platform,
        AlertRuleDelivery delivery,
        IConfiguration configuration,
        TimeProvider time,
        IAlertRuleStore? store,
        IAlertSignalStore? signals,
        IMessageTrackingStore? events,
        IAlertRuleClassifications? classifications)
    {
        _signals = signals;
        _events = events;
        _classifications = classifications;
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _authorization = authorization;
        _audit = audit;
        _platform = platform;
        _delivery = delivery;
        _configuration = configuration;
        _time = time;
        _store = store;
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AlertRuleList>> GetEndpointAlertRulesAsync(string endpointId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (!await _authorization.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();
        if (_store is null)
            return NotImplemented();

        var canManage = await _authorization.HasRoleAsync(AccessRole.Owner, endpointId);
        var rules = await _store.ListAlertRulesByEndpoint(endpointId);
        var evaluator = await _store.GetAlertRuleEvaluatorStatus();
        return new OkObjectResult(new Api.AlertRuleList
        {
            Rules = rules.Select(rule => ToModel(rule, canManage)).ToList(),
            EvaluatorLastCompletedAtUtc = evaluator.LastCompletedAtUtc,
            MailEnabled = _delivery.MailEnabled,
            WebhookEnabled = _delivery.WebhookEnabled,
            CanManage = canManage,
            AvailableKinds = AvailableKinds().ToList(),
            ClassificationAvailable = _classifications is not null,
            Categories = _classifications is null ? [] : SelectableCategories.ToList(),
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AlertRulePreview>> PostEndpointAlertRulePreviewAsync(Api.AlertRulePreviewRequest body, string endpointId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (!await _authorization.HasRoleAsync(AccessRole.Reader, endpointId))
            return new ForbidResult();
        if (_store is null || _events is null)
            return NotImplemented();

        var errors = new List<string>();
        if (body?.Condition is null)
            errors.Add("condition is required.");
        else
            ValidateCondition(body.Condition, errors);
        if (body?.ResolveAfterMinutes is < 1 or > 60)
            errors.Add("resolveAfterMinutes must be 1 to 60.");
        if (errors.Count > 0)
            return BadRequest(errors.Distinct());

        var previewer = new AlertRulePreviewer(new AlertRuleConditionReader(_events, _signals, _classifications));
        var result = await previewer.PreviewAsync(
            endpointId, ToCondition(body!.Condition!), body.ResolveAfterMinutes, _time.GetUtcNow().UtcDateTime, _context.RequestAborted);
        return new OkObjectResult(new Api.AlertRulePreview
        {
            ValueNow = result.ValueNow,
            HoldsNow = result.HoldsNow,
            NowUnavailableReason = result.NowUnavailableReason,
            ReplayUnsupportedReason = result.ReplayUnsupportedReason,
            Replay = result.Replay is not { } replay ? null : new Api.AlertRuleReplay
            {
                FromUtc = replay.FromUtc,
                ToUtc = replay.ToUtc,
                Series = replay.Series.Select(p => new Api.AlertRulePreviewPoint { AtUtc = p.AtUtc, Value = p.Value }).ToList(),
                Periods = replay.Periods.Select(p => new Api.AlertRulePreviewPeriod { FromUtc = p.FromUtc, ToUtc = p.ToUtc }).ToList(),
            },
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AlertRuleSaved>> PostEndpointAlertRuleAsync(Api.AlertRuleRequest body, string endpointId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (!await _authorization.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.AlertRuleCreated, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { name = body?.Name }), endpointId: endpointId);
            return new ForbidResult();
        }

        if (_store is null)
            return NotImplemented();

        var errors = Validate(body);
        if (errors.Count > 0)
            return BadRequest(errors);

        var existing = await _store.ListAlertRulesByEndpoint(endpointId);
        if (existing.Count >= MaxRulesPerEndpoint)
            return BadRequest([$"An endpoint can have at most {MaxRulesPerEndpoint} alert rules."]);

        var now = _time.GetUtcNow().UtcDateTime;
        var author = CurrentUser();
        var rule = new AlertRule
        {
            RuleId = Guid.NewGuid().ToString("N"),
            EndpointId = endpointId,
            Name = body.Name!.Trim(),
            Enabled = body.Enabled,
            Condition = ToCondition(body.Condition!),
            ResolveAfterMinutes = body.ResolveAfterMinutes,
            Targets = AssignTargets(body.Targets!, []),
            CreatedBy = author,
            CreatedAtUtc = now,
            UpdatedBy = author,
            UpdatedAtUtc = now,
            State = new AlertRuleState(),
        };

        var saved = await _store.TrySaveAlertRuleDefinition(rule, expectedVersion: null);
        if (saved is null)
            return Problem(StatusCodes.Status409Conflict, "Conflict", ["A rule with this id already exists. Try again."]);

        await _audit.LogAuditAsync(MessageAuditType.AlertRuleCreated, _context,
            data: JsonConvert.SerializeObject(AuditView(saved)), endpointId: endpointId);
        return new OkObjectResult(new Api.AlertRuleSaved
        {
            Rule = ToModel(saved, canManage: true),
            Secrets = Secrets(saved, saved.Targets),
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AlertRuleSaved>> PutEndpointAlertRuleAsync(Api.AlertRuleRequest body, string endpointId, string ruleId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (!await _authorization.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.AlertRuleUpdated, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { ruleId }), endpointId: endpointId);
            return new ForbidResult();
        }

        if (_store is null)
            return NotImplemented();

        var errors = Validate(body);
        if (errors.Count > 0)
            return BadRequest(errors);

        // The evaluator may save the rule's state between this read and the write; read again and
        // retry rather than fail the edit.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await _store.GetAlertRule(endpointId, ruleId);
            if (current is null)
                return new NotFoundObjectResult("Alert rule not found on this endpoint");

            var now = _time.GetUtcNow().UtcDateTime;
            var updated = current.Clone();
            updated.Name = body.Name!.Trim();
            updated.Enabled = body.Enabled;
            updated.Condition = ToCondition(body.Condition!);
            updated.ResolveAfterMinutes = body.ResolveAfterMinutes;
            updated.Targets = AssignTargets(body.Targets!, current.Targets);
            updated.UpdatedBy = CurrentUser();
            updated.UpdatedAtUtc = now;

            var conditionChanged = JsonConvert.SerializeObject(current.Condition) != JsonConvert.SerializeObject(updated.Condition);
            var disabled = current.Enabled && !updated.Enabled;
            if (conditionChanged || disabled)
            {
                // Closes an open incident with a notice to the targets the rule had, then starts
                // the new definition from Ok, in the same write.
                if (current.State.Status == AlertRuleStatus.Firing)
                {
                    AlertRuleEvaluator.Transition(current, updated.State, AlertRuleStatus.Ok,
                        disabled ? AlertRuleResolveReasons.RuleDisabled : AlertRuleResolveReasons.RuleChanged,
                        value: null, now);
                }

                updated.State.Status = AlertRuleStatus.Ok;
                updated.State.LastValue = null;
                updated.State.ConditionFalseSinceUtc = null;
                updated.State.SinceUtc = updated.Enabled ? null : now;
            }

            var saved = await _store.TrySaveAlertRuleDefinition(updated, current.Version);
            if (saved is null)
                continue;

            await _audit.LogAuditAsync(MessageAuditType.AlertRuleUpdated, _context,
                data: JsonConvert.SerializeObject(new { before = AuditView(current), after = AuditView(saved) }), endpointId: endpointId);
            var newTargets = saved.Targets.Where(t => current.Targets.All(old => old.TargetId != t.TargetId)).ToList();
            return new OkObjectResult(new Api.AlertRuleSaved
            {
                Rule = ToModel(saved, canManage: true),
                Secrets = Secrets(saved, newTargets),
            });
        }

        return Problem(StatusCodes.Status409Conflict, "Conflict", ["The rule kept changing while it was being saved. Try again."]);
    }

    /// <inheritdoc />
    public async Task<IActionResult> DeleteEndpointAlertRuleAsync(string endpointId, string ruleId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (!await _authorization.HasRoleAsync(AccessRole.Owner, endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.AlertRuleDeleted, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { ruleId }), endpointId: endpointId);
            return new ForbidResult();
        }

        if (_store is null)
            return NotImplemented();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await _store.GetAlertRule(endpointId, ruleId);
            if (current is null)
                return new NotFoundObjectResult("Alert rule not found on this endpoint");

            if (!await _store.DeleteAlertRule(endpointId, ruleId, current.Version))
                continue;

            await _audit.LogAuditAsync(MessageAuditType.AlertRuleDeleted, _context,
                data: JsonConvert.SerializeObject(AuditView(current)), endpointId: endpointId);
            if (current.State.Status == AlertRuleStatus.Firing)
                await NotifyDeletedAsync(current);
            return new NoContentResult();
        }

        return Problem(StatusCodes.Status409Conflict, "Conflict", ["The rule kept changing while it was being deleted. Try again."]);
    }

    /// <inheritdoc />
    public async Task<ActionResult<IEnumerable<string>>> GetAlertRulesFiringAsync()
    {
        if (_store is null)
            return new OkObjectResult(Array.Empty<string>());

        var readable = new List<string>();
        foreach (var endpointId in await _store.ListEndpointsWithFiringAlertRules())
        {
            if (await _authorization.HasRoleAsync(AccessRole.Reader, endpointId))
                readable.Add(endpointId);
        }

        return new OkObjectResult(readable);
    }

    /// <summary>The rule's targets get a resolved notice with the reason rule_deleted, best-effort.</summary>
    private async Task NotifyDeletedAsync(AlertRule rule)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var state = rule.State.Clone();
        AlertRuleEvaluator.Transition(rule, state, AlertRuleStatus.Ok, AlertRuleResolveReasons.RuleDeleted, value: null, now);
        foreach (var pending in state.PendingNotices.Where(p => p.Reason == AlertRuleResolveReasons.RuleDeleted))
        {
            var notice = new AlertRuleNotice(pending.NoticeId, rule.RuleId, rule.Name, rule.EndpointId, AlertRuleStatus.Ok,
                AlertRuleResolveReasons.RuleDeleted, null, rule.Condition, now,
                AlertRuleText.LinkFor(_delivery.WebAppUrl, rule.EndpointId, rule.Condition));
            try
            {
                await _delivery.DeliverAsync(notice, pending.Target, _context.RequestAborted);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Best-effort: the rule is gone, so there is no state to keep a retry in.
            }
        }
    }

    private List<string> Validate(Api.AlertRuleRequest? body)
    {
        var errors = new List<string>();
        if (body is null)
            return ["A rule is required."];

        if (string.IsNullOrWhiteSpace(body.Name) || body.Name.Trim().Length > 100)
            errors.Add("name must be 1 to 100 characters.");

        if (body.Condition is null)
            errors.Add("condition is required.");
        else
            ValidateCondition(body.Condition, errors);

        if (body.ResolveAfterMinutes is < 1 or > 60)
            errors.Add("resolveAfterMinutes must be 1 to 60.");

        var targets = body.Targets ?? [];
        if (targets.Count is < 1 or > MaxTargetsPerRule)
            errors.Add($"targets must have 1 to {MaxTargetsPerRule} entries.");
        foreach (var target in targets)
        {
            switch (target.Type?.ToLowerInvariant())
            {
                case "mail":
                    if (!System.Net.Mail.MailAddress.TryCreate(target.Address, out _))
                        errors.Add("A mail target needs a mail address.");
                    else if (!_delivery.MailEnabled)
                        errors.Add("Alert mail is not configured on this WebApp (NimBus:Alerts:Mail).");
                    break;
                case "webhook":
                    if (!_delivery.WebhookEnabled)
                        errors.Add("Alert webhooks are not configured on this WebApp (NimBus:Alerts:Webhook:SigningKey).");
                    else if (!_delivery.CanSendTo(target.Url))
                        errors.Add($"A webhook target needs an absolute https URL without user information, at most {EndpointAlertWebhook.MaxUrlLength} characters.");
                    break;
                default:
                    errors.Add("A target's type must be mail or webhook.");
                    break;
            }
        }

        return errors.Distinct().ToList();
    }

    private void ValidateCondition(Api.AlertRuleCondition condition, List<string> errors)
    {
        var kind = KindOf(condition);
        if (!AlertRuleConditionKinds.All.Contains(kind))
        {
            errors.Add($"condition.kind must be {string.Join(", ", AlertRuleConditionKinds.All)}.");
        }
        else if (!AvailableKinds().Contains(kind))
        {
            errors.Add($"condition.kind {kind} needs a storage provider that can read alert signals, and this deployment has none.");
        }

        switch (kind)
        {
            case AlertRuleConditionKinds.FailureBacklog:
            case AlertRuleConditionKinds.FailureOccurrences:
                if (condition.Threshold is < 1 or > MaxThreshold)
                    errors.Add($"condition.threshold must be 1 to {MaxThreshold}.");
                if (condition.ErrorTextContains is { Length: > 200 })
                    errors.Add("condition.errorTextContains must be at most 200 characters.");
                var eventTypes = condition.EventTypes ?? [];
                if (eventTypes.Count > 20 || eventTypes.Any(t => string.IsNullOrWhiteSpace(t) || t.Length > 200))
                    errors.Add("condition.eventTypes must be at most 20 names of 1 to 200 characters.");
                if (kind == AlertRuleConditionKinds.FailureBacklog)
                {
                    if ((condition.Statuses ?? []).Any(s => !KnownStatuses.Contains(s, StringComparer.OrdinalIgnoreCase)))
                        errors.Add("condition.statuses must be Failed, DeadLettered or Unsupported.");
                }
                else if (condition.WindowMinutes is not (>= MinWindowMinutes and <= MaxWindowMinutes))
                {
                    errors.Add($"condition.windowMinutes must be {MinWindowMinutes} to {MaxWindowMinutes}.");
                }

                var categories = condition.Categories ?? [];
                if (categories.Count > 0 && _classifications is null)
                    errors.Add("condition.categories needs Integration Intelligence, and this deployment can't read classifications.");
                else if (categories.Count > MaxCategories || categories.Any(c => !SelectableCategories.Contains(c)))
                    errors.Add($"condition.categories must be at most {MaxCategories} of {string.Join(", ", SelectableCategories)}.");

                break;
            case AlertRuleConditionKinds.BlockedSession:
                if (condition.Minutes is null && condition.DeferredThreshold is null)
                    errors.Add("A blocked-session condition needs minutes, deferredThreshold or both.");
                if (condition.Minutes is < 1 or > MaxMinutes)
                    errors.Add($"condition.minutes must be 1 to {MaxMinutes}.");
                if (condition.DeferredThreshold is < 1 or > MaxDeferredThreshold)
                    errors.Add($"condition.deferredThreshold must be 1 to {MaxDeferredThreshold}.");
                break;
            case AlertRuleConditionKinds.Silence:
                if (condition.Minutes is not (>= MinSilenceMinutes and <= MaxMinutes))
                    errors.Add($"condition.minutes must be {MinSilenceMinutes} to {MaxMinutes}.");
                break;
        }
    }

    /// <summary>The kinds this deployment can evaluate: the backlog always, the rest with an alert signal store.</summary>
    private IEnumerable<string> AvailableKinds()
        => _signals is null ? [AlertRuleConditionKinds.FailureBacklog] : AlertRuleConditionKinds.All;

    private static string KindOf(Api.AlertRuleCondition condition)
        => string.IsNullOrWhiteSpace(condition.Kind) ? AlertRuleConditionKinds.FailureBacklog : condition.Kind.Trim().ToLowerInvariant();

    /// <summary>The condition as stored: only the fields its kind uses are kept.</summary>
    private static AlertRuleCondition ToCondition(Api.AlertRuleCondition condition)
    {
        var kind = KindOf(condition);
        var counted = kind is AlertRuleConditionKinds.FailureBacklog or AlertRuleConditionKinds.FailureOccurrences;
        return new AlertRuleCondition
        {
            Kind = kind,
            EventTypes = counted ? (condition.EventTypes ?? []).Select(t => t.Trim()).Distinct(StringComparer.Ordinal).ToList() : [],
            Statuses = kind == AlertRuleConditionKinds.FailureBacklog
                ? (condition.Statuses ?? [])
                    .Select(s => KnownStatuses.First(k => string.Equals(k, s, StringComparison.OrdinalIgnoreCase)))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
                : [],
            ErrorTextContains = counted && !string.IsNullOrWhiteSpace(condition.ErrorTextContains) ? condition.ErrorTextContains.Trim() : null,
            Threshold = counted ? condition.Threshold : 1,
            WindowMinutes = kind == AlertRuleConditionKinds.FailureOccurrences ? condition.WindowMinutes : null,
            Minutes = kind is AlertRuleConditionKinds.BlockedSession or AlertRuleConditionKinds.Silence ? condition.Minutes : null,
            DeferredThreshold = kind == AlertRuleConditionKinds.BlockedSession ? condition.DeferredThreshold : null,
            Categories = counted ? (condition.Categories ?? []).Distinct(StringComparer.Ordinal).ToList() : [],
        };
    }

    /// <summary>
    /// Keeps the id (and so the signing secret) of every target sent with an existing id; numbers
    /// the new ones after the highest id the rule has used.
    /// </summary>
    private static List<AlertRuleTarget> AssignTargets(IEnumerable<Api.AlertRuleTargetModel> requested, IReadOnlyCollection<AlertRuleTarget> existing)
    {
        var next = existing
            .Select(t => int.TryParse(t.TargetId.TrimStart('t'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;
        var result = new List<AlertRuleTarget>();
        foreach (var target in requested)
        {
            var type = string.Equals(target.Type, "webhook", StringComparison.OrdinalIgnoreCase)
                ? AlertRuleTargetType.Webhook
                : AlertRuleTargetType.Mail;
            var kept = existing.FirstOrDefault(t => t.TargetId == target.Id && t.Type == type);
            result.Add(new AlertRuleTarget
            {
                TargetId = kept?.TargetId ?? string.Create(CultureInfo.InvariantCulture, $"t{next++}"),
                Type = type,
                Address = type == AlertRuleTargetType.Mail ? target.Address?.Trim() : null,
                Url = type == AlertRuleTargetType.Webhook ? target.Url?.Trim() : null,
            });
        }

        return result;
    }

    private List<Api.AlertRuleSecret> Secrets(AlertRule rule, IEnumerable<AlertRuleTarget> targets)
    {
        var signingKey = _configuration[AlertWebhookOptions.SectionName + ":SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey))
            return [];

        return targets
            .Where(t => t.Type == AlertRuleTargetType.Webhook)
            .Select(t => new Api.AlertRuleSecret
            {
                TargetId = t.TargetId,
                SigningSecret = EndpointAlertWebhook.DeriveRuleTargetSigningSecret(signingKey, rule.RuleId, t.TargetId),
            })
            .ToList();
    }

    private static Api.AlertRuleModel ToModel(AlertRule rule, bool canManage) => new()
    {
        Id = rule.RuleId,
        EndpointId = rule.EndpointId,
        Name = rule.Name,
        Enabled = rule.Enabled,
        Condition = new Api.AlertRuleCondition
        {
            Kind = rule.Condition.Kind,
            EventTypes = rule.Condition.EventTypes.ToList(),
            Statuses = rule.Condition.Statuses.ToList(),
            ErrorTextContains = rule.Condition.ErrorTextContains,
            Categories = rule.Condition.Categories.ToList(),
            Threshold = rule.Condition.Threshold,
            WindowMinutes = rule.Condition.WindowMinutes,
            Minutes = rule.Condition.Minutes,
            DeferredThreshold = rule.Condition.DeferredThreshold,
        },
        ResolveAfterMinutes = rule.ResolveAfterMinutes,
        // Readers see how many targets there are and of which kind; where notices go is for
        // Owners, as with subscriptions.
        Targets = rule.Targets.Select(t => new Api.AlertRuleTargetModel
        {
            Id = t.TargetId,
            Type = t.Type == AlertRuleTargetType.Webhook ? "webhook" : "mail",
            Address = canManage ? t.Address : null,
            Url = canManage ? t.Url : null,
        }).ToList(),
        State = new Api.AlertRuleStateModel
        {
            Status = rule.State.Status.ToString(),
            SinceUtc = rule.State.SinceUtc,
            LastValue = rule.State.LastValue,
            ConditionFalseSinceUtc = rule.State.ConditionFalseSinceUtc,
            PendingNotices = rule.State.PendingNotices.Count,
            Transitions = rule.State.Transitions.Select(t => new Api.AlertRuleTransitionModel
            {
                Seq = t.Seq,
                Status = t.Status.ToString(),
                AtUtc = t.AtUtc,
                Value = t.Value,
                Reason = t.Reason,
            }).ToList(),
        },
        CreatedBy = rule.CreatedBy,
        CreatedAtUtc = rule.CreatedAtUtc,
        UpdatedBy = rule.UpdatedBy,
        UpdatedAtUtc = rule.UpdatedAtUtc,
    };

    private static object AuditView(AlertRule rule) => new
    {
        ruleId = rule.RuleId,
        name = rule.Name,
        enabled = rule.Enabled,
        condition = rule.Condition,
        resolveAfterMinutes = rule.ResolveAfterMinutes,
        targets = rule.Targets.Select(t => new { id = t.TargetId, type = t.Type.ToString(), t.Address, t.Url }),
    };

    private string CurrentUser()
    {
        var name = _context.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(name) ? _authorization.GetCurrentUserName() ?? "unknown" : name;
    }

    private static ObjectResult BadRequest(IEnumerable<string> errors)
        => Problem(StatusCodes.Status400BadRequest, "Invalid", errors);

    private static ObjectResult Problem(int status, string code, IEnumerable<string> errors)
        => new(new Api.ProblemDetails { Code = code, Errors = errors.ToList() }) { StatusCode = status };

    private static ObjectResult NotImplemented()
        => Problem(StatusCodes.Status501NotImplemented, "Unavailable", ["The storage provider cannot store alert rules."]);
}
