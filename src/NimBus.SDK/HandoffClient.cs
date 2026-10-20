using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core.Messages;

namespace NimBus.SDK;

/// <summary>
/// Endpoint binding for an <see cref="IHandoffClient"/> registration. Set by
/// <c>AddNimBusHandoffClient(endpoint)</c> / <c>AddNimBusSubscriber(endpoint, …)</c>
/// so a single process can register clients for multiple endpoints.
/// </summary>
public sealed class HandoffClientOptions
{
    /// <summary>The subscriber endpoint whose pending-handoff rows this client settles. Required.</summary>
    public string Endpoint { get; set; } = null!;
}

/// <summary>
/// Default <see cref="IHandoffClient"/> implementation. Builds the matching
/// Service Bus control message via <see cref="HandoffControlMessageFactory"/>
/// and publishes it through the injected <see cref="ISender"/>.
///
/// <para>Reuses the message-builder shared with the legacy
/// <c>NimBus.Manager.IManagerClient</c> path so on-wire behaviour is
/// byte-identical regardless of which API the adapter used.</para>
///
/// <para>Carries no message-store dependency: the settlement process only
/// needs ServiceBus access. This matches the canonical CrmErpDemo shape
/// where the settlement worker lives separately from the Resolver that
/// owns the audit DB.</para>
/// </summary>
public sealed class HandoffClient : IHandoffClient
{
    private readonly ISender _sender;
    private readonly string _endpoint;
    private readonly ILogger<HandoffClient>? _logger;

    public HandoffClient(
        ISender sender,
        HandoffClientOptions options,
        ILogger<HandoffClient>? logger = null)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (string.IsNullOrEmpty(options.Endpoint))
            throw new ArgumentException("HandoffClientOptions.Endpoint must be set.", nameof(options));

        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _endpoint = options.Endpoint;
        _logger = logger;
    }

    public async Task CompleteAsync(HandoffSettlement coords, object? result = null, CancellationToken cancellationToken = default)
    {
        ValidateCoords(coords);
        var detailsJson = SerializeResult(result);
        var message = HandoffControlMessageFactory.CreateCompleted(ToFactoryCoords(coords), detailsJson);
        await _sender.Send(message, 0, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "HandoffClient.CompleteAsync: published HandoffCompletedRequest to {Endpoint} (EventId={EventId}, SessionId={SessionId}).",
            _endpoint, coords.EventId, coords.SessionId);
    }

    public async Task FailAsync(HandoffSettlement coords, string errorText, string? errorType = null, CancellationToken cancellationToken = default)
    {
        ValidateCoords(coords);
        var message = HandoffControlMessageFactory.CreateFailed(ToFactoryCoords(coords), errorText, errorType);
        await _sender.Send(message, 0, cancellationToken).ConfigureAwait(false);
        _logger?.LogInformation(
            "HandoffClient.FailAsync: published HandoffFailedRequest to {Endpoint} (EventId={EventId}, SessionId={SessionId}, ErrorType={ErrorType}).",
            _endpoint, coords.EventId, coords.SessionId, errorType);
    }

    private HandoffSettlementCoordinates ToFactoryCoords(HandoffSettlement coords) => new(
        To: _endpoint,
        EventId: coords.EventId,
        SessionId: coords.SessionId,
        CorrelationId: coords.CorrelationId,
        ParentMessageId: coords.MessageId,
        OriginatingMessageId: coords.OriginatingMessageId,
        EventTypeId: coords.EventTypeId);

    private static void ValidateCoords(HandoffSettlement coords)
    {
        // Only the fields the wire genuinely requires are strict: EventId and
        // MessageId identify the pending row being settled, and SessionId is
        // required for Service Bus to route the control message into the same
        // session so the subscriber's FIFO replay holds. The lineage fields
        // (CorrelationId, OriginatingMessageId, EventTypeId) may be null on
        // legacy store rows — the Manager settlement path always passed them
        // through, and HandoffControlMessageFactory falls back
        // OriginatingMessageId ?? ParentMessageId on the wire — so rejecting
        // them here would make old pending rows permanently unsettleable.
        if (coords is null) throw new ArgumentNullException(nameof(coords));
        if (string.IsNullOrEmpty(coords.EventId)) throw new ArgumentException("EventId must be supplied.", nameof(coords));
        if (string.IsNullOrEmpty(coords.SessionId)) throw new ArgumentException("SessionId must be supplied.", nameof(coords));
        if (string.IsNullOrEmpty(coords.MessageId)) throw new ArgumentException("MessageId must be supplied.", nameof(coords));
    }

    // Strings pass through verbatim so adapters that already hand-roll JSON
    // aren't double-serialised. Everything else goes through Newtonsoft to
    // stay consistent with NimBus's wire format. Null result → no payload.
    private static string? SerializeResult(object? result) => result switch
    {
        null => null,
        string s => s,
        _ => JsonConvert.SerializeObject(result),
    };
}
