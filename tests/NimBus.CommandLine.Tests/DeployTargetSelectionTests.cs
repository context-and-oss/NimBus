#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class DeployTargetSelectionTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseOnlyOption_DefaultsToAllForAbsentValue(string? value)
    {
        Assert.Equal(AppDeploymentTarget.All, DeployTargetSelection.ParseOnlyOption(value));
    }

    [Theory]
    [InlineData("resolver", nameof(AppDeploymentTarget.Resolver))]
    [InlineData("Resolver", nameof(AppDeploymentTarget.Resolver))]
    [InlineData("webapp", nameof(AppDeploymentTarget.WebApp))]
    [InlineData("WebApp", nameof(AppDeploymentTarget.WebApp))]
    [InlineData("web-app", nameof(AppDeploymentTarget.WebApp))]
    [InlineData("all", nameof(AppDeploymentTarget.All))]
    public void ParseOnlyOption_ParsesKnownValues(string value, string expectedName)
    {
        var expected = Enum.Parse<AppDeploymentTarget>(expectedName);
        Assert.Equal(expected, DeployTargetSelection.ParseOnlyOption(value));
    }

    [Fact]
    public void ParseOnlyOption_ThrowsOnUnknownValue()
    {
        Assert.Throws<CommandException>(() => DeployTargetSelection.ParseOnlyOption("frontend"));
    }
}
