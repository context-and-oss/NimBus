using System.Collections.Concurrent;

namespace CrmErpDemo.Contracts.E2E;

/// <summary>A bounded fault script for one test-owned session and event type.</summary>
public sealed record E2eScript(string EventType, string Stage, string[] Actions);

/// <summary>Evidence captured at the real handler or middleware boundary.</summary>
public sealed record E2eAttempt(string Action, string MessageId, string EventId, string? OriginatingMessageId, DateTimeOffset At);

/// <summary>Serializes scripted fault consumption without affecting unregistered sessions.</summary>
public sealed class E2eRegistry
{
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal)
    {
        "continue", "fail", "retry", "transient", "permanent", "discard",
        "pending", "pending-twice", "pending-throw", "validation",
    };
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    /// <summary>Installs the next script, retaining evidence from previous attempts.</summary>
    public void Configure(Guid id, E2eScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(script.EventType) || script.EventType.Length > 150 ||
            script.Stage is not ("handler" or "middleware") || script.Actions is null || script.Actions.Length > 100 ||
            script.Actions.Any(action => !Actions.Contains(action)))
        {
            throw new ArgumentException("Invalid E2E session script.", nameof(script));
        }
        if (_sessions.Count >= 1000 && !_sessions.ContainsKey(id))
        {
            throw new ArgumentException("Remove completed E2E sessions before registering more.", nameof(id));
        }
        var session = _sessions.GetOrAdd(id, static _ => new Session());
        lock (session)
        {
            session.Script = script with { Actions = (string[])script.Actions.Clone() };
            session.Position = 0;
        }
    }

    /// <summary>Consumes one action only when both event type and execution stage match.</summary>
    public E2eAttempt Attempt(Guid id, string eventType, string stage, string messageId, string eventId, string? origin)
    {
        var result = new E2eAttempt("continue", messageId, eventId, origin, DateTimeOffset.UtcNow);
        if (!_sessions.TryGetValue(id, out var session)) return result;
        lock (session)
        {
            if (session.Script.EventType != eventType || session.Script.Stage != stage) return result;
            var action = session.Position < session.Script.Actions.Length
                ? session.Script.Actions[session.Position++] : "continue";
            result = result with { Action = action };
            if (session.Attempts.Count >= 1000) throw new InvalidOperationException("E2E attempt limit exceeded.");
            session.Attempts.Add(result);
            return result;
        }
    }

    /// <summary>Returns immutable evidence for one session.</summary>
    public IReadOnlyList<E2eAttempt> Snapshot(Guid id)
    {
        if (!_sessions.TryGetValue(id, out var session)) return [];
        lock (session) return session.Attempts.ToArray();
    }

    /// <summary>Removes only the specified test session.</summary>
    public void Remove(Guid id) => _sessions.TryRemove(id, out _);

    private sealed class Session
    {
        public E2eScript Script { get; set; } = new("", "handler", []);
        public int Position { get; set; }
        public List<E2eAttempt> Attempts { get; } = [];
    }
}
