using NimBus.Adapter.Mcp;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Records an adapter HTTP client's calls in the <see cref="AdapterCallLog"/>.</summary>
public static class AdapterCallLogHttpClientBuilderExtensions
{
    /// <summary>
    /// Records the result of every call this client makes for <paramref name="entity"/> in the
    /// shared <see cref="AdapterCallLog"/>: the status code and reason phrase of an answer, or the
    /// exception's type when there was none. The exception still reaches the caller. The log is per
    /// instance; see <see cref="AdapterCallLog"/>.
    /// </summary>
    /// <param name="builder">The adapter's HTTP client.</param>
    /// <param name="entity">The entity the client's calls are for, as listed in <see cref="IAdapterSystem.Entities"/>.</param>
    /// <param name="keyFromRequest">The record's key in a request, or null for a request that is about no record.</param>
    public static IHttpClientBuilder AddAdapterCallLog(this IHttpClientBuilder builder, string entity, Func<HttpRequestMessage, string?> keyFromRequest)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(keyFromRequest);

        AdapterCallLog.Register(builder.Services);
        return builder.AddHttpMessageHandler(sp => new AdapterCallLogHandler(sp.GetRequiredService<AdapterCallLog>(), entity, keyFromRequest));
    }
}

/// <summary>Records each call's result; see <see cref="AdapterCallLogHttpClientBuilderExtensions.AddAdapterCallLog"/>.</summary>
internal sealed class AdapterCallLogHandler(AdapterCallLog log, string entity, Func<HttpRequestMessage, string?> keyFromRequest) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = keyFromRequest(request);
        if (string.IsNullOrEmpty(key))
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        HttpResponseMessage response;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The type and, for HTTP, the error's kind: an exception's message can carry hosts,
            // credentials or record values, and the call log is served to callers.
            var message = exception is HttpRequestException { HttpRequestError: not HttpRequestError.Unknown } http
                ? $"{nameof(HttpRequestException)}: {http.HttpRequestError}"
                : exception.GetType().Name;
            log.Record(entity, key, (int?)(exception as HttpRequestException)?.StatusCode, AdapterCallOutcome.Failed, message);
            throw;
        }

        log.Record(
            entity,
            key,
            (int)response.StatusCode,
            response.IsSuccessStatusCode ? AdapterCallOutcome.Succeeded : AdapterCallOutcome.Failed,
            response.ReasonPhrase);
        return response;
    }
}
