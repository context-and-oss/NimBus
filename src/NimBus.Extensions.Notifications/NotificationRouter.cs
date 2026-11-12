using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Default <see cref="INotificationRouter"/>. For each notification it (1) filters channels by
/// <see cref="NotificationChannelOptions.MinSeverity"/>, short-circuiting when none qualify;
/// (2) drops duplicates of a recently-seen <c>(EventId, Severity)</c>; (3) applies an optional
/// token-bucket rate limit, accumulating suppressed counts into a single storm-summary
/// notification emitted on the next available token; and (4) delivers to each eligible channel,
/// isolating channel failures so they never affect message processing.
/// </summary>
public partial class NotificationRouter : INotificationRouter
{
    private readonly List<ChannelRegistration> _registrations;
    private readonly NotificationRouterOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TokenBucket? _bucket;

    private readonly object _dedupGate = new();
    private readonly Dictionary<(string Id, NotificationSeverity Severity), DateTimeOffset> _dedup = new();

    private readonly object _suppressGate = new();
    private readonly Dictionary<NotificationSeverity, int> _suppressedBySeverity = new();
    private int _suppressedTotal;

    public NotificationRouter(
        IEnumerable<ChannelRegistration> registrations,
        NotificationRouterOptions options,
        ILogger<NotificationRouter> logger)
        : this(registrations, options, logger, TimeProvider.System)
    {
    }

    public NotificationRouter(
        IEnumerable<ChannelRegistration> registrations,
        NotificationRouterOptions options,
        ILogger<NotificationRouter> logger,
        TimeProvider timeProvider)
    {
        _registrations = (registrations ?? throw new ArgumentNullException(nameof(registrations))).ToList();
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<NotificationRouter>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _bucket = _options.RateLimitEnabled
            ? new TokenBucket(_options.MaxPerMinute.GetValueOrDefault(), _options.BurstCapacity.GetValueOrDefault(), _timeProvider)
            : null;
    }

    public async Task RouteAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (notification == null)
        {
            return;
        }

        var eligible = EligibleChannels(notification);
        if (eligible.Count == 0)
        {
            // No channel cares about this severity — short-circuit before dedup/rate limit.
            return;
        }

        if (!PassesDedup(notification))
        {
            return;
        }

        if (_bucket != null)
        {
            if (!_bucket.TryAcquire())
            {
                RecordSuppressed(notification.Severity);
                return;
            }

            // A token was available again — surface any accumulated storm as one summary first.
            await EmitStormSummaryIfPending(cancellationToken).ConfigureAwait(false);
        }

        await DeliverAsync(notification, eligible, cancellationToken).ConfigureAwait(false);
    }

    private List<ChannelRegistration> EligibleChannels(Notification notification) =>
        _registrations.Where(r => notification.Severity >= r.Options.MinSeverity).ToList();

    private bool PassesDedup(Notification notification)
    {
        var id = !string.IsNullOrEmpty(notification.EventId)
            ? notification.EventId
            : notification.MessageId;

        if (string.IsNullOrEmpty(id))
        {
            // Nothing to key on — never dedup; always deliver.
            return true;
        }

        var key = (id, notification.Severity);
        var now = _timeProvider.GetUtcNow();

        lock (_dedupGate)
        {
            EvictStale(now);
            if (_dedup.TryGetValue(key, out var seenAt) && (now - seenAt) < _options.DedupWindow)
            {
                return false;
            }

            _dedup[key] = now;
            return true;
        }
    }

    private void EvictStale(DateTimeOffset now)
    {
        if (_dedup.Count == 0)
        {
            return;
        }

        var stale = _dedup.Where(kv => (now - kv.Value) >= _options.DedupWindow).Select(kv => kv.Key).ToList();
        foreach (var key in stale)
        {
            _dedup.Remove(key);
        }
    }

    private void RecordSuppressed(NotificationSeverity severity)
    {
        lock (_suppressGate)
        {
            _suppressedBySeverity.TryGetValue(severity, out var count);
            _suppressedBySeverity[severity] = count + 1;
            _suppressedTotal++;
        }
    }

    private async Task EmitStormSummaryIfPending(CancellationToken cancellationToken)
    {
        var summary = BuildAndResetSummary();
        if (summary == null)
        {
            return;
        }

        var eligible = EligibleChannels(summary);
        if (eligible.Count == 0 || !PassesDedup(summary))
        {
            return;
        }

        await DeliverAsync(summary, eligible, cancellationToken).ConfigureAwait(false);
    }

    private Notification? BuildAndResetSummary()
    {
        lock (_suppressGate)
        {
            if (_suppressedTotal == 0)
            {
                return null;
            }

            var total = _suppressedTotal;
            var maxSeverity = _suppressedBySeverity.Keys.Max();
            var breakdown = string.Join(
                ", ",
                _suppressedBySeverity.OrderByDescending(kv => kv.Key).Select(kv => $"{kv.Value} {kv.Key}"));

            _suppressedBySeverity.Clear();
            _suppressedTotal = 0;

            return new Notification
            {
                Severity = maxSeverity,
                Title = $"{total} notifications suppressed",
                Message = $"Notification rate limit exceeded; {total} notification(s) were suppressed " +
                          $"to prevent a storm. Suppressed by severity: {breakdown}.",
            };
        }
    }

    private async Task DeliverAsync(Notification notification, List<ChannelRegistration> eligible, CancellationToken cancellationToken)
    {
        foreach (var registration in eligible)
        {
            try
            {
                await registration.Channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Isolate channel failures: a failing channel must not affect sibling channels
                // or message processing.
                LogNotificationChannelFailedToDeliver(_logger, ex, registration.Channel.GetType().Name, notification.Severity);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification channel {Channel} failed to deliver a {Severity} notification.")]
    private static partial void LogNotificationChannelFailedToDeliver(ILogger logger, Exception exception, string channel, NotificationSeverity severity);
}
