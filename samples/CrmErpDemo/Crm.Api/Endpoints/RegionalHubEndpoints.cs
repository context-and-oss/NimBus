using CrmErpDemo.Contracts.Endpoints;

namespace Crm.Api.Endpoints;

/// <summary>
/// Routing-keys showcase: tells crm-web which regional hub takes which countries, read from the
/// endpoint declarations the topology is built from, so the hint on the account form cannot
/// disagree with what Service Bus routes.
/// </summary>
public static class RegionalHubEndpoints
{
    public static void MapRegionalHubEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/regional-hubs", () => Results.Ok(RegionalHubs.All));
    }
}
