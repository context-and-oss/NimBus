using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;
using NimBus.WebApp.Services;
using NimBus.Extensions.Identity;

namespace NimBus.WebApp;

public partial class Startup
{
    // Security: fail fast if dangerous development-only settings are active
    // in non-Development environments.
    private void EnforceProductionSafeConfiguration()
    {
        // Security: Fail fast if dangerous development-only settings are active in non-Development environments
        if (!Env.IsDevelopment())
        {
            if (Configuration.GetValue<bool>("BypassEndpointAuthorization", false))
                throw new InvalidOperationException("SECURITY: BypassEndpointAuthorization must not be enabled outside Development environment. Remove this setting from production configuration.");

            if (Configuration.GetValue<bool>("EnableLocalDevAuthentication", false))
                throw new InvalidOperationException("SECURITY: EnableLocalDevAuthentication must not be enabled outside Development environment. Remove this setting from production configuration.");

            if (Configuration.GetValue<bool>("Authorization:GrantPiiReaderInDevelopment", false))
                throw new InvalidOperationException("SECURITY: Authorization:GrantPiiReaderInDevelopment must not be enabled outside Development environment. Remove this setting from production configuration.");
        }
    }

    // Identity opt-in, the four-way authentication branch ladder, MVC
    // authorization conventions and the OIDC 401-for-API tweak.
    private void AddAuthenticationStack(IServiceCollection services)
    {
        // Opt into ASP.NET Core Identity-backed username/password sign-in when the
        // deployment supplies NimBusIdentity:ConnectionString. The reflection check below
        // then routes the auth-branch ladder to the Identity-only path; downstream config
        // (RequireEmailConfirmation, Bootstrap, Smtp) is bound from the same section.
        var identityConnection = Configuration["NimBusIdentity:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(identityConnection))
        {
            services.AddNimBusIdentity(opts =>
            {
                opts.ConnectionString = identityConnection;
                var schema = Configuration["NimBusIdentity:Schema"];
                if (!string.IsNullOrWhiteSpace(schema)) opts.Schema = schema;
                opts.RequireEmailConfirmation = Configuration.GetValue("NimBusIdentity:RequireEmailConfirmation", true);
                opts.EnableEntraIdLogin = Configuration.GetValue("NimBusIdentity:EnableEntraIdLogin", false);

                opts.Bootstrap.Email = Configuration["NimBusIdentity:Bootstrap:Email"] ?? string.Empty;
                opts.Bootstrap.Password = Configuration["NimBusIdentity:Bootstrap:Password"] ?? string.Empty;
                opts.Bootstrap.DisplayName = Configuration["NimBusIdentity:Bootstrap:DisplayName"] ?? string.Empty;

                Configuration.GetSection("NimBusIdentity:Smtp").Bind(opts.Smtp);
            });
        }

        // Bypass authentication for local development (requires explicit opt-in via config)
        var enableLocalDevAuth = Configuration.GetValue<bool>("EnableLocalDevAuthentication", false);
        var hasNimBusIdentity = services.Any(s => s.ServiceType.FullName == "NimBus.Extensions.Identity.INimBusIdentityMarker");
        var hasEntraId = Configuration.GetSection("AzureAd").GetValue<string>("ClientId") is { Length: > 0 };

        if (Env.IsDevelopment() && enableLocalDevAuth)
        {
            System.Console.WriteLine("WARNING: Local development authentication bypass is ENABLED. This should NEVER be used in production!");

            services.AddAuthentication(LocalDevAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, LocalDevAuthHandler>(LocalDevAuthHandler.SchemeName, null);

            services.AddControllersWithViews().AddMicrosoftIdentityUI();
        }
        else if (hasNimBusIdentity && !hasEntraId)
        {
            // Identity-only mode: ASP.NET Core Identity cookies (no Azure AD)
            services.AddControllersWithViews(options =>
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            });
        }
        else if (hasNimBusIdentity && hasEntraId)
        {
            // Dual mode: Identity cookies + Azure AD
            services
            .AddAuthentication("Az")
            .AddPolicyScheme("Az", "Authorize AzureAD, AzureADBearer, or Identity", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                    if (authHeader?.StartsWith("Bearer", StringComparison.Ordinal) == true)
                    {
                        return JwtBearerDefaults.AuthenticationScheme;
                    }

                    // If user has Identity cookie, use Identity scheme
                    if (context.Request.Cookies.ContainsKey("NimBus.Identity"))
                    {
                        return Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme;
                    }

                    return OpenIdConnectDefaults.AuthenticationScheme;
                };
            })
            .AddMicrosoftIdentityWebApi(Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();

            services.AddMicrosoftIdentityWebAppAuthentication(Configuration, "AzureAd");

            services.AddControllersWithViews(options =>
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            }).AddMicrosoftIdentityUI();
        }
        else
        {
            // Without a ClientId this branch still starts, then every request (anonymous
            // ones included) fails with Microsoft.Identity.Web's IDW10106 when the OIDC
            // handler initializes. Fail at startup instead, naming the settings to fix.
            if (!hasEntraId)
            {
                throw new InvalidOperationException(
                    "No sign-in method is configured for the NimBus WebApp. Set AzureAd:ClientId and AzureAd:TenantId " +
                    "(app settings AzureAd__ClientId, AzureAd__TenantId) for Microsoft Entra ID sign-in, or " +
                    "NimBusIdentity:ConnectionString for username/password sign-in. For local development only, set " +
                    "EnableLocalDevAuthentication=true in the Development environment. See docs/authentication.md.");
            }

            // Entra ID only (original behavior)
            services
            .AddAuthentication("Az")
            .AddPolicyScheme("Az", "Authorize AzureAD or AzureADBearer", options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
                    if (authHeader?.StartsWith("Bearer", StringComparison.Ordinal) == true)
                    {
                        return JwtBearerDefaults.AuthenticationScheme;
                    }
                    return OpenIdConnectDefaults.AuthenticationScheme;
                };
            })
            .AddMicrosoftIdentityWebApi(Configuration.GetSection("AzureAd"))
            .EnableTokenAcquisitionToCallDownstreamApi()
            .AddInMemoryTokenCaches();

            services.AddMicrosoftIdentityWebAppAuthentication(Configuration, "AzureAd");

            services.AddControllersWithViews(options =>
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .Build();
                options.Filters.Add(new AuthorizeFilter(policy));
            }).AddMicrosoftIdentityUI();

            // Entra tokens carry group object IDs (GUIDs), never names, so the
            // literal groups == "EIP_Management" admin checks can't match an
            // Entra principal on their own. Map configured admin group/user
            // object IDs (Authorization:AdminGroupObjectIds / :AdminUserObjectIds)
            // onto the internal marker. Registered only on this Entra-only branch:
            // IClaimsTransformation is single-resolution (last registration wins),
            // and the Identity branches rely on NimBusClaimsTransformation.
            services.AddTransient<IClaimsTransformation, EntraAdminClaimsTransformation>();
        }

        // The NSwag-generated controllers cannot carry per-action attributes,
        // so the stats endpoint's anonymous exemption is applied via an
        // application-model convention instead of a class-level
        // [AllowAnonymous] (which would silently exempt every action on the
        // controller, e.g. /api/me).
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Conventions.Add(new AllowAnonymousActionsConvention()));

        // Entra/OIDC parity with the Identity cookie's clean-401 behaviour
        // (spec 010 FR-011/FR-012). When an anonymous, non-bearer request to
        // the SignalR hub or /api/* is challenged, the policy scheme forwards
        // to OpenIdConnect, whose default OnRedirectToIdentityProvider issues a
        // 302 to the IdP — which a SignalR negotiate or SPA fetch cannot follow.
        // Suppress the redirect for those surfaces and return a literal 401 so
        // the client surfaces the standard "session expired" affordance.
        // Browser navigations to non-API paths still redirect to the IdP.
        if (hasEntraId)
        {
            services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
            {
                var previous = options.Events.OnRedirectToIdentityProvider;
                options.Events.OnRedirectToIdentityProvider = async ctx =>
                {
                    if (NimBusCookieAuthenticationEvents.IsApiOrHubPath(ctx.Request.Path))
                    {
                        ctx.Response.StatusCode = Microsoft.AspNetCore.Http.StatusCodes.Status401Unauthorized;
                        ctx.HandleResponse();
                        return;
                    }

                    if (previous is not null)
                    {
                        await previous(ctx).ConfigureAwait(false);
                    }
                };
            });
        }

        if (!hasNimBusIdentity)
        {
            // The Identity extension's controllers reach MVC as an application
            // part on every build (Razor class library), but their
            // SignInManager/UserManager dependencies exist only when
            // AddNimBusIdentity ran above. Unregister them so /api/auth/* and
            // /account/* 404 — which is what the SPA expects — instead of
            // failing activation with a 500. See
            // IdentityControllersDisabledFeatureProvider.
            services.AddControllers().ConfigureApplicationPartManager(
                apm => apm.FeatureProviders.Add(new IdentityControllersDisabledFeatureProvider()));
        }
    }

    private void AddAuthorizationAndAuditServices(IServiceCollection services)
    {
        // Short-TTL cache for hot read-only store results (status counts,
        // metrics aggregates). Singleton is required: the consuming
        // controllers are transient, so a shorter lifetime would never hit.
        services.AddMemoryCache();
        services.AddSingleton<IStoreResultCache, StoreResultCache>();
        // Spec 026: the ACL snapshot cache must be a singleton (the consuming
        // authorization service is scoped, so a shorter lifetime would never
        // hit); the authorization service memoizes one resolution per request.
        services.AddSingleton<IAccessControlSnapshotProvider, AccessControlSnapshotProvider>();
        services.AddScoped<IEndpointAuthorizationService, EndpointAuthorizationService>();
        // Spec 008: centralized audit-write contract. Scoped to the request's
        // audit workflow; its narrow store dependency resolves to the selected
        // provider singleton.
        // Settings → Audit logging selection. Singleton so its short-TTL cache survives the
        // scoped audit writer that consults it on every row.
        services.AddSingleton<IAuditSettingsProvider, AuditSettingsProvider>();
        services.AddScoped<IAuditLogService, AuditLogService>();
        // Shared hand-off settlement core used by both the operator (EventImplementation)
        // and agent (AgentImplementation) settle endpoints so neither can skip the audit row.
        services.AddScoped<IHandoffSettlementService, HandoffSettlementService>();
        // Settlements sent but not yet projected by the Resolver. Singleton so a receive or
        // second settle on another request sees them (per process, not across instances).
        services.AddSingleton(sp => new HandoffSettlementTracker(sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddScoped<NimBus.WebApp.Services.Operations.IOperatorCommands, NimBus.WebApp.Services.Operations.OperatorCommandCoordinator>();
    }
}
