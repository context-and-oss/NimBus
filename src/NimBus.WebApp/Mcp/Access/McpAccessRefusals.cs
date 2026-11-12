using System.Collections.Concurrent;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// Writes <see cref="MessageAuditType.McpAccessRefused"/> rows (Spec 037). Refused calls are
/// not rate-limited, because the rate limiter runs after authorization, so each instance
/// writes at most one row per tenant, caller, client, reason and tool in <see cref="Window"/>.
/// </summary>
public sealed partial class McpAccessRefusals
{
    /// <summary>The deduplication window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const int PruneThreshold = 10_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger<McpAccessRefusals> _logger;

    /// <summary>Creates the singleton.</summary>
    public McpAccessRefusals(TimeProvider time, ILogger<McpAccessRefusals> logger)
    {
        _time = time;
        _logger = logger;
    }

    /// <summary>Records a refusal unless the same one was recorded within <see cref="Window"/>.</summary>
    public async Task RecordAsync(HttpContext context, McpCaller caller, McpRefusalReason reason, string? tool = null, string? endpointId = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(caller);

        var now = _time.GetUtcNow();
        var key = string.Join('|', caller.TenantId, caller.CallerId, caller.ClientId, reason, tool, endpointId);
        if (_lastWritten.TryGetValue(key, out var last) && now - last < Window)
            return;
        _lastWritten[key] = now;
        if (_lastWritten.Count > PruneThreshold)
            Prune(now);

        var audit = context.RequestServices.GetService<IAuditLogService>();
        if (audit is null)
            return;

        try
        {
            var data = JsonConvert.SerializeObject(new
            {
                channel = "Mcp",
                reason = ReasonName(reason),
                tool,
                clientId = caller.ClientId,
                endpointId,
            });
            await audit.LogAuditAsync(MessageAuditType.McpAccessRefused, context, accessDenied: true, data: data, endpointId: endpointId)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Auditing a refusal is best-effort; the call is refused either way.
            LogCouldNotAuditARefusedMcp(_logger, ex, reason);
        }
    }

    /// <summary>The name recorded in the audit data.</summary>
    public static string ReasonName(McpRefusalReason reason) => reason.ToString().ToLowerInvariant();

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, written) in _lastWritten)
        {
            if (now - written >= Window)
                _lastWritten.TryRemove(key, out _);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not audit a refused MCP call ({Reason})")]
    private static partial void LogCouldNotAuditARefusedMcp(ILogger logger, Exception exception, McpRefusalReason reason);
}
