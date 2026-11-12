#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Controllers;
using NimBus.Extensions.Identity.Data;

namespace NimBus.Extensions.Identity.Tests;

[TestClass]
public class AuthApiControllerTests
{
    [TestMethod]
    public async Task Me_ReturnsAnonymous_WhenNoUserPrincipal()
    {
        await using var scope = IdentityTestScope.Create();
        await InitializeAsync(scope);

        var controller = CreateController(scope, principal: null);
        var result = await controller.Me();

        var ok = (OkObjectResult)result;
        var body = (AuthApiController.CurrentUserResponse)ok.Value!;
        Assert.IsFalse(body.IsAuthenticated);
        Assert.IsNull(body.Email);
        Assert.IsNull(body.DisplayName);
    }

    [TestMethod]
    public async Task Me_ReturnsAuthenticatedUser_WithEmailAndDisplayName()
    {
        await using var scope = IdentityTestScope.Create();
        await InitializeAsync(scope);

        // Seed a user we can sign in as.
        using (var seed = scope.Services.CreateScope())
        {
            var um = seed.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
            var user = new NimBusUser
            {
                UserName = "ops@example.com",
                Email = "ops@example.com",
                DisplayName = "Operator",
                EmailConfirmed = true,
            };
            var createResult = await um.CreateAsync(user, "Ops!Example123");
            Assert.IsTrue(createResult.Succeeded, "harness setup: user seed must succeed");
        }

        // Build a ClaimsPrincipal whose NameIdentifier matches the seeded user
        // (UserManager.GetUserAsync(User) looks this up).
        ClaimsPrincipal principal;
        string seededUserId;
        using (var lookup = scope.Services.CreateScope())
        {
            var um = lookup.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
            var seeded = await um.FindByEmailAsync("ops@example.com");
            seededUserId = seeded!.Id;
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, seededUserId),
            new Claim(ClaimTypes.Name, "ops@example.com"),
        }, "TestAuth");
        principal = new ClaimsPrincipal(identity);

        var controller = CreateController(scope, principal);
        var result = await controller.Me();

        var ok = (OkObjectResult)result;
        var body = (AuthApiController.CurrentUserResponse)ok.Value!;
        Assert.IsTrue(body.IsAuthenticated);
        Assert.AreEqual("ops@example.com", body.Email);
        Assert.AreEqual("Operator", body.DisplayName);
    }

    [TestMethod]
    public async Task Logout_ReturnsNoContent()
    {
        await using var scope = IdentityTestScope.Create();
        await InitializeAsync(scope);

        var controller = CreateController(scope, principal: null);
        var result = await controller.Logout();

        Assert.IsInstanceOfType<NoContentResult>(result);
    }

    private static AuthApiController CreateController(IdentityTestScope scope, ClaimsPrincipal? principal)
    {
        var serviceScope = scope.Services.CreateScope();
        var signInManager = serviceScope.ServiceProvider.GetRequiredService<SignInManager<NimBusUser>>();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();

        var httpContext = new DefaultHttpContext
        {
            RequestServices = serviceScope.ServiceProvider,
            User = principal ?? new ClaimsPrincipal(new ClaimsIdentity()),
        };

        // SignInManager.SignOutAsync() reads its HttpContext from the
        // accessor, not from the controller's ControllerContext. Without this
        // the accessor's HttpContext is null and SignOutAsync throws.
        serviceScope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        return new AuthApiController(signInManager, userManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
        };
    }

    private static async Task InitializeAsync(IdentityTestScope scope)
    {
        var initializer = scope.Services.GetServices<IHostedService>()
            .OfType<Services.IdentityInitializerHostedService>()
            .Single();
        await initializer.StartAsync(CancellationToken.None);
    }
}
