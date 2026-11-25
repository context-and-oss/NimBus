using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NimBus.Adapter.Mcp;

/// <summary>Whether a call to the system succeeded.</summary>
public enum AdapterCallOutcome
{
    /// <summary>The system answered with success.</summary>
    Succeeded = 0,

    /// <summary>The system answered with an error, or could not be reached.</summary>
    Failed = 1,
}

/// <summary>The adapter's most recent call to its system for one record.</summary>
/// <param name="Entity">The entity, as given when the call was recorded.</param>
/// <param name="Key">The record's key.</param>
/// <param name="At">When the answer, or the failure, came.</param>
/// <param name="StatusCode">The HTTP status the system answered, or null when it gave none.</param>
/// <param name="Outcome">Whether the call succeeded.</param>
/// <param name="Message">What the system answered, such as the reason phrase or the error's kind.</param>
public sealed record AdapterCallRecord(string Entity, string Key, DateTimeOffset At, int? StatusCode, AdapterCallOutcome Outcome, string? Message);

/// <summary>
/// A bounded in-memory log of the adapter's most recent call to its system per (entity, key),
/// served by <c>adapter_get_last_call</c>. Fill it with
/// <see cref="Microsoft.Extensions.DependencyInjection.AdapterCallLogHttpClientBuilderExtensions.AddAdapterCallLog"/> on the adapter's
/// HTTP client, or with <see cref="Record"/> for any other client.
/// </summary>
/// <remarks>
/// The log is per instance: it lives in the process's memory, starts empty when the process starts,
/// and holds only the calls this instance made. With several instances, or after a restart or a
/// Functions scale-in, a caller may find no call for a key that another instance handled. Entity
/// names are matched ignoring case, keys exactly. The oldest key is dropped once
/// <see cref="Capacity"/> keys are held.
/// </remarks>
public sealed class AdapterCallLog
{
    /// <summary>How many keys the log keeps by default.</summary>
    public const int DefaultCapacity = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<(string Entity, string Key), LinkedListNode<AdapterCallRecord>> _byKey = new(KeyComparer.Instance);
    private readonly LinkedList<AdapterCallRecord> _byAge = new();
    private readonly TimeProvider _time;

    /// <summary>A log of <see cref="DefaultCapacity"/> keys on the system clock.</summary>
    public AdapterCallLog()
        : this(DefaultCapacity, TimeProvider.System)
    {
    }

    /// <summary>A log of <paramref name="capacity"/> keys.</summary>
    /// <param name="capacity">How many keys to keep; at least 1.</param>
    /// <param name="timeProvider">The clock calls are stamped with.</param>
    public AdapterCallLog(int capacity, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        ArgumentNullException.ThrowIfNull(timeProvider);
        Capacity = capacity;
        _time = timeProvider;
    }

    /// <summary>How many keys the log keeps.</summary>
    public int Capacity { get; }

    /// <summary>How many keys the log holds now.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _byKey.Count;
        }
    }

    /// <summary>Records a call's result, replacing any earlier call for the same key.</summary>
    /// <param name="entity">The entity the call was for.</param>
    /// <param name="key">The record's key.</param>
    /// <param name="statusCode">The HTTP status, or null.</param>
    /// <param name="outcome">Whether it succeeded.</param>
    /// <param name="message">What the system answered. Never a record value or a secret.</param>
    public void Record(string entity, string key, int? statusCode, AdapterCallOutcome outcome, string? message)
    {
        ArgumentException.ThrowIfNullOrEmpty(entity);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var call = new AdapterCallRecord(entity, key, _time.GetUtcNow(), statusCode, outcome, message);
        lock (_gate)
        {
            if (_byKey.Remove((entity, key), out var existing))
                _byAge.Remove(existing);

            _byKey[(entity, key)] = _byAge.AddLast(call);
            while (_byKey.Count > Capacity && _byAge.First is { } oldest)
            {
                _byAge.RemoveFirst();
                _byKey.Remove((oldest.Value.Entity, oldest.Value.Key));
            }
        }
    }

    /// <summary>The most recent call for a key, if this instance made one it still holds.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="key">The record's key.</param>
    /// <param name="call">The call, when found.</param>
    public bool TryGet(string entity, string key, [NotNullWhen(true)] out AdapterCallRecord? call)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            if (_byKey.TryGetValue((entity, key), out var node))
            {
                call = node.Value;
                return true;
            }
        }

        call = null;
        return false;
    }

    /// <summary>
    /// The one registration both <c>AddAdapterCallLog</c> and <c>AddAdapterMcp</c> make, so the
    /// configured capacity holds whichever of them runs first.
    /// </summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddOptions();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(sp => new AdapterCallLog(
            sp.GetRequiredService<IOptions<AdapterMcpOptions>>().Value.CallLogCapacity,
            sp.GetRequiredService<TimeProvider>()));
    }

    private sealed class KeyComparer : IEqualityComparer<(string Entity, string Key)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Entity, string Key) x, (string Entity, string Key) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Entity, y.Entity) && StringComparer.Ordinal.Equals(x.Key, y.Key);

        public int GetHashCode((string Entity, string Key) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Entity), StringComparer.Ordinal.GetHashCode(obj.Key));
    }
}
