using System;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

/// <summary>
/// Internal helpers that translate Cosmos-specific exceptions into provider-neutral
/// equivalents at the message store boundary. Consumers see only the abstractions
/// surface — they should never need to <c>using Microsoft.Azure.Cosmos;</c>.
/// </summary>
internal static partial class CosmosExceptionTranslation
{
    private const string TransientFailureMessage = "Cosmos DB message store is temporarily unavailable.";

    internal static bool IsTransient(CosmosException exception) => exception.StatusCode switch
    {
        HttpStatusCode.RequestTimeout => true,
        HttpStatusCode.Gone => true,
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.InternalServerError => true,
        HttpStatusCode.BadGateway => true,
        HttpStatusCode.ServiceUnavailable => true,
        HttpStatusCode.GatewayTimeout => true,
        (HttpStatusCode)449 => true, // Cosmos-specific Retry With response.
        _ => false,
    };

    internal static void ThrowIfTransient(
        CosmosException exception,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        logger ??= NullLogger.Instance;
        if (!IsTransient(exception))
        {
            return;
        }

        var retryAfter = exception.RetryAfter > TimeSpan.Zero
            ? exception.RetryAfter
            : null;

        // Log only deliberately selected diagnostic fields while the native
        // exception is still available. Cosmos exception messages and metadata
        // can contain account hosts, database names, activity IDs, or payload
        // details and therefore must not be attached to this warning.
        LogCosmosTransientOperation(logger, operation, (int)exception.StatusCode, retryAfter);

        if (exception.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new RequestLimitException(TransientFailureMessage, retryAfter);
        }

        // Do not attach the native exception: provider messages can contain
        // account, host, or database details that must not cross this boundary.
        throw new StorageProviderTransientException(TransientFailureMessage, retryAfter);
    }

    internal static T TranslateTransient<T>(
        Func<T> action,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        try
        {
            return action();
        }
        catch (CosmosException ex)
        {
            ThrowIfTransient(ex, logger, operation);
            throw;
        }
    }

    public static async Task<T> TranslateTransientAsync<T>(
        Func<Task<T>> action,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            ThrowIfTransient(ex, logger, operation);
            throw;
        }
    }

    public static async Task TranslateTransientAsync(
        Func<Task> action,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (CosmosException ex)
        {
            ThrowIfTransient(ex, logger, operation);
            throw;
        }
    }

    internal static FeedIterator<T> Wrap<T>(
        FeedIterator<T> iterator,
        ILogger? logger = null,
        [CallerMemberName] string operation = "") =>
        new TransientTranslatingFeedIterator<T>(iterator, logger, operation);

    private sealed class TransientTranslatingFeedIterator<T> : FeedIterator<T>
    {
        private readonly FeedIterator<T> _inner;
        private readonly ILogger? _logger;
        private readonly string _operation;

        public TransientTranslatingFeedIterator(FeedIterator<T> inner, ILogger? logger, string operation)
        {
            _inner = inner;
            _logger = logger;
            _operation = operation;
        }

        public override bool HasMoreResults => _inner.HasMoreResults;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default) =>
            TranslateTransientAsync(() => _inner.ReadNextAsync(cancellationToken), _logger, _operation);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    public static async Task<T> TranslateAsync<T>(
        Func<Task<T>> action,
        string? endpointId = null,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointNotFoundException(endpointId ?? "(unknown)", ex);
        }
        catch (CosmosException ex) when (IsTransient(ex))
        {
            ThrowIfTransient(ex, logger, operation);
            throw;
        }
    }

    public static async Task TranslateAsync(
        Func<Task> action,
        string? endpointId = null,
        ILogger? logger = null,
        [CallerMemberName] string operation = "")
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            throw new EndpointNotFoundException(endpointId ?? "(unknown)", ex);
        }
        catch (CosmosException ex) when (IsTransient(ex))
        {
            ThrowIfTransient(ex, logger, operation);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "COSMOS TRANSIENT: Operation: {Operation}, HttpStatusCode: {StatusCode}, RetryAfter: {RetryAfter}")]
    private static partial void LogCosmosTransientOperation(ILogger logger, string operation, int statusCode, TimeSpan? retryAfter);
}
