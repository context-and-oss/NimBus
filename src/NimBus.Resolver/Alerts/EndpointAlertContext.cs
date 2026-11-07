using System;
using NimBus.MessageStore;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// An outcome the Resolver recorded that endpoint alert subscribers may need to hear about.
/// Deliberately carries no event payload: it may hold personal data and must not reach a mail.
/// </summary>
public sealed record EndpointAlertContext
{
    /// <summary>The endpoint whose state changed.</summary>
    public required string EndpointId { get; init; }

    /// <summary>The affected event.</summary>
    public required string EventId { get; init; }

    /// <summary>The event's type, when known.</summary>
    public string? EventTypeId { get; init; }

    /// <summary>The event's session, when known.</summary>
    public string? SessionId { get; init; }

    /// <summary>The recorded outcome: <see cref="ResolutionStatus.Failed"/> or <see cref="ResolutionStatus.DeadLettered"/>.</summary>
    public required ResolutionStatus Status { get; init; }

    /// <summary>The handler's error text, or the dead-letter description.</summary>
    public string? ErrorText { get; init; }

    /// <summary>When the Resolver recorded the outcome.</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }
}
