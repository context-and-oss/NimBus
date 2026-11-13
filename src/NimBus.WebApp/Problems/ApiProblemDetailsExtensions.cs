using Microsoft.AspNetCore.Mvc;

namespace NimBus.WebApp.Problems;

/// <summary>Registers the RFC 7807 problem responses for the <c>/api</c> surface.</summary>
public static class ApiProblemDetailsExtensions
{
    /// <summary>
    /// Adds the MVC filter that turns <c>/api</c> error results into problems. Pair it with
    /// <see cref="UseNimBusApiProblemDetails"/>, which covers bodyless errors and exceptions.
    /// </summary>
    public static IServiceCollection AddNimBusApiProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<MvcOptions>(options => options.Filters.Add<ApiProblemDetailsResultFilter>());
        return services;
    }

    /// <summary>
    /// Writes a problem for every <c>/api</c> error that ends without a body, and for unhandled
    /// exceptions. Register it before routing and authentication so their responses are covered.
    /// </summary>
    public static IApplicationBuilder UseNimBusApiProblemDetails(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<ApiProblemDetailsMiddleware>();
    }

    /// <summary>
    /// Answers every unmatched <c>/api</c> route with 404, ahead of the SPA fallback that would
    /// otherwise return <c>index.html</c> with 200. The middleware adds the problem body.
    /// </summary>
    public static IEndpointConventionBuilder MapNimBusApiNotFound(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        return endpoints.MapFallback("/api/{**path}", context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        });
    }
}
