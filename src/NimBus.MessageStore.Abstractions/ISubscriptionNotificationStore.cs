using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Records when an endpoint subscription was last notified, as a compare-and-set, so that
/// concurrent alert senders honor the subscription's frequency. Separate from
/// <see cref="ISubscriptionStore"/> so existing <see cref="ISubscriptionStore"/>
/// implementations keep compiling.
/// </summary>
public interface ISubscriptionNotificationStore
{
    /// <summary>
    /// Sets the subscription's <c>NotifiedAt</c> to <paramref name="notifiedAt"/> only when it
    /// currently equals <paramref name="expectedNotifiedAt"/>. A <see langword="null"/> or empty
    /// value means "never notified".
    /// </summary>
    /// <param name="subscriptionId">The subscription's global id.</param>
    /// <param name="expectedNotifiedAt">The value the caller last read.</param>
    /// <param name="notifiedAt">
    /// The new value, in the <c>yyyy-MM-dd HH:mm</c> UTC format, or <see langword="null"/> to
    /// clear it.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>
    /// <see langword="true"/> when the value was set; <see langword="false"/> when the
    /// subscription doesn't exist or its <c>NotifiedAt</c> no longer equals
    /// <paramref name="expectedNotifiedAt"/>.
    /// </returns>
    Task<bool> TrySetNotifiedAtAsync(
        string subscriptionId,
        string? expectedNotifiedAt,
        string? notifiedAt,
        CancellationToken cancellationToken = default);
}
