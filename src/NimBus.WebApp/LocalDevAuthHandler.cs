using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;

namespace NimBus.WebApp;

/// <summary>
/// Authentication handler for local development that bypasses Azure AD authentication.
/// This should only be used in Development environment with EnableLocalDevAuthentication=true.
/// </summary>
public partial class LocalDevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>The authentication scheme name this handler is registered under.</summary>
    public const string SchemeName = "LocalDev";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<LocalDevAuthHandler> _authLogger;

    public LocalDevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration,
        IWebHostEnvironment environment)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
        _environment = environment;
        _authLogger = logger.CreateLogger<LocalDevAuthHandler>();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Defense-in-depth: ensure this handler cannot grant access outside Development
        if (!_environment.IsDevelopment())
        {
            LogCriticalLocalDevAuthHandlerWasInvokedOutside(_authLogger);
            return Task.FromResult(AuthenticateResult.Fail("Local development authentication is not available outside Development environment."));
        }

        // Safety check: only allow bypass if explicitly enabled in configuration
        var enableLocalDevAuth = _configuration.GetValue<bool>("EnableLocalDevAuthentication", false);

        if (!enableLocalDevAuth)
        {
            LogLocalDevelopmentAuthenticationBypassAttempted(_authLogger);
            return Task.FromResult(AuthenticateResult.Fail("Local development authentication is not enabled. Set EnableLocalDevAuthentication=true in configuration to enable."));
        }

        LogSecurityWarningLocalDevelopmentAuthenticationBypass(_authLogger);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "local-dev-user"),
            new Claim(ClaimTypes.Name, "Local Developer"),
            new Claim(ClaimTypes.Email, "dev@localhost"),
            new Claim("groups", "EIP_Management") // Grant admin access for local development
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "CRITICAL: LocalDevAuthHandler was invoked outside of Development environment. This is a security misconfiguration. Denying request.")]
    private static partial void LogCriticalLocalDevAuthHandlerWasInvokedOutside(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Local development authentication bypass attempted but EnableLocalDevAuthentication is not enabled. Denying request.")]
    private static partial void LogLocalDevelopmentAuthenticationBypassAttempted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SECURITY WARNING: Local development authentication bypass is ENABLED. This should NEVER be used in production!")]
    private static partial void LogSecurityWarningLocalDevelopmentAuthenticationBypass(ILogger logger);
}
