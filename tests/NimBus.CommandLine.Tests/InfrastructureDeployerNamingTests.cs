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
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(postfix: "avk", funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None);

        var merge = TagMerge(azureCli);
        Assert.NotNull(merge);
        Assert.Contains($"{ResourceNaming.PostfixTag}=avk", merge);
        Assert.Contains($"{ResourceNaming.FuncStorageAccountTag}=stdisdevmsgstoreavk", merge);
    }

    [Fact]
    public async Task ApplyAsync_UsesTheRecordedPostfixWhenTheOptionIsAbsent()
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = RecordedTags("""{"nimbus-resource-name-postfix":"avk","nimbus-func-storage-account":"stdisdevmsgstoreavk"}"""),
        };

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
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("storage") && arguments.Contains("show")
                ? """{"sku":"Standard_GRS","kind":"Storage"}"""
                : null,
        };

        await Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevmsgstoreavk"), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("funcStorageAccountName=stdisdevmsgstoreavk", core);
        Assert.Contains("funcStorageSkuName=Standard_GRS", core);
        Assert.Contains("funcStorageKind=Storage", core);
    }

    [Fact]
    public async Task ApplyAsync_ANewOverrideAccountGetsTheTemplateDefaults()
    {
        var azureCli = new RecordingAzureCliRunner();

        await Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevnew"), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("funcStorageAccountName=stdisdevnew", core);
        Assert.DoesNotContain(core, argument => argument.StartsWith("funcStorageSkuName=", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("BlobStorage")]
    [InlineData("FileStorage")]
    [InlineData("BlockBlobStorage")]
    public async Task ApplyAsync_AStorageAccountThatCannotHostFunctionsFailsBeforeRecordingOrDeploying(string kind)
    {
        var azureCli = new RecordingAzureCliRunner
        {
            Responder = arguments => arguments.Contains("storage") && arguments.Contains("show")
                ? $$"""{"sku":"Standard_LRS","kind":"{{kind}}"}"""
                : null,
        };

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(funcStorage: "stdisdevblob"), CancellationToken.None));

        Assert.Contains(kind, error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.Null(TagMerge(azureCli));
    }
}
