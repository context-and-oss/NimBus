#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Data;

namespace NimBus.Extensions.Identity.Tests;

[TestClass]
public class IdentityInitializerHostedServiceTests
{
    [TestMethod]
    public async Task FirstRun_CreatesSchemaAndBootstrapsAdmin()
    {
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "admin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var users = await userManager.Users.ToListAsync();

        Assert.AreEqual(1, users.Count, "bootstrap should seed exactly one admin on a fresh store");
        Assert.AreEqual("admin@local", users[0].Email);
        Assert.IsTrue(users[0].EmailConfirmed, "bootstrap admin must be confirmed so it can sign in without SMTP");
    }

    [TestMethod]
    public async Task FirstRun_BootstrapAdminIsPlatformAdmin()
    {
        // Without the platform-admin role the bootstrap admin can sign in but is denied
        // everywhere on a fresh store, and has no way to grant anyone access.
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "admin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var admin = await userManager.FindByEmailAsync("admin@local");
        Assert.IsNotNull(admin);
        CollectionAssert.Contains(
            (await userManager.GetRolesAsync(admin)).ToList(),
            "EIP_Management",
            "bootstrap admin must hold the platform-admin role");
    }

    [TestMethod]
    public async Task SecondRun_IsIdempotent_NoSecondAdminCreated()
    {
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Bootstrap.Email = "admin@local";
            o.Bootstrap.Password = "Local!Admin123";
        });

        await RunInitializerAsync(scope);
        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var count = await userManager.Users.CountAsync();

        Assert.AreEqual(1, count, "running the initializer twice must not seed a second admin");
    }

    [TestMethod]
    public async Task Bootstrap_NoOpsWhenUserStoreAlreadyHasUsers()
    {
        // No bootstrap creds yet — the first run must create the schema and
        // tables without seeding anyone, so we can plant a different user by
        // hand and then prove a later run with creds leaves it untouched.
        await using var scope = IdentityTestScope.Create();

        await RunInitializerAsync(scope);

        using (var seedScope = scope.Services.CreateScope())
        {
            var userManager = seedScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
            var existing = new NimBusUser
            {
                UserName = "preexisting@local",
                Email = "preexisting@local",
                EmailConfirmed = true,
            };
            var result = await userManager.CreateAsync(existing, "Pre!Existing123");
            Assert.IsTrue(result.Succeeded, "harness setup: existing user must seed");
        }

        // Now turn bootstrap on. The store is non-empty, so the second run
        // must skip seeding rather than add "newadmin@local".
        var options = scope.Services.GetRequiredService<NimBusIdentityOptions>();
        options.Bootstrap.Email = "newadmin@local";
        options.Bootstrap.Password = "Local!Admin123";

        await RunInitializerAsync(scope);

        using var verifyScope = scope.Services.CreateScope();
        var verifyManager = verifyScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var users = await verifyManager.Users.ToListAsync();
        Assert.AreEqual(1, users.Count, "bootstrap must not seed when the user store is non-empty");
        Assert.AreEqual("preexisting@local", users[0].Email);
    }

    [TestMethod]
    public async Task NoBootstrapConfigured_CreatesSchemaButSeedsNoUsers()
    {
        await using var scope = IdentityTestScope.Create();

        await RunInitializerAsync(scope);

        using var serviceScope = scope.Services.CreateScope();
        var userManager = serviceScope.ServiceProvider.GetRequiredService<UserManager<NimBusUser>>();
        var count = await userManager.Users.CountAsync();
        Assert.AreEqual(0, count, "no bootstrap email + password means no seeded user");

        // But the schema + tables should still be there — verify by writing
        // and reading a user directly through UserManager.
        var probe = new NimBusUser { UserName = "probe@local", Email = "probe@local", EmailConfirmed = true };
        var create = await userManager.CreateAsync(probe, "Probe!Local123");
        Assert.IsTrue(create.Succeeded, "schema must be initialized even without bootstrap creds");
    }

    [TestMethod]
    public async Task InvalidSchema_LogsErrorAndDoesNotCrashHost()
    {
        // The initializer's StartAsync catches all exceptions and logs; an
        // invalid schema must not bring the host down. We only need to
        // verify StartAsync completes — the explanation is in the swallowed
        // log, not in observable behaviour.
        await using var scope = IdentityTestScope.Create(o =>
        {
            o.Schema = "1bad schema name"; // fails SchemaNamePattern
        });

        await RunInitializerAsync(scope); // must not throw

        // The schema was bad, so the tables weren't created and the user store is
        // unreachable. There is nothing to probe: reaching this line at all is the
        // assertion, since RunInitializerAsync would have propagated a throw.
    }

    private static async Task RunInitializerAsync(IdentityTestScope scope)
    {
        var initializer = scope.Services.GetServices<IHostedService>()
            .OfType<Services.IdentityInitializerHostedService>()
            .Single();
        await initializer.StartAsync(CancellationToken.None);
    }
}
