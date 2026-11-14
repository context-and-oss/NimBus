#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// 'nb topology apply' addresses the namespace 'nb infra apply' deployed: an explicit
/// override wins, otherwise the override recorded on the resource group, otherwise the
/// derived name. The resource-name postfix follows the same rule.
/// </summary>
public sealed class TopologyNamespaceResolutionTests
{
    private static RecordingAzureCliRunner WithTags(string tagsJson) => new()
    {
        Responder = arguments => arguments.Contains("group")
            ? $$"""{"id":"{{RecordingAzureCliRunner.ResourceGroupIdPrefix}}rg-nimbus-dev","tags":{{tagsJson}}}"""
            : null,
    };

    /// <summary>
    /// Regression: a failed tag read counted as "no tags", so a postfixed deployment's apps were
    /// addressed by their un-postfixed names.
    /// </summary>
    [Fact]
    public async Task ResolveNamesAsync_AFailedTagReadStopsTheCommand()
    {
        var azureCli = new RecordingAzureCliRunner { FailWhen = arguments => arguments.Contains("group") };

        await Assert.ThrowsAsync<CommandException>(() => ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveNamesAsync_FollowsTheRecordedOverride()
    {
        var azureCli = WithTags("""{"nimbus-service-bus-namespace":"sb-nimbus-dev-premium"}""");

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None);

        Assert.Equal("sb-nimbus-dev-premium", names.ServiceBusNamespace);
    }

    [Fact]
    public async Task ResolveNamesAsync_ExplicitOverrideWinsOverTheRecordedOne()
    {
        var azureCli = WithTags("""{"nimbus-service-bus-namespace":"sb-recorded"}""");

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli,
            new TopologyOptions("nimbus", "dev", "rg-nimbus-dev", "sb-explicit-name"),
            CancellationToken.None);

        Assert.Equal("sb-explicit-name", names.ServiceBusNamespace);
    }

    [Fact]
    public async Task ResolveNamesAsync_ExplicitNamespaceAndPostfixWinOverTheRecordedNamespace()
    {
        var azureCli = WithTags("""{"nimbus-service-bus-namespace":"sb-recorded","nimbus-resource-name-postfix":"avk"}""");

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli,
            new TopologyOptions("nimbus", "dev", "rg-nimbus-dev", "sb-explicit-name", ResourceNamePostfix: "avk"),
            CancellationToken.None);

        Assert.Equal("sb-explicit-name", names.ServiceBusNamespace);
        Assert.Equal("cosmos-nimbus-dev-avk", names.CosmosAccountName);
    }

    /// <summary>Regression: with both options explicit the tags were never read, so a mistyped postfix went unnoticed.</summary>
    [Fact]
    public async Task ResolveNamesAsync_ExplicitNamespaceDoesNotSkipThePostfixCheck()
    {
        var azureCli = WithTags("""{"nimbus-resource-name-postfix":"avk"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() => ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli,
            new TopologyOptions("nimbus", "dev", "rg-nimbus-dev", "sb-explicit-name", ResourceNamePostfix: "avx"),
            CancellationToken.None));

        Assert.Contains("'avk'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveNamesAsync_WithoutARecordKeepsTheDerivedName()
    {
        var azureCli = WithTags("null");

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None);

        Assert.Equal("sb-nimbus-dev", names.ServiceBusNamespace);
        Assert.Null(names.ResourceNamePostfix);
    }

    [Fact]
    public async Task ResolveNamesAsync_FollowsTheRecordedPostfix()
    {
        var azureCli = WithTags("""{"nimbus-resource-name-postfix":"avk"}""");

        var names = await ServiceBusTopologyProvisioner.ResolveNamesAsync(azureCli, new TopologyOptions("nimbus", "dev", "rg-nimbus-dev"), CancellationToken.None);

        Assert.Equal("sb-nimbus-dev-avk", names.ServiceBusNamespace);
        Assert.Equal("avk", names.ResourceNamePostfix);
    }

    [Fact]
    public async Task ResolveNamesAsync_PostfixDifferentFromTheRecordedOneFails()
    {
        var azureCli = WithTags("""{"nimbus-resource-name-postfix":"avk"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() => ServiceBusTopologyProvisioner.ResolveNamesAsync(
            azureCli,
            new TopologyOptions("nimbus", "dev", "rg-nimbus-dev", ResourceNamePostfix: "other"),
            CancellationToken.None));

        Assert.Contains("'avk'", error.Message, StringComparison.Ordinal);
    }
}
