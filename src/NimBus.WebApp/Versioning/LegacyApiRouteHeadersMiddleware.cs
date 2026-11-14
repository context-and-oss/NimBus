namespace NimBus.WebApp.Versioning;

/// <summary>
/// Tells callers of a deprecated, unversioned <c>/api/…</c> route where its successor is:
/// <c>Deprecation</c> (RFC 9745, the date the unversioned routes were deprecated) and
/// <c>Link: &lt;/api/v1/…&gt;; rel="successor-version"</c> (RFC 8288). Runs after routing, so the
/// endpoint is known, and sets the headers as the response starts, so error responses carry
/// them too.
/// </summary>
internal sealed class LegacyApiRouteHeadersMiddleware
{
    /// <summary>2026-11-12T00:00:00Z, when the unversioned routes were deprecated, as an RFC 9745 date.</summary>
    internal const string DeprecationValue = "@1794441600";

    private readonly RequestDelegate _next;

    public LegacyApiRouteHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<LegacyApiRouteMetadata>() is not null)
        {
            var successor = SuccessorPath(context.Request.PathBase, context.Request.Path);
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["Deprecation"] = DeprecationValue;
                context.Response.Headers.Append("Link", $"<{successor}>; rel=\"successor-version\"");
                return Task.CompletedTask;
            });
        }

        return _next(context);
    }

    /// <summary>The <c>/api/v1/…</c> path that replaces an unversioned <c>/api/…</c> path.</summary>
    internal static string SuccessorPath(PathString pathBase, PathString path)
    {
        var value = path.Value ?? string.Empty;
        // "/api" is 4 characters; everything after it moves under "/api/v1".
        var rest = value.Length > 4 ? value[4..] : string.Empty;
        return pathBase.Add(new PathString("/api/" + ApiVersionAliasConvention.CurrentVersionSegment + rest)).ToUriComponent();
    }
}
