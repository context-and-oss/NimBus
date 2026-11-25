using CrmErpDemo.Contracts.Demo;
using NimBus.Core.Messages;

namespace Erp.Adapter.Functions;

/// <summary>
/// The ERP adapter's retry rules, passed to <c>ConfigureRetryPolicies</c> in <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// <para>
/// There is one rule, and it is the sample's retry showcase: a message that fails because the
/// pricing service rate-limited the adapter (<see cref="ErpPricingThrottledException"/>, the
/// "Rate limited" error-mode reason in erp-web) is retried by NimBus up to three times. A retry is
/// a scheduled message in the same session, so the session stays ordered while it waits and every
/// attempt is in the event's history. When the budget runs out the message is recorded as Failed,
/// like a failure no rule matches.
/// </para>
/// <para>
/// Nothing else is retried. In particular the "Downstream timeout" reason stays a plain
/// <see cref="TimeoutException"/> with no rule, so those failures still wait in Failed for an
/// operator or an operations agent to resubmit. A real adapter would map every failure of its
/// target system to a typed exception and write a rule per kind; see
/// <c>docs/building-adapters.md#resilience</c>.
/// </para>
/// </remarks>
public static class ErpResilience
{
    /// <summary>Adds the adapter's retry rules to <paramref name="policies"/>.</summary>
    /// <param name="policies">The subscriber's retry policy provider.</param>
    public static void ConfigureRetries(DefaultRetryPolicyProvider policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        // Matched by type, not by text: a text rule on "429" would also match an id or a line
        // number. The exception carries Retry-After (5 seconds in the demo), and each wait is the
        // longer of that and the delay below, never above MaxDelay: 5, 5 and 8 seconds here.
        policies.AddExceptionRule<ErpPricingThrottledException>(new RetryPolicy
        {
            MaxRetries = 3,
            Strategy = BackoffStrategy.Exponential,
            BaseDelay = TimeSpan.FromSeconds(2),
            MaxDelay = TimeSpan.FromSeconds(30),
        });
    }
}
