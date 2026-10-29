#pragma warning disable CA1707, CA2007

using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Csrf;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Issue #296: which controllers carry <see cref="CookieAntiforgeryFilter"/>, read from the action
/// descriptors MVC builds through the production registration. The generated controllers can't
/// carry an attribute (NSwag rewrites <c>ApiContract.g.cs</c>), so a convention attaches it, and
/// this pins that it reaches every generated controller but the three that must not get it.
/// </summary>
[TestClass]
public sealed class CookieAntiforgeryConventionTests
{
    private static readonly Type[] Exempt =
    {
        // Anonymous, authenticated by X-Webhook-Key.
        typeof(StorageHookApiController),
        // Their [AutoValidateAntiforgeryToken] already requires the token on every write.
        typeof(AgentKeysApiController),
        typeof(McpAccessApiController),
    };

    [TestMethod]
    public async Task Every_generated_controller_but_the_exempt_ones_validates_cookie_writes()
    {
        using var host = await StartHostAsync();
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .Where(action => action.ControllerTypeInfo.Namespace == typeof(AdminApiController).Namespace)
            .ToList();

        var generatedControllers = actions.Select(action => action.ControllerTypeInfo.AsType()).Distinct().ToList();
        Assert.IsTrue(generatedControllers.Count > Exempt.Length, "The generated controllers were not found.");

        foreach (var action in actions)
        {
            var exempt = Exempt.Contains(action.ControllerTypeInfo.AsType());
            Assert.AreEqual(!exempt, CarriesFilter(action), $"{action.DisplayName}: expected the filter {(exempt ? "not " : "")}to be attached.");
        }
    }

    [TestMethod]
    public async Task The_admin_writes_named_in_the_issue_validate_cookie_writes()
    {
        using var host = await StartHostAsync();
        var actions = host.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>()
            .ToList();

        foreach (var name in new[]
                 {
                     nameof(AdminApiController.PostAdminDeleteAll),
                     nameof(AdminApiController.PostAdminServicebusSubscriptionPurge),
                     nameof(AdminApiController.PostAdminServicebusSubscriptionRecreate),
                     nameof(AdminApiController.PostAdminDeleteDeadlettered),
                 })
        {
            var action = actions.Single(candidate =>
                candidate.ControllerTypeInfo.AsType() == typeof(AdminApiController) && candidate.ActionName == name);
            Assert.IsTrue(CarriesFilter(action), name);
        }
    }

    private static bool CarriesFilter(ControllerActionDescriptor action) =>
        action.FilterDescriptors.Any(descriptor =>
            descriptor.Filter is ServiceFilterAttribute service && service.ServiceType == typeof(CookieAntiforgeryFilter));

    private static Task<IHost> StartHostAsync() =>
        new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
                    services.AddNimBusCookieAntiforgery();
                    // MVC resolves application parts from the entry assembly, which under
                    // `dotnet test` is the test host, so the WebApp's part is added explicitly.
                    services.AddControllers().AddApplicationPart(typeof(AdminApiController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
}
