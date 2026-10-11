using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Which operator actions the audit log records. Settings → Audit logging edits the selection;
/// <see cref="AuditLogService"/> consults it before every write.
/// </summary>
public interface IAuditSettingsProvider
{
    /// <summary>
    /// True when rows of <paramref name="type"/> are recorded. Always true for
    /// <see cref="AuditSettingsProvider.AlwaysRecorded"/> types, and true when the
    /// selection cannot be read — auditing fails open.
    /// </summary>
    Task<bool> IsRecordedAsync(MessageAuditType type);

    /// <summary>The disabled audit types, read through the cache.</summary>
    Task<IReadOnlySet<MessageAuditType>> GetDisabledAsync();

    /// <summary>
    /// Stores <paramref name="disabled"/> as the new selection and refreshes this
    /// instance's cache. Types outside <see cref="AuditSettingsProvider.Configurable"/>
    /// are rejected with <see cref="ArgumentException"/>.
    /// </summary>
    Task<IReadOnlySet<MessageAuditType>> SaveAsync(IEnumerable<MessageAuditType> disabled);
}

/// <summary>
/// Singleton cache over <see cref="IEndpointMetadataStore.GetAuditSettings"/>. The audit
/// writer runs on hot paths (<see cref="MessageAuditType.SearchEvents"/> fires on every
/// list refresh), so the selection is re-read at most every <see cref="SuccessTtl"/>;
/// a save on this instance takes effect immediately and other instances converge
/// within the TTL.
/// </summary>
public sealed class AuditSettingsProvider : IAuditSettingsProvider
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Types recorded regardless of the selection: turning auditing down must itself
    /// leave a trace, and operator commands that change a message (Spec 035 Phase 2a)
    /// run only once their audit row exists.
    /// </summary>
    public static readonly IReadOnlySet<MessageAuditType> AlwaysRecorded = new HashSet<MessageAuditType>
    {
        MessageAuditType.UpdateAuditSettings,
        MessageAuditType.Resubmit,
        MessageAuditType.ResubmitWithChanges,
        MessageAuditType.Skip,
        MessageAuditType.ReportEvent,
        MessageAuditType.CommandNotSent,
        MessageAuditType.UpdateMcpSettings,
        MessageAuditType.McpAccessRefused,
        MessageAuditType.AgentApiKeyCreated,
        MessageAuditType.AgentApiKeyRevoked,
        MessageAuditType.AgentApiKeyRemoved,
    };

    /// <summary>
    /// Types an operator may switch off: every type the WebApp audit writer records,
    /// minus <see cref="AlwaysRecorded"/>. <see cref="MessageAuditType.Comment"/> and
    /// <see cref="MessageAuditType.Retry"/> are event history written straight to the
    /// store (operator comments, Resolver retries), not access audit, so they are not
    /// offered.
    /// </summary>
    public static readonly IReadOnlyList<MessageAuditType> Configurable = Enum.GetValues<MessageAuditType>()
        .Where(t => t is not (MessageAuditType.Comment or MessageAuditType.Retry) && !AlwaysRecorded.Contains(t))
        .ToList();

    private static readonly IReadOnlySet<MessageAuditType> None = new HashSet<MessageAuditType>();

    private readonly IEndpointMetadataStore _store;
    private readonly ILogger<AuditSettingsProvider> _logger;
    private readonly Func<DateTime> _utcNow;
    private CacheEntry? _current;

    public AuditSettingsProvider(IEndpointMetadataStore store, ILogger<AuditSettingsProvider> logger)
        : this(store, logger, () => DateTime.UtcNow)
    {
    }

    internal AuditSettingsProvider(IEndpointMetadataStore store, ILogger<AuditSettingsProvider> logger, Func<DateTime> utcNow)
    {
        _store = store;
        _logger = logger;
        _utcNow = utcNow;
    }

    /// <inheritdoc/>
    public async Task<bool> IsRecordedAsync(MessageAuditType type)
        => AlwaysRecorded.Contains(type) || !(await GetDisabledAsync().ConfigureAwait(false)).Contains(type);

    /// <inheritdoc/>
    public async Task<IReadOnlySet<MessageAuditType>> GetDisabledAsync()
    {
        var current = Volatile.Read(ref _current);
        if (current != null && _utcNow() < current.ExpiresAtUtc)
            return current.Disabled;

        // No single-flight: a concurrent refresh costs one extra singleton read, and
        // the audit write it guards is best-effort anyway.
        try
        {
            var settings = await _store.GetAuditSettings().ConfigureAwait(false);
            var disabled = Parse(settings.DisabledAuditTypes);
            Volatile.Write(ref _current, new CacheEntry(disabled, _utcNow() + SuccessTtl));
            return disabled;
        }
        catch (Exception ex)
        {
            // Fail open: keep the last-known selection, or record everything when there
            // is none — a store outage must not silently stop auditing.
            _logger.LogWarning(ex, "Audit settings read failed; using the last-known selection");
            var fallback = current?.Disabled ?? None;
            Volatile.Write(ref _current, new CacheEntry(fallback, _utcNow() + FailureTtl));
            return fallback;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlySet<MessageAuditType>> SaveAsync(IEnumerable<MessageAuditType> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);
        var selection = disabled.ToHashSet();
        var rejected = selection.Where(t => !Configurable.Contains(t)).ToList();
        if (rejected.Count != 0)
            throw new ArgumentException($"Audit types cannot be disabled: {string.Join(", ", rejected)}", nameof(disabled));

        await _store.SetAuditSettings(new AuditSettings
        {
            DisabledAuditTypes = selection.OrderBy(t => t).Select(t => t.ToString()).ToList(),
        }).ConfigureAwait(false);

        IReadOnlySet<MessageAuditType> stored = selection;
        Volatile.Write(ref _current, new CacheEntry(stored, _utcNow() + SuccessTtl));
        return stored;
    }

    /// <summary>
    /// Parses an audit type by name, case-insensitively — the stored PascalCase name or the
    /// API's camelCase one. Numeric strings are rejected: <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/>
    /// would otherwise accept a position, which differs between enum versions.
    /// </summary>
    internal static bool TryParseName(string? name, out MessageAuditType type)
    {
        type = default;
        return !string.IsNullOrWhiteSpace(name)
            && char.IsLetter(name.TrimStart()[0])
            && Enum.TryParse(name, ignoreCase: true, out type)
            && Enum.IsDefined(type);
    }

    // Names that no longer parse (a type retired in a later major) are ignored rather
    // than failing every audit write.
    private static IReadOnlySet<MessageAuditType> Parse(IEnumerable<string>? names)
        => (names ?? Enumerable.Empty<string>())
            .Select(n => TryParseName(n, out var t) ? (MessageAuditType?)t : null)
            .Where(t => t.HasValue && !AlwaysRecorded.Contains(t.Value))
            .Select(t => t!.Value)
            .ToHashSet();

    private sealed record CacheEntry(IReadOnlySet<MessageAuditType> Disabled, DateTime ExpiresAtUtc);
}
