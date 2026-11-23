using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// The narrow, capped reads alert rule conditions need beyond the failure backlog (Spec 053
/// Phase 2): failed attempts in a window, the oldest event blocking a session, and whether an
/// endpoint completed anything recently. A separate optional capability (ADR-018): without it the
/// WebApp offers only the failure-backlog condition.
/// </summary>
/// <remarks>
/// Every read is for one endpoint, matched exactly (never as a prefix), and bounded: the evaluator
/// calls these once a minute per rule. A null or empty endpoint id throws <see cref="ArgumentException"/>.
/// </remarks>
public interface IAlertSignalStore
{
    /// <summary>
    /// Counts the endpoint's failed attempts — stored <see cref="NimBus.Core.Messages.MessageType.ErrorResponse"/>
    /// messages, which include dead-letters — whose <c>EnqueuedTimeUtc</c> is in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), counting at most <paramref name="cap"/>.
    /// </summary>
    /// <param name="endpointId">The endpoint (exact match).</param>
    /// <param name="fromUtc">The window's start, inclusive.</param>
    /// <param name="toUtc">The window's end, exclusive.</param>
    /// <param name="eventTypes">Event types to count; null or empty counts every type.</param>
    /// <param name="errorTextContains">
    /// A case-insensitive substring of the error text, or null. The error text is
    /// <c>MessageContent.ErrorContent.ErrorText</c>, else <c>DeadLetterErrorDescription</c>.
    /// </param>
    /// <param name="cap">The most attempts to count (at least 1).</param>
    /// <returns>The count, at most <paramref name="cap"/>.</returns>
    Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap);

    /// <summary>
    /// The endpoint's oldest event, by <c>UpdatedAt</c>, that holds its session: a non-deleted
    /// event that is Failed, or Pending with the <c>Handoff</c> sub-status. DeadLettered events are
    /// left out: they don't block their session.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <returns>The event, or null when nothing blocks a session.</returns>
    Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId);

    /// <summary>
    /// Whether the endpoint stored any <see cref="NimBus.Core.Messages.MessageType.ResolutionResponse"/>
    /// (a successfully completed message) with <c>EnqueuedTimeUtc</c> at or after <paramref name="sinceUtc"/>.
    /// </summary>
    /// <param name="endpointId">The endpoint (exact match).</param>
    /// <param name="sinceUtc">The earliest time that counts.</param>
    Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc);
}

/// <summary>An event that holds its session blocked, as <see cref="IAlertSignalStore.GetOldestBlockingEvent"/> returns it.</summary>
/// <param name="EventId">The event.</param>
/// <param name="SessionId">The session it holds.</param>
/// <param name="Status">Failed, or Pending (with the Handoff sub-status).</param>
/// <param name="UpdatedAtUtc">When the event last changed.</param>
public sealed record BlockingEvent(string EventId, string SessionId, ResolutionStatus Status, DateTime UpdatedAtUtc);
