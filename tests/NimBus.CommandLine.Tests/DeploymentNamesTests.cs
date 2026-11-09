#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class DeploymentNamesTests
{
    [Fact]
    public void RecordEquality_WorksForIdenticalValues()
    {
        var a = new DeploymentNames(
            "sol", "env",
            "sb-sol-env", "ai-sol-env-global-tracelog", "cosmos-sol-env",
            "sql-sol-env", "stsolenvfunc",
            "asp-sol-env-management", "asp-sol-env-core",
            "func-sol-env-resolver", "webapp-sol-env-management");
        var b = new DeploymentNames(
            "sol", "env",
            "sb-sol-env", "ai-sol-env-global-tracelog", "cosmos-sol-env",
            "sql-sol-env", "stsolenvfunc",
            "asp-sol-env-management", "asp-sol-env-core",
            "func-sol-env-resolver", "webapp-sol-env-management");

        Assert.Equal(a, b);
    }

    [Fact]
    public void RecordEquality_DiffersForDifferentValues()
    {
        var a = new DeploymentNames(
            "sol", "dev",
            "sb-sol-dev", "ai-sol-dev-global-tracelog", "cosmos-sol-dev",
            "sql-sol-dev", "stsoldevfunc",
            "asp-sol-dev-management", "asp-sol-dev-core",
            "func-sol-dev-resolver", "webapp-sol-dev-management");
        var b = new DeploymentNames(
            "sol", "prod",
            "sb-sol-prod", "ai-sol-prod-global-tracelog", "cosmos-sol-prod",
            "sql-sol-prod", "stsolprodfunc",
            "asp-sol-prod-management", "asp-sol-prod-core",
            "func-sol-prod-resolver", "webapp-sol-prod-management");

        Assert.NotEqual(a, b);
    }
}
