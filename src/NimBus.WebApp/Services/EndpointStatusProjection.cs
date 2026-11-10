using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.ManagementApi;
using StoredEndpointMetadata = NimBus.MessageStore.States.EndpointMetadata;

namespace NimBus.WebApp.Services;

/// <summary>
/// Builds the per-endpoint <see cref="EndpointStatusCount"/> shared by the endpoint status API and
/// the Monitor feed: the cached state count, or a "storage unavailable" stub when the endpoint's
/// container or table is missing, plus the subscription status known from endpoint metadata.
/// </summary>
internal static partial class EndpointStatusProjection
{
    private const int SqlInvalidObjectNameErrorNumber = 208;

    /// <summary>
    /// The endpoint's status count through the shared 5-second cache, or a stub marked
    /// <c>storageStatus = "unavailable"</c> when its storage is missing. Other failures propagate.
    /// </summary>
    public static async Task<EndpointStatusCount> ReadOrStubAsync(
        IStoreResultCache cache, IMessageTrackingStore store, ILogger logger, string endpointId)
    {
        try
        {
            // Cached below any per-user filtering: the value is the endpoint-scoped raw count,
            // identical for every authorized caller. Failures propagate uncached.
            var endpointStateCount = await cache.GetEndpointStateCountAsync(store, endpointId).ConfigureAwait(false);
            return Mapper.EndpointStatusCountFromEndpointStateCount(endpointStateCount);
        }
        catch (Exception ex) when (IsMissingStorage(ex))
        {
            LogStorageMissing(logger, ex, endpointId, ex.GetType().Name);
            return StorageUnavailableStub(endpointId);
        }
    }

    /// <summary>
    /// True when <paramref name="exception"/> means the endpoint's container or table does not
    /// exist (Cosmos 404, <see cref="EndpointNotFoundException"/>, SQL error 208).
    /// </summary>
    public static bool IsMissingStorage(Exception exception) => exception switch
    {
        CosmosException cosmos => cosmos.StatusCode == HttpStatusCode.NotFound,
        EndpointNotFoundException => true,
        SqlException sql => sql.Number == SqlInvalidObjectNameErrorNumber,
        _ => false,
    };

    /// <summary>The count reported for an endpoint whose storage is missing.</summary>
    public static EndpointStatusCount StorageUnavailableStub(string endpointId) => new()
    {
        EndpointId = endpointId,
        StorageStatus = "unavailable",
    };

    /// <summary>
    /// Copies the subscription status that <paramref name="metadata"/> knows onto
    /// <paramref name="counts"/>. A null flag means "unknown", not "missing", so it is left unset.
    /// </summary>
    public static void ApplySubscriptionStatus(
        IEnumerable<EndpointStatusCount> counts, IReadOnlyCollection<StoredEndpointMetadata> metadata)
    {
        foreach (var count in counts)
        {
            var match = metadata.FirstOrDefault(m =>
                m.EndpointId != null && m.EndpointId.Equals(count.EndpointId, StringComparison.OrdinalIgnoreCase));

            // SetSubscriptionStatusMetadata leaves the flag null both when the admin probe fails
            // and when the subscription is genuinely absent, so only a known status is reported.
            if (match?.SubscriptionStatus != null)
            {
                count.SubscriptionStatus = Mapper.SubscriptionStatusFromMetadata(match);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Storage missing for endpoint {EndpointId} ({Cause})")]
    private static partial void LogStorageMissing(ILogger logger, Exception exception, string endpointId, string cause);
}
