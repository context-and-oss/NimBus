using Microsoft.Extensions.Options;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.RateLimiting;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// The MCP access policy this instance enforces (Spec 037), read from the shared record
/// through a short cache. Registered only when the deployment serves <c>/mcp</c>.
/// </summary>
public interface IMcpAccessPolicyProvider
{
    /// <summary>
    /// The last loaded policy without I/O, for synchronous callers such as rate-limit
    /// partitions. Null until the first successful read.
    /// </summary>
    McpAccessPolicy? Current { get; }

    /// <summary>
    /// The policy, re-read at most every 30 seconds after a success and every 5 seconds after
    /// a failure. A failed read keeps the last policy. Null until the first successful read:
    /// the endpoint then refuses every call rather than fall back to defaults that may be
    /// wider than the saved policy.
    /// </summary>
    Task<McpAccessPolicy?> GetAsync();

    /// <summary>Replaces this instance's policy at once, after a save through the Admin API.</summary>
    void Accept(McpAccessSettings settings);
}

/// <summary>Singleton cache over <see cref="IEndpointMetadataStore.GetMcpAccessSettings"/>.</summary>
public sealed partial class McpAccessPolicyProvider : IMcpAccessPolicyProvider, IDisposable
{
    /// <summary>How long a successful read is reused.</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before retrying a failed read.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    private readonly IEndpointMetadataStore _store;
    private readonly RateLimitOptions _rateLimits;
    private readonly ILogger<McpAccessPolicyProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry? _entry;

    /// <summary>Creates the provider.</summary>
    public McpAccessPolicyProvider(
        IEndpointMetadataStore store,
        IOptions<RateLimitOptions> rateLimits,
        ILogger<McpAccessPolicyProvider> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(rateLimits);
        _store = store;
        _rateLimits = rateLimits.Value;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc/>
    public McpAccessPolicy? Current => Volatile.Read(ref _entry)?.Policy;

    /// <inheritdoc/>
    public async Task<McpAccessPolicy?> GetAsync()
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
            return entry.Policy;

        // Single-flight: every /mcp request passes here, so one read refreshes for all.
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            entry = Volatile.Read(ref _entry);
            if (entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
                return entry.Policy;

            try
            {
                var settings = await _store.GetMcpAccessSettings().ConfigureAwait(false);
                var policy = McpAccessPolicy.From(settings, _rateLimits);
                Volatile.Write(ref _entry, new Entry(policy, _time.GetUtcNow() + SuccessTtl));
                return policy;
            }
            catch (Exception ex)
            {
                // Fail closed: keep the last policy, or none at all. Never the defaults.
                LogMcpAccessPolicyReadFailed(_logger, ex);
                Volatile.Write(ref _entry, new Entry(entry?.Policy, _time.GetUtcNow() + FailureTtl));
                return entry?.Policy;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <inheritdoc/>
    public void Accept(McpAccessSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _entry, new Entry(McpAccessPolicy.From(settings, _rateLimits), _time.GetUtcNow() + SuccessTtl));
    }

    /// <inheritdoc/>
    public void Dispose() => _refresh.Dispose();

    private sealed record Entry(McpAccessPolicy? Policy, DateTimeOffset ExpiresAt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP access policy read failed; keeping the last loaded policy")]
    private static partial void LogMcpAccessPolicyReadFailed(ILogger logger, Exception exception);
}
