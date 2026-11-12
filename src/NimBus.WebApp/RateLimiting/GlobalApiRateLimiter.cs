using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;

namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// The global <c>/api</c> limiter, wrapped so a rejection can name it. The rate-limiting middleware
/// tells <c>OnRejected</c> only that some limiter refused; on a route that also carries a named
/// policy, the endpoint metadata alone would blame that policy for the global limit.
/// </summary>
internal sealed class GlobalApiRateLimiter : PartitionedRateLimiter<HttpContext>
{
    private static readonly object RejectedKey = new();
    private readonly PartitionedRateLimiter<HttpContext> _inner;

    public GlobalApiRateLimiter(PartitionedRateLimiter<HttpContext> inner) => _inner = inner;

    /// <summary>True when the global limit refused this request.</summary>
    public static bool Rejected(HttpContext context) => context.Items.ContainsKey(RejectedKey);

    public override RateLimiterStatistics? GetStatistics(HttpContext resource) => _inner.GetStatistics(resource);

    protected override RateLimitLease AttemptAcquireCore(HttpContext resource, int permitCount)
        => Mark(resource, _inner.AttemptAcquire(resource, permitCount));

    protected override async ValueTask<RateLimitLease> AcquireAsyncCore(HttpContext resource, int permitCount, CancellationToken cancellationToken)
        => Mark(resource, await _inner.AcquireAsync(resource, permitCount, cancellationToken).ConfigureAwait(false));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    protected override async ValueTask DisposeAsyncCore()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsyncCore().ConfigureAwait(false);
    }

    private static RateLimitLease Mark(HttpContext resource, RateLimitLease lease)
    {
        if (!lease.IsAcquired)
            resource.Items[RejectedKey] = true;
        return lease;
    }
}
