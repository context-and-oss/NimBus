using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>
/// Reads and changes an endpoint's Service Bus session state for Operations → Session state and
/// Operations → Clean up session state. The state lives on the endpoint subscription
/// (<c>&lt;endpoint&gt;/&lt;endpoint&gt;</c>) and is only reachable under the session's lock.
/// </summary>
public interface ISessionStateService
{
    /// <summary>
    /// Reads the session's state, its queued messages and its store counts. Never writes.
    /// </summary>
    /// <param name="endpointId">The endpoint (topic and subscription name).</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="cancellationToken">Cancels the lookup.</param>
    /// <returns>The session state view; <see cref="SessionStateView.Locked"/> when a receiver holds the session.</returns>
    Task<SessionStateView> GetAsync(string endpointId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies <paramref name="request"/> under the session's lock when the stored state still
    /// has the expected version. <paramref name="beforeWrite"/> runs, under the lock, before the
    /// state is written; an exception from it (an audit that can't be recorded) leaves the state
    /// unchanged and propagates.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="request">The action, the expected version and, for replace, the new state.</param>
    /// <param name="beforeWrite">Records the change before it is written.</param>
    /// <param name="cancellationToken">Cancels the change.</param>
    /// <returns>What happened, with the state as it now stands.</returns>
    Task<SessionStateChangeOutcome> ChangeAsync(
        string endpointId,
        string sessionId,
        SessionStateChangeRequest request,
        Func<SessionStateWrite, Task> beforeWrite,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inspects the sessions of the endpoint's stored events and classifies each one that holds
    /// state as idle, stale-blocked or in use. Never writes.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="request">The time window, the session cap and whether stale blocks are candidates.</param>
    /// <param name="cancellationToken">Cancels the preview.</param>
    /// <returns>The classified sessions.</returns>
    Task<SessionCleanupPreview> PreviewCleanupAsync(string endpointId, SessionCleanupPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the state of each selected session that, re-checked under its lock, still has the
    /// previewed version and is still eligible. Others are skipped with a reason.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="request">The previewed sessions and versions to reset.</param>
    /// <param name="cancellationToken">Cancels the run between sessions.</param>
    /// <returns>How many sessions were reset, and why the others were skipped.</returns>
    Task<SessionCleanupResult> CleanupAsync(string endpointId, SessionCleanupRequest request, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of <see cref="ISessionStateService.ChangeAsync"/>.</summary>
public enum SessionStateChangeStatus
{
    /// <summary>The state was written.</summary>
    Changed,

    /// <summary>The stored state no longer has the expected version.</summary>
    Conflict,

    /// <summary>A receiver holds the session, so its state can't be changed now.</summary>
    Locked,

    /// <summary>The request is invalid for the stored state.</summary>
    Invalid,
}

/// <summary>The outcome of a session state change.</summary>
/// <param name="Status">What happened.</param>
/// <param name="View">The state as it now stands; null when the session couldn't be read.</param>
/// <param name="Errors">Why an invalid request was refused.</param>
public sealed record SessionStateChangeOutcome(
    SessionStateChangeStatus Status,
    SessionStateView? View,
    IReadOnlyList<string> Errors);

/// <summary>A session state write about to happen, for the audit record.</summary>
/// <param name="SessionId">The session.</param>
/// <param name="Action">The guided action or <c>replace</c>.</param>
/// <param name="Before">The stored state before the write; null when none was stored.</param>
/// <param name="After">The state being written; null when it clears the state.</param>
public sealed record SessionStateWrite(string SessionId, string Action, string? Before, string? After);
