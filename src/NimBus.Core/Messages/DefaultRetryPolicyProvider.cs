using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages;

/// <summary>
/// Configurable retry policy provider. Supports per-event-type policies,
/// exception-based policies, and a default fallback policy.
/// </summary>
public class DefaultRetryPolicyProvider : IRetryPolicyProvider
{
    private readonly Dictionary<string, RetryPolicy> _eventTypePolicies = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ExceptionRetryRule> _exceptionRules = new();
    private RetryPolicy? _defaultPolicy;

    /// <summary>
    /// Sets a retry policy for a specific event type.
    /// </summary>
    public DefaultRetryPolicyProvider AddEventTypePolicy(string eventTypeId, RetryPolicy policy)
    {
        _eventTypePolicies[eventTypeId] = policy ?? throw new ArgumentNullException(nameof(policy));
        return this;
    }

    /// <summary>
    /// Adds a retry rule that matches when the exception text contains the specified text.
    /// Optionally scoped to specific event types.
    /// </summary>
    /// <remarks>
    /// The text is <c>"{exception.InnerException} {exception}"</c>, which includes type names, messages
    /// and stack traces, so a short fragment such as <c>"429"</c> can match unrelated text. Prefer
    /// <see cref="AddExceptionRule{TException}(RetryPolicy, string[])"/> for exceptions you own.
    /// </remarks>
    public DefaultRetryPolicyProvider AddExceptionRule(string exceptionContains, RetryPolicy policy, params string[] eventTypeIds)
    {
        _exceptionRules.Add(new ExceptionRetryRule
        {
            ExceptionContains = exceptionContains ?? throw new ArgumentNullException(nameof(exceptionContains)),
            Policy = policy ?? throw new ArgumentNullException(nameof(policy)),
            EventTypeIds = ToEventTypeSet(eventTypeIds)
        });
        return this;
    }

    /// <summary>
    /// Adds a retry rule that matches when the handler's exception, or any of its inner exceptions,
    /// is a <typeparamref name="TException"/> (including subclasses). Optionally scoped to specific
    /// event types.
    /// </summary>
    /// <remarks>
    /// Typed and text rules are checked together in registration order; the first match wins.
    /// Typed rules only match through <see cref="GetRetryPolicy(string, Exception, string?)"/>, which is
    /// the overload the message handler uses.
    /// </remarks>
    public DefaultRetryPolicyProvider AddExceptionRule<TException>(RetryPolicy policy, params string[] eventTypeIds)
        where TException : Exception
    {
        _exceptionRules.Add(new ExceptionRetryRule
        {
            ExceptionType = typeof(TException),
            Policy = policy ?? throw new ArgumentNullException(nameof(policy)),
            EventTypeIds = ToEventTypeSet(eventTypeIds)
        });
        return this;
    }

    /// <summary>
    /// Sets a default retry policy used when no specific policy matches.
    /// </summary>
    public DefaultRetryPolicyProvider SetDefaultPolicy(RetryPolicy policy)
    {
        _defaultPolicy = policy;
        return this;
    }

    /// <inheritdoc />
    public RetryPolicy? GetRetryPolicy(string eventTypeId, string exceptionMessage, string? endpoint = null) =>
        Resolve(eventTypeId, exceptionMessage, exception: null);

    /// <inheritdoc />
    public RetryPolicy? GetRetryPolicy(string eventTypeId, Exception exception, string? endpoint = null) =>
        Resolve(eventTypeId, $"{exception?.InnerException} {exception}", exception);

    private RetryPolicy? Resolve(string eventTypeId, string exceptionText, Exception? exception)
    {
        // 1. Check exception-based rules first (most specific), in registration order
        foreach (var rule in _exceptionRules)
        {
            if (rule.EventTypeIds != null && !rule.EventTypeIds.Contains(eventTypeId))
                continue;

            if (rule.Matches(exceptionText, exception))
                return rule.Policy;
        }

        // 2. Check event-type-specific policies
        if (!string.IsNullOrEmpty(eventTypeId) && _eventTypePolicies.TryGetValue(eventTypeId, out var policy))
            return policy;

        // 3. Fall back to default policy
        return _defaultPolicy;
    }

    private static HashSet<string>? ToEventTypeSet(string[]? eventTypeIds) =>
        eventTypeIds?.Length > 0 ? new HashSet<string>(eventTypeIds, StringComparer.OrdinalIgnoreCase) : null;

    private class ExceptionRetryRule
    {
        public string? ExceptionContains { get; set; }
        public Type? ExceptionType { get; set; }
        public RetryPolicy Policy { get; set; } = null!;
        public HashSet<string>? EventTypeIds { get; set; }

        public bool Matches(string exceptionText, Exception? exception)
        {
            if (ExceptionType != null)
            {
                for (var current = exception; current != null; current = current.InnerException)
                {
                    if (ExceptionType.IsInstanceOfType(current))
                        return true;
                }

                return false;
            }

            return !string.IsNullOrEmpty(exceptionText)
                && exceptionText.Contains(ExceptionContains!, StringComparison.OrdinalIgnoreCase);
        }
    }
}
