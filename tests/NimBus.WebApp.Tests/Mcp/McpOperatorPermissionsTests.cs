#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Mcp;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// The workload app roles must be definable on the same Entra app registration as the delegated
/// scopes (#407). Entra requires app-role and scope values to be unique ignoring case.
/// </summary>
[TestClass]
public class McpOperatorPermissionsTests
{
    [TestMethod]
    public void Workload_roles_do_not_collide_with_any_scope_ignoring_case()
    {
        var collisions = McpOperatorPermissions.AllRoles
            .Where(role => McpOperatorPermissions.AllScopes.Contains(role, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Assert.IsEmpty(collisions, $"Entra refuses these app-role values next to the scopes: {string.Join(", ", collisions)}");
    }

    [TestMethod]
    public void Workload_roles_are_unique_ignoring_case()
    {
        var distinct = McpOperatorPermissions.AllRoles.Distinct(StringComparer.OrdinalIgnoreCase).Count();

        Assert.AreEqual(McpOperatorPermissions.AllRoles.Count, distinct);
    }

    [TestMethod]
    [DataRow("Nimbus.Workload.Observe", DisplayName = "current role")]
    [DataRow("Nimbus.Observe", DisplayName = "legacy role")]
    public void A_workload_with_the_observe_role_can_observe(string role)
    {
        Assert.IsTrue(McpOperatorPermissions.CanObserve(Workload(role)));
    }

    [TestMethod]
    [DataRow("Nimbus.Workload.Classify", McpOperatorPermissions.ClassifyRole)]
    [DataRow("Nimbus.Classify", McpOperatorPermissions.ClassifyRole)]
    [DataRow("Nimbus.Workload.Resubmit", McpOperatorPermissions.ResubmitRole)]
    [DataRow("Nimbus.Resubmit", McpOperatorPermissions.ResubmitRole)]
    [DataRow("Nimbus.Workload.Annotate", McpOperatorPermissions.AnnotateRole)]
    [DataRow("Nimbus.Annotate", McpOperatorPermissions.AnnotateRole)]
    public void A_workload_holding_the_role_or_its_legacy_name_is_admitted(string granted, string required)
    {
        Assert.IsTrue(McpOperatorPermissions.IsWorkloadWithRole(Workload(granted), required));
    }

    [TestMethod]
    public void Role_values_are_matched_case_sensitively()
    {
        Assert.IsFalse(McpOperatorPermissions.CanObserve(Workload("nimbus.workload.observe")));
    }

    [TestMethod]
    public void A_role_does_not_admit_a_different_action()
    {
        Assert.IsFalse(McpOperatorPermissions.IsWorkloadWithRole(Workload("Nimbus.Workload.Observe"), McpOperatorPermissions.ResubmitRole));
    }

    private static ClaimsPrincipal Workload(string role)
        => new(new ClaimsIdentity([new Claim("roles", role)], "test"));
}
