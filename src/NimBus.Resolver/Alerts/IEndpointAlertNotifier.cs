using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Tells an endpoint's alert subscribers (the endpoint's Alerts tab) about a failure the
/// Resolver recorded. Best-effort: the Resolver has already settled the message.
/// </summary>
public interface IEndpointAlertNotifier
{
    /// <summary>Notifies the subscribers that match <paramref name="context"/>.</summary>
    /// <param name="context">The recorded outcome.</param>
    /// <param name="cancellationToken">Cancels the notification.</param>
    Task NotifyAsync(EndpointAlertContext context, CancellationToken cancellationToken = default);
}

/// <summary>The notifier used when alert mail isn't configured: does nothing.</summary>
public sealed class NoopEndpointAlertNotifier : IEndpointAlertNotifier
{
    /// <inheritdoc />
    public Task NotifyAsync(EndpointAlertContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
