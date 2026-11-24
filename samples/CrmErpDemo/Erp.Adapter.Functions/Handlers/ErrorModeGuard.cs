using CrmErpDemo.Contracts.Demo;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using Erp.Adapter.Functions.Mapping;
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
/// <remarks>
/// One reason is not a thrown, written exception. <see cref="ErpFailureReasons.ApplicationDefect"/>
/// runs <see cref="CustomerPostingGroup"/>, a mapping step with a real defect, on the message, so
/// it fails only the messages the defect mishandles and its error quotes their own data.
/// </remarks>
public static class ErrorModeGuard
{
    public static async Task ThrowIfEnabledAsync(
        IServiceModeClient modeClient,
        IEventHandlerContext context,
        ILogger logger,
        CancellationToken cancellationToken,
        object? message = null)
    {
        var mode = await modeClient.GetErrorModeAsync(cancellationToken);
        if (!mode.Enabled)
            return;

        var reason = ErpFailureReasons.Decide(mode.FailureRate, mode.Reason, Random.Shared);
        if (reason is null)
            return;

        logger.LogWarning(
            "Message {MessageId} ({EventType}) drew a failure — ERP error mode is enabled with reason {Reason} (selected {SelectedReason}, failure rate {FailureRate:P0}).",
            context.MessageId,
            context.EventType,
            reason.Id,
            mode.Reason ?? ErpFailureReasons.Default,
            mode.FailureRate);

        Fail(reason, message, context.EventType ?? "event");
    }

    /// <summary>
    /// Fails a message for <paramref name="reason"/>. Every reason throws its written exception
    /// except the application defect, which runs the defective mapping step on
    /// <paramref name="message"/> and returns when the defect does not affect it.
    /// </summary>
    /// <param name="reason">A failure from <see cref="ErpFailureReasons.All"/>.</param>
    /// <param name="message">The message being handled; null when the handler has none to map.</param>
    /// <param name="eventType">The inbound event type.</param>
    public static void Fail(ErpFailureReason reason, object? message, string eventType)
    {
        ArgumentNullException.ThrowIfNull(reason);
        if (reason.Id != ErpFailureReasons.ApplicationDefect)
            throw ErpFailureReasons.CreateException(reason.Id, eventType);

        var countryCode = message switch
        {
            CrmAccountCreated created => created.CountryCode,
            CrmAccountUpdated updated => updated.CountryCode,
            _ => null,
        };
        if (countryCode is not null)
            _ = CustomerPostingGroup.For(countryCode);
    }
}
