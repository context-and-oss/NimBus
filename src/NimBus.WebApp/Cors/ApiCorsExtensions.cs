using Microsoft.Extensions.Options;
using NimBus.WebApp.Problems;

namespace NimBus.WebApp.Cors;

/// <summary>
/// Lets browser code on approved origins call the REST API (<c>/api/*</c>). The origins come
/// from <c>Cors:AllowedOrigins</c>; with none configured (the default) no CORS policy exists and
/// every cross-origin browser call is refused, as before.
/// <para>
/// Credentials are never allowed: a cross-origin caller authenticates with a header (an Entra
/// ID bearer token or an API key), so the browser never sends the WebApp's sign-in cookies and
/// cross-site request forgery stays impossible. Nothing outside <c>/api</c> (the SPA, the
/// SignalR hub, <c>/mcp</c>, sign-in) answers cross-origin requests.
/// </para>
/// </summary>
public static class ApiCorsExtensions
{
    /// <summary>The name of the REST API's CORS policy.</summary>
    public const string PolicyName = "NimBusApiCors";

    /// <summary>The configuration key that lists the allowed origins.</summary>
    public const string AllowedOriginsKey = "Cors:AllowedOrigins";

    /// <summary>Request headers a cross-origin caller may send.</summary>
    public static readonly IReadOnlyList<string> AllowedHeaders =
    [
        "Authorization", "Content-Type", "Accept", "Idempotency-Key", "X-NimBus-Agent-Key", "X-Agent-Id",
    ];

    /// <summary>Response headers a cross-origin caller may read.</summary>
    public static readonly IReadOnlyList<string> ExposedHeaders =
    [
        "Retry-After", "Deprecation", "Link", "Location", "Content-Disposition",
    ];

    /// <summary>
    /// Registers the API's CORS policy when <c>Cors:AllowedOrigins</c> lists any origin. Fails
    /// at startup on an empty entry, <c>*</c>, an origin with a path, query or credentials, or a
    /// non-HTTPS origin outside Development.
    /// </summary>
    public static IServiceCollection AddNimBusApiCors(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        var origins = ReadOrigins(configuration, environment);
        services.AddSingleton(Options.Create(new ApiCorsSettings(origins)));
        if (origins.Count == 0)
            return services;

        services.AddCors(cors => cors.AddPolicy(PolicyName, policy => policy
            .WithOrigins([.. origins])
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE")
            .WithHeaders([.. AllowedHeaders])
            .WithExposedHeaders([.. ExposedHeaders])
            .SetPreflightMaxAge(TimeSpan.FromMinutes(10))));
        return services;
    }

    /// <summary>
    /// Applies the API's CORS policy to <c>/api/*</c>. A no-op when no origin is configured.
    /// Register it after routing and before authentication, so a preflight is answered before
    /// the sign-in requirement turns it into a 401.
    /// </summary>
    public static IApplicationBuilder UseNimBusApiCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var settings = app.ApplicationServices.GetService<IOptions<ApiCorsSettings>>()?.Value;
        if (settings is null || settings.AllowedOrigins.Count == 0)
            return app;

        return app.UseWhen(context => ApiProblems.IsApiPath(context.Request.Path), api => api.UseCors(PolicyName));
    }

    internal static IReadOnlyList<string> ReadOrigins(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(AllowedOriginsKey);
        var entries = section.GetChildren().Select(c => c.Value).ToList();
        // A single string (Cors:AllowedOrigins=https://a) is accepted as one origin.
        if (entries.Count == 0 && section.Value is { } single)
            entries.Add(single);

        var origins = new List<string>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                throw Invalid("an entry is empty. Remove it, or list the origins as Cors__AllowedOrigins__0, Cors__AllowedOrigins__1 and so on");

            var value = entry.Trim();
            if (value == "*")
                throw Invalid("'*' is not allowed. List each origin that may call the API");

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || uri.PathAndQuery != "/"
                || !string.IsNullOrEmpty(uri.Fragment)
                || value.EndsWith('/'))
            {
                throw Invalid($"'{value}' is not an origin. Use scheme://host[:port], for example https://portal.example.com");
            }

            if (uri.Scheme != Uri.UriSchemeHttps && !environment.IsDevelopment())
                throw Invalid($"'{value}' is not HTTPS. Only HTTPS origins are allowed outside Development");

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
                origins.Add(origin);
        }

        return origins;
    }

    private static InvalidOperationException Invalid(string reason)
        => new($"{AllowedOriginsKey} is invalid: {reason}. See docs/webapp-rest-api.md#cors.");
}

/// <summary>The origins the REST API's CORS policy allows; empty when CORS is off.</summary>
/// <param name="AllowedOrigins">The normalised origins.</param>
public sealed record ApiCorsSettings(IReadOnlyList<string> AllowedOrigins);
