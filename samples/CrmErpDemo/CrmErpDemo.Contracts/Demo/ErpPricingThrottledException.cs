using NimBus.Core.Messages;

namespace CrmErpDemo.Contracts.Demo;

/// <summary>
/// The ERP pricing service answered HTTP 429: the request was fine and can be repeated once the
/// rate-limit window has passed. It is the one error-mode failure the ERP adapter has a retry
/// rule for (<c>Erp.Adapter.Functions/ErpResilience.cs</c>), which matches it by type.
/// </summary>
/// <remarks>
/// <see cref="IRetryAfterHint"/> hands NimBus the wait the service asked for: the scheduled retry
/// waits for the longer of the rule's own delay and <see cref="RetryAfter"/>.
/// </remarks>
/// <param name="message">The failure text NimBus records as evidence.</param>
/// <param name="retryAfter">The <c>Retry-After</c> delay the service sent, or null when it sent none.</param>
public sealed class ErpPricingThrottledException(string message, TimeSpan? retryAfter)
    : Exception(message), IRetryAfterHint
{
    /// <inheritdoc />
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
