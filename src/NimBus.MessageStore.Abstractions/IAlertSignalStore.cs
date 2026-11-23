using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.Core.Messages;
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

    /// <summary>
    /// Per-minute counts of the endpoint's stored messages of one type with <c>EnqueuedTimeUtc</c> in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), for a rule preview's replay (Spec 053
    /// Phase 3). Minutes with no message are left out; the result is ordered by minute.
    /// </summary>
    /// <param name="endpointId">The endpoint (exact match).</param>
    /// <param name="type">The message type to count, for example <see cref="MessageType.ErrorResponse"/>.</param>
    /// <param name="fromUtc">The start, inclusive; must be a whole minute.</param>
    /// <param name="toUtc">The end, exclusive; must be a whole minute after <paramref name="fromUtc"/>.</param>
    /// <param name="eventTypes">Event types to count; null or empty counts every type.</param>
    /// <param name="errorTextContains">
    /// A case-insensitive substring of the error text, with the same meaning as in
    /// <see cref="CountFailedAttempts"/>, or null.
    /// </param>
    Task<IReadOnlyList<MinuteCount>> CountMessagesPerMinute(string endpointId, MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains);

    /// <summary>
    /// The endpoint's failed attempts — the same messages <see cref="CountFailedAttempts"/> counts — in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), newest first, at most
    /// <paramref name="limit"/>: their message and event ids, to look up their classifications.
    /// </summary>
    /// <param name="endpointId">The endpoint (exact match).</param>
    /// <param name="fromUtc">The window's start, inclusive.</param>
    /// <param name="toUtc">The window's end, exclusive.</param>
    /// <param name="eventTypes">Event types to list; null or empty lists every type.</param>
    /// <param name="errorTextContains">A case-insensitive substring of the error text, or null.</param>
    /// <param name="limit">The most attempts to return (at least 1).</param>
    /// <returns>The attempts, and whether more matched than <paramref name="limit"/>.</returns>
    Task<FailedAttemptPage> ListFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int limit);
}

/// <summary>An event that holds its session blocked, as <see cref="IAlertSignalStore.GetOldestBlockingEvent"/> returns it.</summary>
/// <param name="EventId">The event.</param>
/// <param name="SessionId">The session it holds.</param>
/// <param name="Status">Failed, or Pending (with the Handoff sub-status).</param>
/// <param name="UpdatedAtUtc">When the event last changed.</param>
public sealed record BlockingEvent(string EventId, string SessionId, ResolutionStatus Status, DateTime UpdatedAtUtc);

/// <summary>The number of messages in one minute, as <see cref="IAlertSignalStore.CountMessagesPerMinute"/> returns it.</summary>
/// <param name="MinuteUtc">The minute's start (UTC, seconds zero).</param>
/// <param name="Count">The messages enqueued in that minute.</param>
public sealed record MinuteCount(DateTime MinuteUtc, int Count);

/// <summary>One failed attempt, as <see cref="IAlertSignalStore.ListFailedAttempts"/> returns it.</summary>
/// <param name="MessageId">The failed attempt's message id: the key its classification is stored under.</param>
/// <param name="EventId">The event the attempt belongs to.</param>
/// <param name="EnqueuedTimeUtc">When the attempt's message was enqueued.</param>
public sealed record FailedAttempt(string MessageId, string EventId, DateTime EnqueuedTimeUtc);

/// <summary>A page of <see cref="FailedAttempt"/>s, newest first.</summary>
/// <param name="Items">The attempts, at most the requested limit.</param>
/// <param name="Truncated">Whether more attempts matched than the limit.</param>
public sealed record FailedAttemptPage(IReadOnlyList<FailedAttempt> Items, bool Truncated);
