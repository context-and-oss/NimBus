using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CrmErpDemo.Contracts.Security;

/// <summary>
/// Sign-in for the CRM and ERP apps in the Azure deployment. People sign in with their nimbus-ops
/// accounts (NimBus Identity, same user store), and the adapters, which call the APIs over the same
/// public URLs, present a shared demo service key instead. Local runs configure neither and stay
/// anonymous.
/// </summary>
public static class DemoSignIn
{
    /// <summary>The policy that admits a signed-in NimBus user or the demo service key.</summary>
    public const string PolicyName = "DemoUser";

    /// <summary>The authentication scheme that accepts the demo service key.</summary>
    public const string ServiceKeyScheme = "DemoServiceKey";

    /// <summary>The request header that carries the demo service key.</summary>
    public const string ServiceKeyHeader = "X-Demo-Service-Key";

    /// <summary>The configuration key (<c>Demo__ServiceKey</c>) holding the demo service key.</summary>
    public const string ServiceKeyConfigKey = "Demo:ServiceKey";

    /// <summary>True when the deployment supplies a NimBus Identity database, which turns sign-in on.</summary>
    public static bool IsEnabled(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["NimBusIdentity:ConnectionString"]);

    /// <summary>
    /// Adds the Identity sign-in pages, the service-key scheme and the <see cref="PolicyName"/> policy.
    /// Call after <c>AddNimBusIdentity</c>, which adds the Identity cookie.
    /// </summary>
    public static IServiceCollection AddDemoSignIn(this IServiceCollection services, IConfiguration configuration)
    {
        // The /account/* pages and /api/auth/* come from NimBus.Extensions.Identity's controllers.
        services.AddControllersWithViews();
        services.AddAuthentication()
            .AddScheme<DemoServiceKeyOptions, DemoServiceKeyAuthenticationHandler>(
                ServiceKeyScheme, options => options.Key = configuration[ServiceKeyConfigKey]);
        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy => policy
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, ServiceKeyScheme)
                .RequireAuthenticatedUser());
        return services;
    }

    /// <summary>Adds authentication and authorization to the pipeline and maps the sign-in controllers.</summary>
    public static WebApplication UseDemoSignIn(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        return app;
    }

    /// <summary>Requires a signed-in user or the service key for the endpoints.</summary>
    public static TBuilder RequireDemoSignIn<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.RequireAuthorization(PolicyName);

    /// <summary>
    /// Adapter side: every HTTP client sends the demo service key, but only to <paramref name="apiBaseUrl"/>'s
    /// origin. Does nothing when no key is configured (local runs).
    /// </summary>
    public static IServiceCollection AddDemoServiceKey(this IServiceCollection services, IConfiguration configuration, string apiBaseUrl)
    {
        var key = configuration[ServiceKeyConfigKey];
        if (string.IsNullOrEmpty(key)) return services;

        var api = new Uri(apiBaseUrl);
        services.ConfigureHttpClientDefaults(http => http.AddHttpMessageHandler(() => new DemoServiceKeyHttpHandler(key, api)));
        return services;
    }

    internal static bool KeysMatch(string supplied, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(supplied)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

/// <summary>Options for <see cref="DemoServiceKeyAuthenticationHandler"/>.</summary>
public sealed class DemoServiceKeyOptions : AuthenticationSchemeOptions
{
    /// <summary>The expected key. When empty, the scheme authenticates nobody.</summary>
    public string? Key { get; set; }
}

/// <summary>Authenticates requests that carry the demo service key as the <c>demo-service</c> principal.</summary>
public sealed class DemoServiceKeyAuthenticationHandler(
    IOptionsMonitor<DemoServiceKeyOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<DemoServiceKeyOptions>(options, logger, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = Options.Key;
        var supplied = Request.Headers[DemoSignIn.ServiceKeyHeader].ToString();
        if (string.IsNullOrEmpty(expected) || supplied.Length is 0 or > 256 || !DemoSignIn.KeysMatch(supplied, expected))
            return Task.FromResult(AuthenticateResult.NoResult());

        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "demo-service")], Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }

    /// <summary>
    /// Leaves the challenge to the Identity cookie, which redirects browsers to sign in and answers
    /// /api with 401. The default would overwrite that redirect with a bare 401.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Task.CompletedTask;
}

/// <summary>Adds the demo service key to requests for the API's origin.</summary>
internal sealed class DemoServiceKeyHttpHandler(string key, Uri api) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { IsAbsoluteUri: true } target
            && Uri.Compare(target, api, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
        {
            request.Headers.Remove(DemoSignIn.ServiceKeyHeader);
            request.Headers.Add(DemoSignIn.ServiceKeyHeader, key);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
