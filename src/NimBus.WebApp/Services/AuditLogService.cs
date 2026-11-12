using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services;

/// <summary>
/// Default <see cref="IAuditLogService"/> implementation. Writes each audit row
/// to the durable <see cref="IMessageTrackingStore"/> and emits a structured
/// "Webapp AuditEvent occurred" log event (captured by Application Insights via
/// <see cref="ILogger"/> telemetry). Both writes are best-effort — failures
/// are absorbed and logged as warnings so the user's privileged action proceeds.
/// </summary>
public sealed partial class AuditLogService : IAuditLogService
{
    /// <summary>4 KB cap on the <c>Data</c> field per NFR-004.</summary>
    internal const int DataTruncationLimit = 4096;
    internal const string TruncationSuffix = "… [truncated]";
    internal const string AnonymousAuditorName = "anonymous";
    internal const string StructuredLogMessage = "Webapp AuditEvent occurred";

    private readonly ILogger<AuditLogService> _logger;
    private readonly IMessageTrackingStore _messageStore;
    private readonly IAuditSettingsProvider? _settings;

    /// <param name="logger">Structured-log sink (Application Insights).</param>
    /// <param name="messageStore">Durable sink.</param>
    /// <param name="settings">The Settings → Audit logging selection. When null every type is recorded.</param>
    public AuditLogService(
        ILogger<AuditLogService> logger,
        IMessageTrackingStore messageStore,
        IAuditSettingsProvider? settings = null)
    {
        _logger = logger;
        _messageStore = messageStore;
        _settings = settings;
    }

    /// <inheritdoc/>
    public async Task LogAuditAsync(
        MessageAuditType type,
        HttpContext context,
        bool accessDenied = false,
        string? data = null,
        string? eventId = null,
        string? endpointId = null,
        string? eventTypeId = null,
        string? auditorNameOverride = null,
        CancellationToken cancellationToken = default)
    {
        // An operator can switch a type off in Settings → Audit logging; the selection governs both
        // sinks. A denied attempt is recorded regardless — it is the security signal the
        // log exists for, not routine traffic.
        if (!accessDenied && _settings != null && !await IsRecordedAsync(type).ConfigureAwait(false))
        {
            return;
        }

        var entity = CreateEntity(type, context, accessDenied, data, eventId, endpointId, auditorNameOverride);

        // (1) Durable sink: message store. Catch and log so a transient store
        //     failure cannot fail the user action — see User Story 5.
        try
        {
            await _messageStore.StoreMessageAudit(eventId ?? string.Empty, entity, endpointId, eventTypeId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogAuditWriteToMessageStoreFailed(_logger, ex, entity.AuditType, ForLog(entity.EventId), ForLog(entity.EndpointId), ForLog(entity.AuditorName)!);
        }

        EmitStructuredLog(entity);
    }

    /// <inheritdoc/>
    public async Task LogRequiredAuditAsync(
        MessageAuditType type,
        HttpContext context,
        string? data = null,
        string? eventId = null,
        string? endpointId = null,
        string? eventTypeId = null,
        CancellationToken cancellationToken = default)
    {
        // No audit-logging selection check: a command recorded here may only run once its row exists.
        var entity = CreateEntity(type, context, accessDenied: false, data, eventId, endpointId, auditorNameOverride: null);
        try
        {
            await _messageStore.StoreMessageAudit(eventId ?? string.Empty, entity, endpointId, eventTypeId)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRequiredAuditWriteFailedForAuditType(_logger, ex, entity.AuditType, ForLog(entity.EventId), ForLog(entity.EndpointId), ForLog(entity.AuditorName)!);
            throw new AuditUnavailableException("The audit row could not be recorded, so the command was not run.", ex);
        }

        EmitStructuredLog(entity);
    }

    private static MessageAuditEntity CreateEntity(
        MessageAuditType type,
        HttpContext? context,
        bool accessDenied,
        string? data,
        string? eventId,
        string? endpointId,
        string? auditorNameOverride) => new()
    {
        AuditorName = string.IsNullOrWhiteSpace(auditorNameOverride)
            ? ResolveAuditorName(context)
            : auditorNameOverride,
        AuditTimestamp = DateTime.UtcNow,
        AuditType = type,
        AccessDenied = accessDenied,
        Data = TruncateData(data),
        EventId = eventId,
        EndpointId = endpointId,
    };

    /// <summary>
    /// Escapes CR and LF in a caller-supplied value bound for the log sink, so it cannot start a
    /// forged log entry. The durable audit row keeps the original value.
    /// </summary>
    private static string? ForLog(string? value) =>
        value?.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    // Short-term sink: App Insights via structured ILogger. The BeginScope dictionary keys
    // MUST match the entity property names so KQL queries can pivot on
    // customDimensions.<FieldName>.
    private void EmitStructuredLog(MessageAuditEntity entity)
    {
        try
        {
            var scope = new Dictionary<string, object?>
            {
                [nameof(MessageAuditEntity.AuditorName)] = ForLog(entity.AuditorName),
                [nameof(MessageAuditEntity.AuditTimestamp)] = entity.AuditTimestamp,
                [nameof(MessageAuditEntity.AuditType)] = entity.AuditType.ToString(),
                [nameof(MessageAuditEntity.Comment)] = ForLog(entity.Comment),
                [nameof(MessageAuditEntity.AccessDenied)] = entity.AccessDenied,
                [nameof(MessageAuditEntity.Data)] = ForLog(entity.Data),
                [nameof(MessageAuditEntity.EventId)] = ForLog(entity.EventId),
                [nameof(MessageAuditEntity.EndpointId)] = ForLog(entity.EndpointId),
            };

            using (_logger.BeginScope(scope))
            {
                LogWebappAuditEventOccurred(_logger);
            }
        }
        catch (Exception ex)
        {
            LogAuditAppInsightsEmitFailed(_logger, ex, entity.AuditType, ForLog(entity.EventId), ForLog(entity.EndpointId), ForLog(entity.AuditorName)!);
        }
    }

    private async Task<bool> IsRecordedAsync(MessageAuditType type)
    {
        try
        {
            return await _settings!.IsRecordedAsync(type).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The provider already fails open on store faults; this guards anything else
            // so the audit contract (never fail the user action) holds.
            LogAuditSettingsCheckFailedForAuditType(_logger, ex, type);
            return true;
        }
    }

    internal static string ResolveAuditorName(HttpContext? context)
    {
        var user = context?.User;
        if (user == null)
        {
            return AnonymousAuditorName;
        }

        // Per spec FR-020: name claim → ClaimTypes.Name → preferred_username → "anonymous"
        var name = user.FindFirst(c => string.Equals(c.Type, "name", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        name = user.FindFirst(ClaimTypes.Name)?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        name = user.FindFirst("preferred_username")?.Value;
        if (!string.IsNullOrEmpty(name))
        {
            return name;
        }

        return AnonymousAuditorName;
    }

    internal static string? TruncateData(string? data)
    {
        if (string.IsNullOrEmpty(data) || data.Length <= DataTruncationLimit)
        {
            return data;
        }

        return string.Concat(data.AsSpan(0, DataTruncationLimit), TruncationSuffix);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit write to message store failed for AuditType={AuditType} EventId={EventId} EndpointId={EndpointId} AuditorName={AuditorName}")]
    private static partial void LogAuditWriteToMessageStoreFailed(ILogger logger, Exception exception, MessageAuditType auditType, string? eventId, string? endpointId, string auditorName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Required audit write failed for AuditType={AuditType} EventId={EventId} EndpointId={EndpointId} AuditorName={AuditorName}; the command is refused")]
    private static partial void LogRequiredAuditWriteFailedForAuditType(ILogger logger, Exception exception, MessageAuditType auditType, string? eventId, string? endpointId, string auditorName);

    [LoggerMessage(Level = LogLevel.Information, Message = StructuredLogMessage)]
    private static partial void LogWebappAuditEventOccurred(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit App Insights emit failed for AuditType={AuditType} EventId={EventId} EndpointId={EndpointId} AuditorName={AuditorName}")]
    private static partial void LogAuditAppInsightsEmitFailed(ILogger logger, Exception exception, MessageAuditType auditType, string? eventId, string? endpointId, string auditorName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audit settings check failed for AuditType={AuditType}; recording the row")]
    private static partial void LogAuditSettingsCheckFailedForAuditType(ILogger logger, Exception exception, MessageAuditType auditType);
}
