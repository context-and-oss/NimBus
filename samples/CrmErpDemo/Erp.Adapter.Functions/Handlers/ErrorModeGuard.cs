using CrmErpDemo.Contracts.Demo;
using Erp.Adapter.Functions.Clients;
using Microsoft.Extensions.Logging;
using NimBus.SDK.EventHandlers;

namespace Erp.Adapter.Functions.Handlers;

/// <summary>
/// First call inside every ERP handler. When erp-web has error mode on, it rolls against the
/// failure rate and, for a message that fails, throws the exception that matches the selected
/// failure reason (see <see cref="ErpFailureReasons"/>; <c>random</c> picks one per message)
/// before the handler does any work, so NimBus records a realistic <c>ErrorType</c>/<c>ErrorText</c>
/// for the Integration Intelligence card to classify. Each delivery rolls again, so a resubmit
/// can succeed.
/// </summary>
public static class ErrorModeGuard
{
    public static async Task ThrowIfEnabledAsync(
        IServiceModeClient modeClient,
        IEventHandlerContext context,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var mode = await modeClient.GetErrorModeAsync(cancellationToken);
        if (!mode.Enabled)
            return;

        var reason = ErpFailureReasons.Decide(mode.FailureRate, mode.Reason, Random.Shared);
        if (reason is null)
            return;

        logger.LogWarning(
            "Failing message {MessageId} ({EventType}) from handler — ERP error mode is enabled with reason {Reason} (selected {SelectedReason}, failure rate {FailureRate:P0}).",
            context.MessageId,
            context.EventType,
            reason.Id,
            mode.Reason ?? ErpFailureReasons.Default,
            mode.FailureRate);

        throw ErpFailureReasons.CreateException(reason.Id, context.EventType ?? "event");
    }
}
