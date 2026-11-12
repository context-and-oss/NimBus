using Microsoft.AspNetCore.Mvc;

namespace NimBus.WebApp.Versioning;

/// <summary>Registers the <c>/api/v1</c> routes and the deprecated unversioned aliases.</summary>
public static class ApiVersioningExtensions
{
    /// <summary>
    /// Serves every API controller route at <c>/api/v1/…</c> and, deprecated, at <c>/api/…</c>.
    /// The generated controllers also carry <see cref="ApiVersionAliasesAttribute"/>, so hosts
    /// that don't call this (tests) still serve both forms.
    /// </summary>
    public static IServiceCollection AddNimBusApiVersioning(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Configure<MvcOptions>(options => options.Conventions.Insert(0, new ApiVersionAliasConvention()));
        return services;
    }

    /// <summary>
    /// Adds the <c>Deprecation</c> and successor <c>Link</c> headers to responses from a
    /// deprecated unversioned route. Register it after routing.
    /// </summary>
    public static IApplicationBuilder UseNimBusLegacyApiRouteHeaders(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<LegacyApiRouteHeadersMiddleware>();
    }
}
