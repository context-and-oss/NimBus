namespace Erp.Api;

/// <summary>
/// One request received from a NimBus endpoint-alert webhook (an Alerts-tab subscription or alert rule), kept
/// as it arrived so a test can check the signature: the raw body and the <c>X-NimBus-*</c> headers.
/// </summary>
public sealed record ReceivedEndpointAlert(
    string Body,
    string? Subscription,
    string? Notice,
    string? Delivery,
    string? Timestamp,
    string? Signature,
    DateTimeOffset ReceivedAt);

/// <summary>
/// In-memory, thread-safe store of the most recent endpoint-alert webhook requests (newest first,
/// capped), for the end-to-end tests. Non-persistent, like <see cref="AlertsState"/>.
/// </summary>
public sealed class EndpointAlertWebhookState
{
    private const int Capacity = 100;
    private readonly object _gate = new();
    private readonly LinkedList<ReceivedEndpointAlert> _requests = new();

    /// <summary>Records a request, dropping the oldest beyond the cap.</summary>
    public void Add(ReceivedEndpointAlert request)
    {
        lock (_gate)
        {
            _requests.AddFirst(request);
            while (_requests.Count > Capacity)
            {
                _requests.RemoveLast();
            }
        }
    }

    /// <summary>The recorded requests, newest first.</summary>
    public IReadOnlyList<ReceivedEndpointAlert> Snapshot()
    {
        lock (_gate)
        {
            return _requests.ToList();
        }
    }
}
