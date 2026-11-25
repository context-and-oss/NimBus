using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace NimBus.Adapter.Mcp;

/// <summary>
/// The read log (Spec 051 §7): one structured entry per tool call, event <c>AdapterRead</c>, with
/// the caller, the client, the tool, the entity and the key. It never carries a record value.
/// </summary>
internal sealed partial class AdapterReadLog(ILoggerFactory loggers)
{
    /// <summary>The logger category, for routing the read log to its own sink.</summary>
    public const string Category = "NimBus.Adapter.Mcp.Reads";

    private readonly ILogger _logger = loggers.CreateLogger(Category);
    private readonly ILogger _failures = loggers.CreateLogger("NimBus.Adapter.Mcp");

    /// <summary>An adapter call that threw; the caller is told only the exception's type.</summary>
    public void Failed(string tool, Exception exception) => LogFailed(_failures, tool, exception);

    [LoggerMessage(
        EventId = 5102,
        EventName = "AdapterToolFailed",
        Level = LogLevel.Warning,
        Message = "Adapter tool {Tool} failed in the adapter")]
    private static partial void LogFailed(ILogger logger, string tool, Exception exception);

    public void Read(ClaimsPrincipal? caller, string tool, string? entity, string? key, string outcome)
        => LogRead(
            _logger,
            tool,
            entity,
            key,
            caller?.FindFirst("oid")?.Value,
            caller?.FindFirst("azp")?.Value ?? caller?.FindFirst("appid")?.Value,
            outcome);

    [LoggerMessage(
        EventId = 5101,
        EventName = "AdapterRead",
        Level = LogLevel.Information,
        Message = "Adapter read {Tool} entity {Entity} key {Key} by {CallerObjectId} through {CallerAppId}: {Outcome}")]
    private static partial void LogRead(ILogger logger, string tool, string? entity, string? key, string? callerObjectId, string? callerAppId, string outcome);
}
