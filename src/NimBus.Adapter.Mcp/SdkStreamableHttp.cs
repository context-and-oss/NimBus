using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace NimBus.Adapter.Mcp;

/// <summary>
/// The MCP SDK's Streamable HTTP POST handler, lifted out of endpoint routing so it can run on any
/// <see cref="HttpContext"/>, including one an Azure Functions HttpTrigger hands over. The SDK only
/// exposes its handler through <c>MapMcp</c>; mapping it onto a detached route builder and taking
/// the POST endpoint's delegate gives the same handler without the host's routing.
/// </summary>
internal sealed class SdkStreamableHttp
{
    private readonly Lazy<RequestDelegate> _post;

    public SdkStreamableHttp(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _post = new Lazy<RequestDelegate>(() => BuildPost(services), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Runs one JSON-RPC POST through the SDK.</summary>
    public Task PostAsync(HttpContext context) => _post.Value(context);

    private static RequestDelegate BuildPost(IServiceProvider services)
    {
        var routes = new DetachedRouteBuilder(services);
        routes.MapMcp("/");

        var post = routes.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase) == true);

        return post?.RequestDelegate
            ?? throw new InvalidOperationException("The MCP SDK mapped no POST endpoint; check the ModelContextProtocol.AspNetCore version.");
    }

    private sealed class DetachedRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider => services;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(services);
    }
}
