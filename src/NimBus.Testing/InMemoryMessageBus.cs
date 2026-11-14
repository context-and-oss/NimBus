using NimBus.Core.Messages;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

public class InMemoryMessageBus : ISender
{
    private readonly ConcurrentQueue<IMessage> _pending = new();
    private readonly List<IMessage> _allSent = new();
    private readonly ConcurrentDictionary<string, InMemorySessionState> _sessions = new();
    private readonly List<(long SequenceNumber, IMessage Message, DateTimeOffset ScheduledTime)> _scheduled = new();
    private readonly HashSet<long> _cancelledSchedules = new();
    private long _nextSequenceNumber;
    private readonly object _lock = new();

    public IReadOnlyList<IMessage> SentMessages
    {
        get { lock (_lock) { return _allSent.ToList(); } }
    }

    public int PendingCount => _pending.Count;

    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        lock (_lock) { _allSent.Add(message); }
        _pending.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            lock (_lock) { _allSent.Add(message); }
            _pending.Enqueue(message);
        }
        return Task.CompletedTask;
    }

    public async Task DeliverAll(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        while (_pending.TryDequeue(out var message))
        {
            var sessionKey = message.SessionId ?? "__no_session__";
            var sessionState = _sessions.GetOrAdd(sessionKey, _ => new InMemorySessionState());
            var context = new InMemoryMessageContext(message, sessionState);

            await messageHandler.Handle(context, cancellationToken);
        }
    }

    public async Task<List<InMemoryDeliveryResult>> DeliverAllWithResults(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        var results = new List<InMemoryDeliveryResult>();

        while (_pending.TryDequeue(out var message))
        {
            var sessionKey = message.SessionId ?? "__no_session__";
            var sessionState = _sessions.GetOrAdd(sessionKey, _ => new InMemorySessionState());
            var context = new InMemoryMessageContext(message, sessionState);
            Exception? caughtException = null;

            try
            {
                await messageHandler.Handle(context, cancellationToken);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            results.Add(new InMemoryDeliveryResult(message, context, caughtException));
        }

        return results;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            var seq = Interlocked.Increment(ref _nextSequenceNumber);
            _scheduled.Add((seq, message, scheduledEnqueueTime));
            _allSent.Add(message);
            return Task.FromResult(seq);
        }
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _cancelledSchedules.Add(sequenceNumber);
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<(long SequenceNumber, IMessage Message, DateTimeOffset ScheduledTime)> ScheduledMessages
    {
        get { lock (_lock) { return _scheduled.Where(s => !_cancelledSchedules.Contains(s.SequenceNumber)).ToList(); } }
    }

    public void Clear()
    {
        while (_pending.TryDequeue(out _)) { }
        lock (_lock)
        {
            _allSent.Clear();
            _scheduled.Clear();
            _cancelledSchedules.Clear();
        }
        _sessions.Clear();
    }
}
