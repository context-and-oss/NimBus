using NimBus.Core.Messages;
using NimBus.ServiceBus;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// In-memory IServiceBusSession that records Complete/DeadLetter/Abandon/Defer calls
/// and provides session state management without Azure Service Bus. Like the Service Bus
/// SDK, every call made with a cancelled token fails without effect.
/// </summary>
internal sealed class FakeServiceBusSession : IServiceBusSession
{
    private SessionState _sessionState = new();

    public int CompletedCount { get; private set; }
    public int DeadLetteredCount { get; private set; }
    public int DeferredCount { get; private set; }

    public string? LastDeadLetterReason { get; private set; }
    public string? LastDeadLetterDescription { get; private set; }

    public bool WasCompleted => CompletedCount > 0;
    public bool WasDeadLettered => DeadLetteredCount > 0;
    public bool WasDeferred => DeferredCount > 0;

    /// <summary>The session state as last written.</summary>
    public SessionState State => _sessionState;

    /// <summary>Runs after every session-state write, e.g. to stop the processor at that point.</summary>
    public Action? OnStateWritten { get; set; }

    /// <summary>Thrown by the next completion instead of completing, e.g. a lost session lock.</summary>
    public Exception? NextCompleteException { get; set; }

    public Task CompleteAsync(IServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (NextCompleteException is { } exception)
        {
            NextCompleteException = null;
            throw exception;
        }

        CompletedCount++;
        return Task.CompletedTask;
    }

    public Task DeadLetterAsync(IServiceBusMessage message, string reason, string? errorDescription, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeadLetteredCount++;
        LastDeadLetterReason = reason;
        LastDeadLetterDescription = errorDescription;
        return Task.CompletedTask;
    }

    public Task SetStateAsync(SessionState sessionState, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sessionState = sessionState;
        OnStateWritten?.Invoke();
        return Task.CompletedTask;
    }

    public Task<SessionState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_sessionState);
    }

    public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
