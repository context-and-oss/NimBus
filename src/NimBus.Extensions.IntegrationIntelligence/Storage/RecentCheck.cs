namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>
/// Runs a check at most once per lifetime. Callers that arrive while it runs, or within its
/// lifetime after it succeeded, share that one run. A failed run fails the callers awaiting it
/// and is never reused, so the next caller checks again.
/// </summary>
internal sealed class RecentCheck
{
    private readonly Func<Task> _check;
    private readonly TimeSpan _lifetime;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();
    private Task? _current;
    private long _startedAt;

    /// <summary>Creates the check. <paramref name="check"/> must not depend on one caller's cancellation.</summary>
    public RecentCheck(Func<Task> check, TimeSpan lifetime, TimeProvider clock)
    {
        _check = check;
        _lifetime = lifetime;
        _clock = clock;
    }

    /// <summary>Completes when a check that is still within its lifetime has succeeded; throws what it threw.</summary>
    public Task EnsureAsync()
    {
        lock (_gate)
        {
            if (_current is { IsFaulted: false, IsCanceled: false } && _clock.GetElapsedTime(_startedAt) < _lifetime)
                return _current;
            _startedAt = _clock.GetTimestamp();
            return _current = _check();
        }
    }
}
