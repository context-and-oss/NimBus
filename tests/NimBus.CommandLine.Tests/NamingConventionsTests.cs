#pragma warning disable CA1707, CA2007
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

public class NamingConventionsTests
{
    [Theory]
    [InlineData("MyApp", "myapp")]
    [InlineData("  MyApp  ", "myapp")]
    [InlineData("My-App_123", "myapp123")]
    [InlineData("ABC", "abc")]
    [InlineData("123", "123")]
    public void NormalizePart_ReturnsLowercaseAlphanumeric(string input, string expected)
    {
        var result = NamingConventions.NormalizePart(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NormalizePart_ThrowsOnEmptyOrWhitespace(string? input)
    {
        Assert.Throws<CommandException>(() => NamingConventions.NormalizePart(input!));
    }

    [Fact]
    public void NormalizePart_ThrowsWhenNoAlphanumericCharactersRemain()
    {
        Assert.Throws<CommandException>(() => NamingConventions.NormalizePart("---"));
    }

    [Fact]
    public void Build_ProducesCorrectDeploymentNames()
    {
        var names = NamingConventions.Build("NimBus", "Dev");

        Assert.Equal("nimbus", names.SolutionId);
        Assert.Equal("dev", names.Environment);
        Assert.Equal("sb-nimbus-dev", names.ServiceBusNamespace);
        Assert.Equal("ai-nimbus-dev-global-tracelog", names.AppInsightsName);
        Assert.Equal("cosmos-nimbus-dev", names.CosmosAccountName);
        Assert.Equal("func-nimbus-dev-resolver", names.ResolverFunctionAppName);
        Assert.Equal("webapp-nimbus-dev-management", names.WebAppName);
    }

    [Fact]
    public void Build_NormalizesInputsBeforeBuildingNames()
    {
        var names = NamingConventions.Build("My-Solution", "Prod-01");

        Assert.Equal("mysolution", names.SolutionId);
        Assert.Equal("prod01", names.Environment);
        Assert.Equal("sb-mysolution-prod01", names.ServiceBusNamespace);
    }
}
