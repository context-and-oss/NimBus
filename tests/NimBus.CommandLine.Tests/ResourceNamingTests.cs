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

    private static readonly ResourceNaming.DerivedNameScope CosmosOnFlex = new(UsesCosmos: true, ProvisionsSqlServer: false, CreatesResolverContentShare: false);

    /// <summary>Regression: these passed validation and produced a Service Bus name Azure rejects.</summary>
    [Theory]
    [InlineData("sb")]
    [InlineData("-SB")]
    [InlineData("mgmt")]
    public void ValidateDerivedNames_RejectsSuffixesServiceBusForbids(string input)
    {
        var names = NamingConventions.Build("dis", "dev", null, input);

        var error = Assert.Throws<CommandException>(() => ResourceNaming.ValidateDerivedNames(names, null, null, CosmosOnFlex));

        Assert.Contains("Service Bus", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Regression: the reserved suffixes were rejected even when the namespace name was overridden.</summary>
    [Theory]
    [InlineData("sb")]
    [InlineData("mgmt")]
    public void ValidateDerivedNames_AcceptsReservedSuffixesWithANamespaceOverride(string postfix)
    {
        Assert.Equal(postfix, ResourceNaming.NormalizePostfix(postfix));
        var names = NamingConventions.Build("dis", "dev", "sb-custom-name", postfix);

        ResourceNaming.ValidateDerivedNames(names, "sb-custom-name", null, CosmosOnFlex);
    }

    /// <summary>Regression: the Cosmos limit rejected SQL deployments, which create no Cosmos account.</summary>
    [Fact]
    public void ValidateDerivedNames_ChecksTheCosmosNameOnlyForCosmosDeployments()
    {
        // cosmos-dis-dev-{30} is 45 characters; the SQL, Service Bus and app names fit.
        var names = NamingConventions.Build("dis", "dev", null, new string('a', 30));

        Assert.Throws<CommandException>(() => ResourceNaming.ValidateDerivedNames(names, null, null, CosmosOnFlex));
        ResourceNaming.ValidateDerivedNames(names, null, null, new(UsesCosmos: false, ProvisionsSqlServer: true, CreatesResolverContentShare: false));
    }

    /// <summary>
    /// Regression: a new Elastic Premium Resolver's content share is its name plus 13 characters,
    /// so a 51-character app name made a 64-character share Azure Files rejects.
    /// </summary>
    [Fact]
    public void ValidateDerivedNames_LeavesRoomForANewContentShare()
    {
        // func-dis-dev-resolver-{29} is 51 characters.
        var names = NamingConventions.Build("dis", "dev", null, new string('a', 29));
        Assert.Equal(51, names.ResolverFunctionAppName.Length);
        var sqlScope = new ResourceNaming.DerivedNameScope(UsesCosmos: false, ProvisionsSqlServer: true, CreatesResolverContentShare: false);

        var error = Assert.Throws<CommandException>(() => ResourceNaming.ValidateDerivedNames(names, null, null, sqlScope with { CreatesResolverContentShare = true }));
        Assert.Contains("Resolver function app", error.Message, StringComparison.Ordinal);
        ResourceNaming.ValidateDerivedNames(names, null, null, sqlScope);
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

    /// <summary>Regression: only un-postfixed resources were checked, so 'avx' next to an untagged 'avk' deployment passed.</summary>
    [Fact]
    public void GuardAgainstParallelDeployment_ADifferentPostfixNextToAPostfixedDeploymentNamesTheExistingOne()
    {
        var target = NamingConventions.Build("dis", "dev", serviceBusNamespaceName: null, resourceNamePostfix: "avx");

        var error = Assert.Throws<CommandException>(() => ResourceNaming.GuardAgainstParallelDeployment(
            target, new[] { "func-dis-dev-resolver-avk", "webapp-dis-dev-management-avk" }, "rg-dis-dev"));

        Assert.Contains("--resource-name-postfix avk", error.Message, StringComparison.Ordinal);
        Assert.Contains("'avx'", error.Message, StringComparison.Ordinal);
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
    public void ValidateDerivedNames_FailsWhenThePostfixMakesANameTooLong()
    {
        // cosmos-{s}-{e}-{p} must stay within 44 characters.
        var names = NamingConventions.Build("averylongsolutionid", "production", null, "postfixvalue12");

        var error = Assert.Throws<CommandException>(() => ResourceNaming.ValidateDerivedNames(names, null, null, CosmosOnFlex));

        Assert.Contains("Cosmos DB account", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateDerivedNames_IgnoresNamesWithoutAPostfix()
    {
        ResourceNaming.ValidateDerivedNames(NamingConventions.Build("averylongsolutionidthatgoesonandon", "production"), null, null, CosmosOnFlex);
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
