using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Diagnostics;
using NimBus.Core.Inbox;

namespace NimBus.SDK.Hosting;

/// <summary>
/// Periodically removes inbox records that are older than the configured retention period.
/// </summary>
public sealed partial class InboxPurgeHostedService : BackgroundService
{
    private const string PurgeOperation = "purge";
    private readonly IInboxStore _inboxStore;
    private readonly string _endpointId;
    private readonly InboxOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InboxPurgeHostedService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InboxPurgeHostedService"/> class.
    /// </summary>
    /// <param name="inboxStore">The selected inbox-store provider.</param>
    /// <param name="endpointId">
    /// The subscriber endpoint whose records this host purges. Retention is configured per
    /// subscriber, so cleanup is scoped to that endpoint — purging a shared store globally
    /// would let a short-retention subscriber delete another endpoint's still-valid records.
    /// </param>
    /// <param name="options">The inbox retention and cleanup options.</param>
    /// <param name="timeProvider">The clock and timer provider.</param>
    /// <param name="logger">The optional structured logger.</param>
    public InboxPurgeHostedService(
        IInboxStore inboxStore,
        string endpointId,
        InboxOptions options,
        TimeProvider timeProvider,
        ILogger<InboxPurgeHostedService>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        _inboxStore = inboxStore ?? throw new ArgumentNullException(nameof(inboxStore));
        _endpointId = endpointId;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? NullLogger<InboxPurgeHostedService>.Instance;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_options.CleanupInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var cutoff = _timeProvider.GetUtcNow() - _options.RetentionPeriod;
                    var purged = await _inboxStore.PurgeExpiredAsync(_endpointId, cutoff, stoppingToken);
                    LogInboxCleanupPurgedExpiredRecords(_logger, purged, _endpointId);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    NimBusMeters.InboxOperationFailed.Add(
                        1,
                        new KeyValuePair<string, object?>(
                            MessagingAttributes.NimBusStoreOperation,
                            PurgeOperation));
                    LogInboxCleanupFailed(_logger, exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Cooperative host shutdown.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Inbox cleanup purged {PurgedRecordCount} expired records. EndpointId:{EndpointId}")]
    private static partial void LogInboxCleanupPurgedExpiredRecords(ILogger logger, int purgedRecordCount, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Inbox cleanup failed with {ExceptionType}; cleanup will retry on the next tick.")]
    private static partial void LogInboxCleanupFailed(ILogger logger, string exceptionType);
}
