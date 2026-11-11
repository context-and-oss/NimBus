#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// --resource-name-postfix and --func-storage-account-name: passed to the templates, recorded
/// on the resource group, read back on the next run, and checked before anything deploys.
/// </summary>
public sealed class InfrastructureDeployerNamingTests
{
    private static InfrastructureOptions Options(string? postfix = null, string? funcStorage = null) =>
        new("dis", "dev", "rg-dis-dev", postfix, Location: null, WebAppVersion: "test", FuncStorageAccountName: funcStorage);

    private static InfrastructureDeployer Deployer(RecordingAzureCliRunner azureCli) =>
        new(new CommandContext(Path.GetTempPath()), azureCli);

    private static Func<IReadOnlyList<string>, string?> RecordedTags(string tagsJson) =>
        arguments => arguments.Contains("group") && arguments.Contains("show")
            ? $$"""{"id":"{{RecordingAzureCliRunner.ResourceGroupIdPrefix}}rg-dis-dev","tags":{{tagsJson}}}"""
            : null;

    private static bool IsStorageShow(IReadOnlyList<string> arguments) =>
        arguments.Contains("resource") && arguments.Contains("show") && arguments.Contains("Microsoft.Storage/storageAccounts");

    private static bool IsCheckName(IReadOnlyList<string> arguments) => arguments.Contains("check-name");

    /// <summary>The override account does not exist yet and its name is free.</summary>
    private static RecordingAzureCliRunner NewStorageAccount(Func<IReadOnlyList<string>, string?>? responder = null) => new()
    {
        FailWhen = IsStorageShow,
        Responder = arguments => IsCheckName(arguments) ? """{"available":true,"reason":null}""" : responder?.Invoke(arguments),
    };

    private static RecordingAzureCliRunner ExistingStorageAccount(string accountJson) => new()
    {
        Responder = arguments => IsStorageShow(arguments) ? accountJson : null,
    };

    private static IReadOnlyList<string>? TagMerge(RecordingAzureCliRunner azureCli) =>
        azureCli.Commands.SingleOrDefault(command => command.Count > 1 && command[0] == "tag" && command.Contains("Merge"));

    [Fact]
    public async Task ApplyAsync_PassesThePostfixToBothTemplates()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(postfix: "AVK"), CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
        Assert.All(azureCli.Deployments, deployment => Assert.Contains("resourceNamePostfix=avk", deployment.Arguments));
    }

    /// <summary>Regression: without the option, the template parameters are exactly as before.</summary>
    [Fact]
    public async Task ApplyAsync_WithoutNamingOptionsPassesNoNamingParametersAndWritesNoTags()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None);

        Assert.All(azureCli.Deployments, deployment => Assert.DoesNotContain(
            deployment.Arguments,
            argument => argument.StartsWith("resourceNamePostfix=", StringComparison.Ordinal)
                || argument.StartsWith("funcStorage", StringComparison.Ordinal)));
        Assert.Null(TagMerge(azureCli));
    }

    [Fact]
    public async Task ApplyAsync_NoLongerIgnoresThePostfix()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(postfix: "avk"), CancellationToken.None);

        // Discovery and lookups use the postfixed names.
        Assert.Contains(azureCli.Commands, command => command.Contains("sb-dis-dev-avk"));
        Assert.Contains(azureCli.Commands, command => command.Contains("cosmos-dis-dev-avk"));
    }

    [Fact]
    public async Task ApplyAsync_RecordsThePostfixAndStorageAccountOnTheResourceGroup()
    {
        var azureCli = NewStorageAccount();

        await Deployer(azureCli).ApplyAsync(Options(postfix: "avk", funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None);

        var merge = TagMerge(azureCli);
        Assert.NotNull(merge);
        Assert.Contains($"{ResourceNaming.PostfixTag}=avk", merge);
        Assert.Contains($"{ResourceNaming.FuncStorageAccountTag}=stdisdevmsgstoreavk", merge);
    }

    [Fact]
    public async Task ApplyAsync_UsesTheRecordedPostfixWhenTheOptionIsAbsent()
    {
        var azureCli = NewStorageAccount(RecordedTags("""{"nimbus-resource-name-postfix":"avk","nimbus-func-storage-account":"stdisdevmsgstoreavk"}"""));

        await Deployer(azureCli).ApplyAsync(Options(), CancellationToken.None);

        Assert.Contains("resourceNamePostfix=avk", azureCli.Deployments[0].Arguments);
        Assert.Contains("funcStorageAccountName=stdisdevmsgstoreavk", azureCli.Deployments[0].Arguments);
        // Already recorded: nothing to write.
        Assert.Null(TagMerge(azureCli));
    }

    [Fact]
    public async Task ApplyAsync_APostfixDifferentFromTheRecordedOneFailsBeforeDeploying()
    {
        var azureCli = new RecordingAzureCliRunner { Responder = RecordedTags("""{"nimbus-resource-name-postfix":"avk"}""") };

        await Assert.ThrowsAsync<CommandException>(() => Deployer(azureCli).ApplyAsync(Options(postfix: "xyz"), CancellationToken.None));

        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }

    [Theory]
    [InlineData("a-b", null)]
    [InlineData(null, "Not-A-Storage-Name")]
    public async Task ApplyAsync_MalformedNamingOptionsFailBeforeAnyAzureCall(string? postfix, string? funcStorage)
    {
        var azureCli = new RecordingAzureCliRunner();

        await Assert.ThrowsAsync<CommandException>(() => Deployer(azureCli).ApplyAsync(Options(postfix, funcStorage), CancellationToken.None));

        Assert.Empty(azureCli.Commands);
    }

    [Fact]
    public async Task ApplyAsync_KeepsTheSkuAndKindOfAReusedStorageAccount()
    {
        var azureCli = ExistingStorageAccount("""{"sku":"Standard_GRS","kind":"Storage","networkAcls":null}""");

        await Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("funcStorageAccountName=stdisdevmsgstoreavk", core);
        Assert.Contains("funcStorageSkuName=Standard_GRS", core);
        Assert.Contains("funcStorageKind=Storage", core);
        Assert.DoesNotContain(core, argument => argument.StartsWith("funcStorageNetworkAcls=", StringComparison.Ordinal));
    }

    /// <summary>Regression: the template defaults replaced a reused account's firewall rules.</summary>
    [Fact]
    public async Task ApplyAsync_KeepsTheNetworkRulesOfAReusedStorageAccount()
    {
        const string acls = """{"defaultAction":"Deny","bypass":"AzureServices","ipRules":[{"value":"203.0.113.10","action":"Allow"}]}""";
        var azureCli = ExistingStorageAccount($$"""{"sku":"Standard_LRS","kind":"StorageV2","networkAcls":{{acls}}}""");

        await Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None);

        Assert.Contains($"funcStorageNetworkAcls={acls}", azureCli.Deployments[0].Arguments);
    }

    /// <summary>
    /// Regression: private mode leaves a reused account at Deny with no bypass. Keeping that on the
    /// way back to public, while the cleanup removes the private endpoints and VNet integration,
    /// cut the Resolver off from its storage.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_LeavingPrivateModeReopensAReusedStorageAccountFirewall()
    {
        const string locked = """{"defaultAction":"Deny","bypass":"None","ipRules":[{"value":"203.0.113.10","action":"Allow"}]}""";
        var tags = RecordedTags("""{"nimbus-network-mode":"private"}""");
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => IsStorageShow(arguments)
                ? $$"""{"sku":"Standard_LRS","kind":"StorageV2","networkAcls":{{locked}}}"""
                : tags(arguments),
        };

        await Deployer(azureCli).ApplyAsync(
            Options(funcStorage: "stdisdevmsgstoreavk") with { Network = new NetworkOptions(Mode: NetworkModeChoice.Public) },
            CancellationToken.None);

        Assert.Contains(
            """funcStorageNetworkAcls={"defaultAction":"Allow","bypass":"AzureServices","ipRules":[{"value":"203.0.113.10","action":"Allow"}]}""",
            azureCli.Deployments[0].Arguments);
    }

    [Fact]
    public void ReopenNetworkAcls_KeepsTheRulesAndOpensTheDefaults()
    {
        var reopened = InfrastructureDeployer.ReopenNetworkAcls(
            """{"defaultAction":"Deny","bypass":"None","virtualNetworkRules":[{"id":"/subnets/a","action":"Allow"}]}""");

        Assert.Equal("""{"defaultAction":"Allow","bypass":"AzureServices","virtualNetworkRules":[{"id":"/subnets/a","action":"Allow"}]}""", reopened);
    }

    [Fact]
    public async Task ApplyAsync_ANewOverrideAccountGetsTheTemplateDefaults()
    {
        var azureCli = NewStorageAccount();

        await Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevnew"), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("funcStorageAccountName=stdisdevnew", core);
        Assert.DoesNotContain(core, argument => argument.StartsWith("funcStorageSkuName=", StringComparison.Ordinal));
        Assert.DoesNotContain(core, argument => argument.StartsWith("funcStorageNetworkAcls=", StringComparison.Ordinal));
    }

    /// <summary>
    /// Regression: any failed read was taken as "the account does not exist", so a throttled call or
    /// an account in another resource group got the template defaults (or a late name conflict).
    /// </summary>
    [Theory]
    [InlineData("""{"available":false,"reason":"AlreadyExists"}""", "AlreadyExists")]
    [InlineData(null, "Could not tell")]
    public async Task ApplyAsync_AnUnreadableStorageAccountFailsBeforeRecordingOrDeploying(string? checkNameOutput, string expected)
    {
        var azureCli = new RecordingAzureCliRunner
        {
            FailWhen = arguments => IsStorageShow(arguments) || (checkNameOutput is null && IsCheckName(arguments)),
            Responder = arguments => IsCheckName(arguments) ? checkNameOutput : null,
        };

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None));

        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }

    [Theory]
    [InlineData("Premium_LRS")]
    [InlineData("Premium_ZRS")]
    public async Task ApplyAsync_APremiumStorageAccountFailsBeforeRecordingOrDeploying(string sku)
    {
        var azureCli = ExistingStorageAccount($$"""{"sku":"{{sku}}","kind":"StorageV2"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevpremium"), CancellationToken.None));

        Assert.Contains(sku, error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }

    /// <summary>
    /// Regression: before this option took effect, a postfix was ignored and the un-postfixed names
    /// deployed. The first run that honours it must not build a second set next to them.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_AFirstPostfixNextToAnUnpostfixedDeploymentFailsBeforeDeploying()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("resource") && arguments.Contains("list")
                ? """[{"name":"func-dis-dev-resolver","location":"westeurope"},{"name":"webapp-dis-dev-management","location":"westeurope"}]"""
                : null,
        };

        await Assert.ThrowsAsync<CommandException>(() => Deployer(azureCli).ApplyAsync(Options(postfix: "avk"), CancellationToken.None));

        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }

    [Fact]
    public async Task ApplyAsync_ARecordedPostfixSkipsTheParallelDeploymentGuard()
    {
        var tags = RecordedTags("""{"nimbus-resource-name-postfix":"avk"}""");
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("resource") && arguments.Contains("list")
                ? """[{"name":"func-dis-dev-resolver","location":"westeurope"}]"""
                : tags(arguments),
        };

        await Deployer(azureCli).ApplyAsync(Options(postfix: "avk"), CancellationToken.None);

        Assert.Equal(2, azureCli.Deployments.Count);
    }

    [Theory]
    [InlineData("BlobStorage")]
    [InlineData("FileStorage")]
    [InlineData("BlockBlobStorage")]
    public async Task ApplyAsync_AStorageAccountThatCannotHostFunctionsFailsBeforeRecordingOrDeploying(string kind)
    {
        var azureCli = ExistingStorageAccount($$"""{"sku":"Standard_LRS","kind":"{{kind}}"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevblob"), CancellationToken.None));

        Assert.Contains(kind, error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }
}
