#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// The legacy naming options: the postfix goes only where the legacy pipeline put it, and the
/// function storage account can be pointed at an existing account.
/// </summary>
public sealed class ResourceNamingTests
{
    /// <summary>
    /// Pins the names the legacy templates produced with postfix 'avk', so deploying with the
    /// postfix matches those resources instead of creating a parallel set.
    /// </summary>
    [Fact]
    public void Build_WithPostfixMatchesTheLegacyNames()
    {
        var names = NamingConventions.Build("dis", "dev", serviceBusNamespaceName: null, resourceNamePostfix: "avk");

        Assert.Equal("sb-dis-dev-avk", names.ServiceBusNamespace);
        Assert.Equal("cosmos-dis-dev-avk", names.CosmosAccountName);
        Assert.Equal("sql-dis-dev-avk", names.SqlServerName);
        Assert.Equal("func-dis-dev-resolver-avk", names.ResolverFunctionAppName);
        Assert.Equal("webapp-dis-dev-management-avk", names.WebAppName);
        Assert.Equal("avk", names.ResourceNamePostfix);
    }

    /// <summary>
    /// The legacy templates never postfixed App Insights or the App Service plans, and the
    /// function storage account has its own override. Postfixing them would miss the existing ones.
    /// </summary>
    [Fact]
    public void Build_WithPostfixLeavesAppInsightsPlansAndStorageAlone()
    {
        var names = NamingConventions.Build("dis", "dev", serviceBusNamespaceName: null, resourceNamePostfix: "avk");

        Assert.Equal("ai-dis-dev-global-tracelog", names.AppInsightsName);
        Assert.Equal("asp-dis-dev-management", names.ManagementAppServicePlanName);
        Assert.Equal("asp-dis-dev-core", names.CoreAppServicePlanName);
        Assert.Equal("stdisdevfunc", names.FuncStorageAccountName);
    }

    [Fact]
    public void Build_WithoutPostfixKeepsTheCurrentNames()
    {
        Assert.Equal(NamingConventions.Build("nimbus", "dev"), NamingConventions.Build("nimbus", "dev", null, null, null));
        Assert.Null(NamingConventions.Build("nimbus", "dev").ResourceNamePostfix);
    }

    [Fact]
    public void Build_NamespaceOverrideBeatsThePostfix()
    {
        var names = NamingConventions.Build("dis", "dev", "sb-custom-name", "avk");

        Assert.Equal("sb-custom-name", names.ServiceBusNamespace);
        Assert.Equal("cosmos-dis-dev-avk", names.CosmosAccountName);
    }

    [Fact]
    public void Build_FuncStorageOverrideReplacesTheDerivedAccount()
    {
        var names = NamingConventions.Build("dis", "dev", null, "avk", "stdisdevmsgstoreavk");

        Assert.Equal("stdisdevmsgstoreavk", names.FuncStorageAccountName);
    }

    [Theory]
    [InlineData("avk", "avk")]
    [InlineData(" AVK ", "avk")]
    [InlineData("-avk", "avk")]
    [InlineData("01", "01")]
    public void NormalizePostfix_TrimsLowercasesAndDropsALeadingDash(string input, string expected)
    {
        Assert.Equal(expected, ResourceNaming.NormalizePostfix(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NormalizePostfix_BlankMeansNone(string? input)
    {
        Assert.Null(ResourceNaming.NormalizePostfix(input));
    }

    [Theory]
    [InlineData("a-b")]
    [InlineData("a_b")]
    [InlineData("--avk")]
    [InlineData("-")]
    public void NormalizePostfix_RejectsAnythingButLettersAndDigits(string input)
    {
        Assert.Throws<CommandException>(() => ResourceNaming.NormalizePostfix(input));
    }

    /// <summary>Regression: these passed validation and produced a Service Bus name Azure rejects.</summary>
    [Theory]
    [InlineData("sb")]
    [InlineData("-SB")]
    [InlineData("mgmt")]
    public void NormalizePostfix_RejectsSuffixesServiceBusForbids(string input)
    {
        var error = Assert.Throws<CommandException>(() => ResourceNaming.NormalizePostfix(input));

        Assert.Contains("Service Bus", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardAgainstParallelDeployment_APostfixNextToAnUnpostfixedDeploymentFails()
    {
        var target = NamingConventions.Build("dis", "dev", serviceBusNamespaceName: null, resourceNamePostfix: "avk");

        var error = Assert.Throws<CommandException>(() => ResourceNaming.GuardAgainstParallelDeployment(
            target, new[] { "func-dis-dev-resolver", "webapp-dis-dev-management", "asp-dis-dev-core" }, "rg-dis-dev"));

        Assert.Contains("func-dis-dev-resolver", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GuardAgainstParallelDeployment_NoPostfixNextToAPostfixedDeploymentNamesThePostfix()
    {
        var target = NamingConventions.Build("dis", "dev");

        var error = Assert.Throws<CommandException>(() => ResourceNaming.GuardAgainstParallelDeployment(
            target, new[] { "func-dis-dev-resolver-avk", "webapp-dis-dev-management-avk" }, "rg-dis-dev"));

        Assert.Contains("--resource-name-postfix avk", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("avk", new[] { "func-dis-dev-resolver-avk", "func-dis-dev-resolver" })]
    [InlineData(null, new[] { "webapp-dis-dev-management", "webapp-dis-dev-management-avk" })]
    [InlineData("avk", new string[0])]
    [InlineData(null, new[] { "asp-dis-dev-core", "stdisdevfunc" })]
    public void GuardAgainstParallelDeployment_PassesForAMatchingOrFreshDeployment(string? postfix, string[] existing)
    {
        var target = NamingConventions.Build("dis", "dev", serviceBusNamespaceName: null, resourceNamePostfix: postfix);

        ResourceNaming.GuardAgainstParallelDeployment(target, existing, "rg-dis-dev");
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("StDisDev")]
    [InlineData("st-dis-dev")]
    [InlineData("stdisdevmsgstoreavk123456")] // 25 characters
    public void NormalizeFuncStorageAccountName_RejectsInvalidNames(string input)
    {
        Assert.Throws<CommandException>(() => ResourceNaming.NormalizeFuncStorageAccountName(input));
    }

    [Fact]
    public void NormalizeFuncStorageAccountName_AcceptsAValidName()
    {
        Assert.Equal("stdisdevmsgstoreavk", ResourceNaming.NormalizeFuncStorageAccountName(" stdisdevmsgstoreavk "));
    }

    [Theory]
    [InlineData("avk", null, "avk")]
    [InlineData(null, "avk", "avk")]
    [InlineData("avk", "avk", "avk")]
    [InlineData(null, null, null)]
    public void Resolve_ExplicitOrRecordedValueWins(string? explicitValue, string? recorded, string? expected)
    {
        Assert.Equal(expected, ResourceNaming.Resolve(explicitValue, recorded, "--resource-name-postfix", ResourceNaming.PostfixTag, "rg"));
    }

    [Fact]
    public void Resolve_DifferentExplicitAndRecordedValuesFail()
    {
        var error = Assert.Throws<CommandException>(() =>
            ResourceNaming.Resolve("new", "avk", "--resource-name-postfix", ResourceNaming.PostfixTag, "rg-dis-dev"));

        Assert.Contains(ResourceNaming.PostfixTag, error.Message, StringComparison.Ordinal);
        Assert.Contains("'avk'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLengths_FailsWhenThePostfixMakesANameTooLong()
    {
        // cosmos-{s}-{e}-{p} must stay within 44 characters.
        var names = NamingConventions.Build("averylongsolutionid", "production", null, "postfixvalue12");

        var error = Assert.Throws<CommandException>(() => ResourceNaming.ValidateLengths(names, null, null));

        Assert.Contains("Cosmos DB account", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateLengths_IgnoresNamesWithoutAPostfix()
    {
        ResourceNaming.ValidateLengths(NamingConventions.Build("averylongsolutionidthatgoesonandon", "production"), null, null);
    }

    [Fact]
    public void DescribePostfix_ListsWhatIsAndIsNotPostfixed()
    {
        var names = NamingConventions.Build("dis", "dev", null, "avk");

        var description = ResourceNaming.DescribePostfix(names, new[] { names.ServiceBusNamespace, names.WebAppName });

        Assert.Equal(
            "Resource-name postfix 'avk' applies to: sb-dis-dev-avk, webapp-dis-dev-management-avk. " +
            "Not postfixed: ai-dis-dev-global-tracelog, asp-dis-dev-management, asp-dis-dev-core, stdisdevfunc.",
            description);
    }

    [Fact]
    public void PostfixOptionDescription_KeepsTheCompatibilityNoteAndNamesThePostfixedResources()
    {
        var description = ResourceNaming.PostfixOptionDescription;

        Assert.Contains("compatibility with the legacy pipeline scripts", description, StringComparison.Ordinal);
        Assert.Contains("Service Bus namespace, Cosmos DB account, SQL server, Resolver function app and management WebApp names only", description, StringComparison.Ordinal);
        Assert.Contains("App Insights, the App Service plans and the function storage account are not postfixed", description, StringComparison.Ordinal);
    }

    [Fact]
    public void OwnerValue_CarriesThePostfixLikeTheTemplates()
    {
        Assert.Equal("dis-dev-avk", PrivateNetworkCleanup.OwnerValue(NamingConventions.Build("dis", "dev", null, "avk")));
        Assert.Equal("dis-dev", PrivateNetworkCleanup.OwnerValue(NamingConventions.Build("dis", "dev")));
    }
}
