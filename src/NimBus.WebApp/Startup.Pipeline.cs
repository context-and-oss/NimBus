using NimBus.WebApp.Hubs;
using Microsoft.Net.Http.Headers;
using NimBus.WebApp.Middleware;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.Mcp;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace NimBus.WebApp;

public partial class Startup
{
    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Home/Error");
            // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
            app.UseHsts();
        }

        app.UseHttpsRedirection();

        // Response compression MUST run before UseStaticFiles and UseRouting.
        // The static-file middleware short-circuits the request and writes
        // the response body inline; if compression sits *after* it, the SPA bundle
        // ships uncompressed because the encoding is selected too late to apply to the
        // already-written stream. Routing/endpoint middleware has the same hazard for
        // JSON API responses. Do not move this call below either of them.
        app.UseResponseCompression();

        // Add security headers to all responses
        app.UseMiddleware<SecurityHeadersMiddleware>();

        app.UseRouting();

        // Serve the Brotli/gzip siblings the Vite build emits next to each
        // asset, before the SPA static-file middleware. When the client
        // accepts the encoding and the sibling exists on disk the request
        // is rewritten to it and Content-Encoding/Vary are set; otherwise
        // the request falls through and the plain asset is served (with
        // dynamic response compression above as the fallback).
        var spaAssetRoot = System.IO.Path.Combine(env.ContentRootPath, "ClientApp", "build", "public");
        if (System.IO.Directory.Exists(spaAssetRoot))
        {
            app.UseMiddleware<PrecompressedStaticFileMiddleware>(
                (Microsoft.Extensions.FileProviders.IFileProvider)new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaAssetRoot));

            // Serve the built SPA bundle. Must stay inside the Directory.Exists guard:
            // PhysicalFileProvider throws on a missing root, and unit-test hosts run
            // without a built SPA (see the matching guard on the fallback below).
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaAssetRoot),

                // Resolve the rewritten `.js.br` / `.css.gz` paths back to the
                // underlying asset's Content-Type so precompressed responses
                // keep their real type instead of application/octet-stream.
                ContentTypeProvider = new PrecompressedContentTypeProvider(),
                OnPrepareResponse = ctx =>
                {
                    var path = ctx.Context.Request.Path.Value ?? string.Empty;
                    var headers = ctx.Context.Response.GetTypedHeaders();
                    if (path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
                    {
                        // Vite emits content-hashed filenames under /assets/ — the bytes
                        // for a given URL never change, so cache them aggressively and skip
                        // revalidation entirely. A new deploy ships new hashes, new URLs.
                        headers.CacheControl = new CacheControlHeaderValue
                        {
                            Public = true,
                            MaxAge = TimeSpan.FromDays(365),
                            Extensions = { new NameValueHeaderValue("immutable") },
                        };
                    }
                    else
                    {
                        // Unhashed root assets (favicon, etc.) must revalidate so a deploy
                        // is picked up without serving stale bytes.
                        headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
                    }
                },
            });
        }

        // OpenAPI / Swagger UI publishes the management API surface, so keep
        // it gated to Development. Production hosts should not expose the
        // schema (or the "try it out" UI) anonymously.
        if (env.IsDevelopment())
        {
            app.UseOpenApi();
            app.UseSwaggerUi();
        }

        // Origin and loopback checks for /mcp, ahead of authentication. No-op when MCP is off.
        app.UseNimBusOperatorMcpGuard();

        if (app.ApplicationServices.GetService<IntegrationIntelligenceActivation>()?.Enabled == true)
            app.UseMiddleware<NimBus.Extensions.IntegrationIntelligence.IntegrationIntelligenceAuditMiddleware>();

        app.UseAuthentication();
        app.UseAuthorization();

        // After UseAuthorization, not right after UseRouting: the admin and
        // search partitions key on HttpContext.User, which UseAuthentication
        // populates. Placed earlier, every authenticated caller collapses
        // into one anonymous partition and two operators throttle each other.
        // Still satisfies the ordering contract — after UseRouting(), before
        // UseEndpoints(...) — which is what endpoint policy resolution needs.
        // Side benefit: unauthorized requests are rejected before they can
        // consume a permit.
        app.UseRateLimiter();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHealthChecks("/health");
            endpoints.MapHealthChecks("/alive", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
            endpoints.MapHealthChecks("/ready", new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("ready")
            });
            endpoints.MapHub<GridEventsHub>(Constants.AppEndpoints.GridEventHub);
            endpoints.MapNimBusOperatorMcp();
            endpoints.MapControllers();
            var loginPath = app.ApplicationServices.GetService(
                Type.GetType("NimBus.Extensions.Identity.INimBusIdentityMarker, NimBus.Extensions.Identity")
                ?? typeof(object)) != null ? "/account/login" : "/login";
            var fallbackOptions = new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    // index.html references content-hashed bundles; it MUST NOT be
                    // cached, or a browser holding a stale copy will request asset
                    // hashes that no longer exist after a deploy (blank page / 404s).
                    ctx.Context.Response.GetTypedHeaders().CacheControl =
                        new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };

                    if (!ctx.Context.User.Identity!.IsAuthenticated)
                    {
                        ctx.Context.Response.Redirect(loginPath);
                    }
                },
            };

            // The SPA (index.html included) lives only under ClientApp/build/public —
            // wwwroot holds no copy, so the fallback must use the SPA file provider
            // rather than the default WebRoot one. The Directory.Exists guard keeps
            // hosts without a built SPA (unit tests) constructible.
            if (System.IO.Directory.Exists(spaAssetRoot))
            {
                fallbackOptions.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(spaAssetRoot);
            }

            endpoints.MapFallbackToFile("index.html", fallbackOptions);
        });
    }
}
