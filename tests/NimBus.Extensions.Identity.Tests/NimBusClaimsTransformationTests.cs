#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Services;

namespace NimBus.Extensions.Identity.Tests;

[TestClass]
public class NimBusClaimsTransformationTests
{
    [TestMethod]
    public async Task Roles_become_groups_claims()
    {
        // Regression: adding claims while enumerating principal.Claims threw "Collection was
        // modified" for every user holding a role, such as the bootstrap admin.
        var principal = IdentityUser(
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Name, "admin@local"),
            new Claim(ClaimTypes.Role, "EIP_Management"),
            new Claim(ClaimTypes.Role, "Auditors"));

        var transformed = await new NimBusClaimsTransformation().TransformAsync(principal);

        CollectionAssert.AreEquivalent(
            new[] { "EIP_Management", "Auditors" },
            transformed.FindAll("groups").Select(c => c.Value).ToArray());
        Assert.AreEqual("user-1", transformed.FindFirst("oid")?.Value);
        Assert.AreEqual("admin@local", transformed.FindFirst("name")?.Value);
    }

    [TestMethod]
    public async Task Running_twice_adds_nothing_more()
    {
        var principal = IdentityUser(
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim(ClaimTypes.Role, "EIP_Management"));
        var transformation = new NimBusClaimsTransformation();

        await transformation.TransformAsync(principal);
        var transformed = await transformation.TransformAsync(principal);

        Assert.AreEqual(1, transformed.FindAll("groups").Count());
        Assert.AreEqual(1, transformed.FindAll("oid").Count());
    }

    [TestMethod]
    public async Task Other_schemes_are_left_alone()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "EIP_Management") }, "SomeOtherScheme"));

        var transformed = await new NimBusClaimsTransformation().TransformAsync(principal);

        Assert.IsFalse(transformed.HasClaim(c => c.Type == "groups"));
    }

    private static ClaimsPrincipal IdentityUser(params Claim[] claims)
        => new(new ClaimsIdentity(claims, IdentityConstants.ApplicationScheme));
}
