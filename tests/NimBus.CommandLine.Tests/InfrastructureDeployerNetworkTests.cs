#pragma warning disable CA1707, CA2007

using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// How the private networking options (spec 034) reach the two Bicep deployments, and what
/// the deployer refuses before it deploys anything.
/// </summary>
public sealed class InfrastructureDeployerNetworkTests
{
    private const string Vnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-nimbus";
    private const string EndpointVnet = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-net/providers/Microsoft.Network/virtualNetworks/vnet-endpoints";
    private const string PeSubnet = EndpointVnet + "/subnets/snet-private-endpoints";
    private const string ResolverSubnet = Vnet + "/subnets/snet-resolver";
    private const string WebAppSubnet = Vnet + "/subnets/snet-management";
    private const string DnsScope = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg-hub-dns";

    private static readonly NetworkOptions PrivateNetwork = new(
        Mode: NetworkModeChoice.Private,
        PrivateEndpointSubnetId: PeSubnet,
        ResolverSubnetId: ResolverSubnet,
        WebAppSubnetId: WebAppSubnet,
        DnsMode: PrivateDnsModeChoice.Existing,
        DnsZoneScope: DnsScope,
        MonitorPrivateLink: MonitorPrivateLinkChoice.None);

    [Fact]
    public async Task ApplyAsync_PublicModePassesNoNetworkParameters()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        foreach (var deployment in azureCli.Deployments)
        {
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("networkMode=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("privateEndpointSubnetId=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("serviceBusSku=", StringComparison.Ordinal));
            Assert.DoesNotContain(deployment.Arguments, a => a.StartsWith("serviceBusNamespaceName=", StringComparison.Ordinal));
        }

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("subnet", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_PrivateModePassesTheNetworkToBothDeployments()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("networkMode=private", core);
        Assert.Contains("allowPublicAccess=false", core);
        Assert.Contains($"privateEndpointSubnetId={PeSubnet}", core);
        Assert.Contains($"resolverSubnetId={ResolverSubnet}", core);
        Assert.Contains("privateEndpointLocation=swedencentral", core);
        Assert.Contains("privateDnsMode=existing", core);
        Assert.Contains($"privateDnsZoneScope={DnsScope}", core);
        Assert.Contains("serviceBusSku=Premium", core);
        Assert.Contains("serviceBusCapacity=1", core);

        var webApp = azureCli.Deployments[1].Arguments;
        Assert.Contains("networkMode=private", webApp);
        Assert.Contains($"managementSubnetId={WebAppSubnet}", webApp);
        Assert.Contains($"privateEndpointSubnetId={PeSubnet}", webApp);
        Assert.Contains("privateDnsMode=existing", webApp);
        Assert.DoesNotContain(webApp, a => a.StartsWith("resolverSubnetId=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_TransitionKeepsPublicAccessOn()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { AllowPublicAccess = true }), CancellationToken.None);

        Assert.Contains("allowPublicAccess=true", azureCli.Deployments[0].Arguments);
        Assert.Contains("allowPublicAccess=true", azureCli.Deployments[1].Arguments);
    }

    [Fact]
    public async Task ApplyAsync_CreateModePassesLinkedVnetsAsAJsonArray()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);
        var network = PrivateNetwork with
        {
            DnsMode = PrivateDnsModeChoice.Create,
            DnsZoneScope = null,
            DnsLinkVnetIds = new[] { Vnet },
        };

        await Deployer(azureCli).ApplyAsync(Options(network), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains($"privateDnsLinkVnetIds=[\"{Vnet}\"]", core);
        Assert.DoesNotContain(core, a => a.StartsWith("privateDnsZoneScope=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RefusesPrivateModeOnAStandardNamespaceBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Standard","capacity":1,"publicNetworkAccess":"Enabled"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None));

        Assert.Contains("--service-bus-namespace-name", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_KeepsAnExistingPremiumNamespaceInPublicMode()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":2,"publicNetworkAccess":"Enabled"}""");

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.Contains("serviceBusSku=Premium", azureCli.Deployments[0].Arguments);
        Assert.Contains("serviceBusCapacity=2", azureCli.Deployments[0].Arguments);
    }

    [Fact]
    public async Task ApplyAsync_WillNotReopenALockedNamespaceWithoutAnExplicitMode()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None));

        Assert.Contains("--network-mode", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_NamespaceOverrideReachesBothDeploymentsAndTheFqdn()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(
            Options(PrivateNetwork) with { ServiceBusNamespaceName = "sb-nimbus-dev-premium" },
            CancellationToken.None);

        Assert.Contains("serviceBusNamespaceName=sb-nimbus-dev-premium", azureCli.Deployments[0].Arguments);
        Assert.Contains("serviceBusNamespaceName=sb-nimbus-dev-premium", azureCli.Deployments[1].Arguments);
        Assert.Contains("serviceBusFullyQualifiedNamespace=sb-nimbus-dev-premium.servicebus.windows.net", azureCli.Deployments[1].Arguments);
        Assert.Contains(azureCli.Commands, c => c.Contains("servicebus", StringComparer.Ordinal) && c.Contains("sb-nimbus-dev-premium", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RejectsAWronglyDelegatedResolverSubnetBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null, resolverDelegation: "Microsoft.Web/serverFarms");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None));

        Assert.Contains("Microsoft.App/environments", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    /// <summary>
    /// Without --location, new apps go to the resource group's region (the templates'
    /// default), so the subnets are checked against that region, not a fixed West Europe.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_WithoutLocationChecksSubnetsAgainstTheResourceGroupRegion()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);
        var network = azureCli.Responder!;
        azureCli.Responder = arguments =>
            arguments.Contains("group") && arguments.Contains("show") && arguments.Any(a => a.Contains("location", StringComparison.Ordinal))
                ? $$"""{"id":"{{RecordingAzureCliRunner.ResourceGroupIdPrefix}}rg-nimbus-dev","location":"swedencentral","tags":null}"""
                : network(arguments);

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork) with { Location = null }, CancellationToken.None));

        Assert.Contains("the app it serves is in swedencentral", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
    }

    /// <summary>Malformed or contradictory values fail before login or any Azure call.</summary>
    [Fact]
    public async Task ApplyAsync_MalformedNetworkOptionsFailBeforeAnyAzureCall()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { ResolverSubnetId = "snet-resolver" }), CancellationToken.None));

        Assert.Empty(azureCli.Commands);
    }

    /// <summary>Completeness is judged after the recorded setup is merged in, still before deploying.</summary>
    [Fact]
    public async Task ApplyAsync_IncompletePrivateOptionsFailBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { DnsMode = null, DnsZoneScope = null }), CancellationToken.None));

        Assert.Contains("--private-dns", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_PublicDeploymentWithoutHistoryWritesNoTags()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RecordsTheSetupBeforeDeploying()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None);

        var tagIndex = azureCli.Commands.FindIndex(c => c.Contains("tag", StringComparer.Ordinal) && c.Contains("Merge", StringComparer.Ordinal));
        var deployIndex = azureCli.Commands.FindIndex(c => c.Contains("deployment", StringComparer.Ordinal));
        Assert.InRange(tagIndex, 0, deployIndex - 1);

        var tags = azureCli.Commands[tagIndex];
        Assert.Contains($"--resource-id", tags);
        Assert.Contains($"{RecordingAzureCliRunner.ResourceGroupIdPrefix}rg-nimbus-dev", tags);
        Assert.Contains("nimbus-network-mode=private", tags);
        Assert.Contains($"nimbus-network-resolver-subnet={ResolverSubnet}", tags);
        Assert.Contains("nimbus-network-dns=existing", tags);
        Assert.Contains($"nimbus-network-dns-zone-scope={DnsScope}", tags);
        Assert.Contains("nimbus-network-monitor=none", tags);
    }

    /// <summary>A rerun without flags after pass 1 stays in the transition.</summary>
    [Fact]
    public async Task ApplyAsync_RerunWithoutFlagsReproducesTheRecordedTransition()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork with { AllowPublicAccess = true }, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        var core = azureCli.Deployments[0].Arguments;
        Assert.Contains("networkMode=private", core);
        Assert.Contains("allowPublicAccess=true", core);
        Assert.Contains($"resolverSubnetId={ResolverSubnet}", core);
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal));
    }

    /// <summary>Pass 2: the mode alone locks the deployment with the recorded subnets.</summary>
    [Fact]
    public async Task ApplyAsync_ExplicitPrivateModeEndsTheRecordedTransition()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork with { AllowPublicAccess = true }, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli, resolvesPrivately: true).ApplyAsync(Options(new NetworkOptions(Mode: NetworkModeChoice.Private)), CancellationToken.None);

        Assert.Contains("allowPublicAccess=false", azureCli.Deployments[0].Arguments);
        Assert.Contains(azureCli.Commands, c => c.Contains("Merge", StringComparer.Ordinal) && c.Contains("nimbus-network-mode=private", StringComparer.Ordinal));
        Assert.Contains(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal) && c.Contains("pe-sb-nimbus-dev-namespace", StringComparer.Ordinal));
    }

    /// <summary>
    /// A runner that cannot resolve the private endpoints must not lock the deployment: it
    /// would lock itself out of the topology and app deployment steps that follow.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_DoesNotLockWhenThisMachineCannotResolveTheEndpoints()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork with { AllowPublicAccess = true }, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""",
            recordedTags: recorded);

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli, resolvesPrivately: false).ApplyAsync(
                Options(new NetworkOptions(Mode: NetworkModeChoice.Private)) with { DnsWait = TimeSpan.Zero },
                CancellationToken.None));

        Assert.Contains("turning public access off", error.Message, StringComparison.Ordinal);
        Assert.Contains("sb-nimbus-dev.servicebus.windows.net", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RefusesADirectSwitchOfAnExistingPublicDeployment()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""");

        var error = await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(PrivateNetwork), CancellationToken.None));

        Assert.Contains("--skip-transition", error.Message, StringComparison.Ordinal);
        Assert.Empty(azureCli.Deployments);
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_SkipTransitionAllowsTheDirectSwitch()
    {
        var azureCli = CustomerNetwork(existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""");

        await Deployer(azureCli).ApplyAsync(Options(PrivateNetwork with { SkipTransition = true }), CancellationToken.None);

        Assert.Contains("allowPublicAccess=false", azureCli.Deployments[0].Arguments);
    }

    /// <summary>The namespace is locked but the record says transition: never silently reopen.</summary>
    [Fact]
    public async Task ApplyAsync_StopsWhenTheNamespaceIsMoreLockedThanRecorded()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork with { AllowPublicAccess = true }, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""",
            recordedTags: recorded);

        await Assert.ThrowsAsync<CommandException>(() =>
            Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None));

        Assert.Empty(azureCli.Deployments);
    }

    [Fact]
    public async Task ApplyAsync_LeavingPrivateModeRecordsPublicAndDropsTheNetworkTags()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(new NetworkOptions(Mode: NetworkModeChoice.Public)), CancellationToken.None);

        var merge = Assert.Single(azureCli.Commands, c => c.Contains("tag", StringComparer.Ordinal) && c.Contains("Merge", StringComparer.Ordinal));
        Assert.Contains("nimbus-network-mode=public", merge);
        Assert.Contains("nimbus-network-cleanup=pending", merge);
        Assert.Contains(azureCli.Commands, c => c.Contains("Delete", StringComparer.Ordinal) && c.Contains($"nimbus-network-resolver-subnet={ResolverSubnet}", StringComparer.Ordinal));
        Assert.DoesNotContain("networkMode=private", azureCli.Deployments[0].Arguments);
    }

    /// <summary>The pending marker is cleared only after the cleanup has run.</summary>
    [Fact]
    public async Task ApplyAsync_ClearsThePendingMarkerAfterTheCleanup()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(new NetworkOptions(Mode: NetworkModeChoice.Public)), CancellationToken.None);

        var endpointList = azureCli.Commands.FindIndex(c => c.Contains("private-endpoint", StringComparer.Ordinal) && c.Contains("list", StringComparer.Ordinal));
        var markerDelete = azureCli.Commands.FindIndex(c => c.Contains("Delete", StringComparer.Ordinal) && c.Contains("nimbus-network-cleanup=pending", StringComparer.Ordinal));
        Assert.InRange(markerDelete, endpointList + 1, int.MaxValue);
    }

    /// <summary>
    /// A public deployment that only uses a namespace override has a record but never had a
    /// private network: its apps' VNet integration, if any, is the customer's, not NimBus's.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_PublicDeploymentWithOnlyANamespaceOverrideRunsNoCleanup()
    {
        var recorded = NetworkIntent.ToTags(new NetworkOptions(Mode: NetworkModeChoice.Public), serviceBusNamespaceName: "sb-nimbus-dev-premium");
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("vnet-integration", StringComparer.Ordinal));
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal));
    }

    /// <summary>A cleanup interrupted on an earlier run continues, although the mode already reads public.</summary>
    [Fact]
    public async Task ApplyAsync_ContinuesAnInterruptedCleanup()
    {
        var recorded = new Dictionary<string, string>
        {
            [NetworkIntent.ModeTag] = "public",
            [NetworkIntent.CleanupPendingTag] = NetworkIntent.CleanupPendingValue,
        };
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Enabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.Contains(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal) && c.Contains("list", StringComparer.Ordinal));
        Assert.Contains(azureCli.Commands, c => c.Contains("Delete", StringComparer.Ordinal) && c.Contains("nimbus-network-cleanup=pending", StringComparer.Ordinal));
    }

    /// <summary>Public access is back on both deployments before any private resource is taken down.</summary>
    [Fact]
    public async Task ApplyAsync_LeavingPrivateModeCleansUpOnlyAfterBothDeployments()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork, serviceBusNamespaceName: null);
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(new NetworkOptions(Mode: NetworkModeChoice.Public)), CancellationToken.None);

        var lastDeployment = azureCli.Commands.FindLastIndex(c => c.Contains("deployment", StringComparer.Ordinal) && c.Contains("create", StringComparer.Ordinal));
        var endpointList = azureCli.Commands.FindIndex(c => c.Contains("private-endpoint", StringComparer.Ordinal) && c.Contains("list", StringComparer.Ordinal));
        Assert.Equal(2, azureCli.Deployments.Count);
        Assert.True(endpointList > lastDeployment, "cleanup must follow both deployments");
        Assert.Contains("[?tags.\"nimbus-deployment\"=='nimbus-dev'].id", azureCli.Commands[endpointList]);
    }

    [Fact]
    public async Task ApplyAsync_PublicDeploymentWithoutHistoryRunsNoCleanup()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("private-endpoint", StringComparer.Ordinal));
        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("vnet-integration", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_DeletesTheSqlAllowAzureRuleWhenTheServerGoesPrivate()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null, sqlRuleExists: true);

        await Deployer(azureCli).ApplyAsync(SqlOptions(PrivateNetwork), CancellationToken.None);

        var delete = Assert.Single(azureCli.Commands, c => c.Contains("firewall-rule", StringComparer.Ordinal) && c.Contains("delete", StringComparer.Ordinal));
        Assert.Contains("AllowAllWindowsAzureIps", delete);
        Assert.Contains("sql-nimbus-dev", delete);
    }

    /// <summary>During the transition public access, and so the rule, stay.</summary>
    [Fact]
    public async Task ApplyAsync_KeepsTheSqlAllowAzureRuleDuringTheTransition()
    {
        var azureCli = CustomerNetwork(existingServiceBus: null, sqlRuleExists: true);

        await Deployer(azureCli).ApplyAsync(SqlOptions(PrivateNetwork with { AllowPublicAccess = true }), CancellationToken.None);

        Assert.DoesNotContain(azureCli.Commands, c => c.Contains("firewall-rule", StringComparer.Ordinal));
    }

    [Fact]
    public async Task ApplyAsync_RecordedNamespaceOverrideIsReused()
    {
        var recorded = NetworkIntent.ToTags(PrivateNetwork, serviceBusNamespaceName: "sb-nimbus-dev-premium");
        var azureCli = CustomerNetwork(
            existingServiceBus: """{"tier":"Premium","capacity":1,"publicNetworkAccess":"Disabled"}""",
            recordedTags: recorded);

        await Deployer(azureCli).ApplyAsync(Options(NetworkOptions.None), CancellationToken.None);

        Assert.Contains("serviceBusNamespaceName=sb-nimbus-dev-premium", azureCli.Deployments[0].Arguments);
    }

    private static InfrastructureDeployer Deployer(RecordingAzureCliRunner azureCli, bool resolvesPrivately = true) =>
        new(
            new CommandContext(Path.GetTempPath()),
            azureCli,
            new PrivateEndpointDnsCheck(
                azureCli,
                (host, _) => Task.FromResult(new[]
                {
                    resolvesPrivately ? EndpointAddress(host) : System.Net.IPAddress.Parse("20.38.116.190"),
                }),
                (_, _) => Task.CompletedTask));

    // One address per endpoint, as the fake network interfaces report them.
    private static System.Net.IPAddress EndpointAddress(string host) => System.Net.IPAddress.Parse(
        host.Contains("servicebus", StringComparison.Ordinal) ? "10.20.0.4"
        : host.Contains("func-", StringComparison.Ordinal) ? "10.20.0.5"
        : "10.20.0.6");

    private static InfrastructureOptions Options(NetworkOptions network) =>
        new(
            "nimbus",
            "dev",
            "rg-nimbus-dev",
            ResourceNamePostFix: null,
            Location: "westeurope",
            WebAppVersion: "test",
            ResolverPlan: ResolverPlanChoice.FlexConsumption,
            Network: network);

    /// <summary>
    /// A customer VNet in West Europe whose private-endpoint subnet sits in a peered VNet in
    /// Sweden Central, so the endpoint location must come from the subnet, not the app.
    /// </summary>
    private static InfrastructureOptions SqlOptions(NetworkOptions network) =>
        Options(network) with
        {
            StorageProvider = StorageProviderChoice.SqlServer,
            SqlMode = SqlProvisioningMode.Provision,
            SqlAdminLogin = "nimbusadmin",
            SqlAdminPassword = "not-a-real-password",
        };

    private static RecordingAzureCliRunner CustomerNetwork(
        string? existingServiceBus,
        string resolverDelegation = "Microsoft.App/environments",
        IReadOnlyDictionary<string, string>? recordedTags = null,
        bool sqlRuleExists = false) =>
        new()
        {
            Responder = arguments =>
            {
                if (arguments.Contains("firewall-rule") && arguments.Contains("show"))
                {
                    return sqlRuleExists ? """{"name":"AllowAllWindowsAzureIps"}""" : "[]";
                }

                string? ValueOf(string option)
                {
                    var index = arguments.ToList().IndexOf(option);
                    return index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : null;
                }

                if (arguments.Contains("group") && arguments.Contains("show") && recordedTags is not null)
                {
                    return System.Text.Json.JsonSerializer.Serialize(new
                    {
                        id = RecordingAzureCliRunner.ResourceGroupIdPrefix + ValueOf("--name"),
                        tags = recordedTags,
                    });
                }

                if (arguments.Contains("subnet") && arguments.Contains("show"))
                {
                    return ValueOf("--ids") switch
                    {
                        PeSubnet => """{"name":"snet-private-endpoints","delegations":[]}""",
                        ResolverSubnet => $$"""{"name":"snet-resolver","delegations":["{{resolverDelegation}}"]}""",
                        WebAppSubnet => """{"name":"snet-management","delegations":["Microsoft.Web/serverFarms"]}""",
                        _ => null,
                    };
                }

                if (arguments.Contains("vnet") && arguments.Contains("show"))
                {
                    return ValueOf("--ids") == EndpointVnet ? "swedencentral" : "westeurope";
                }

                if (arguments.Contains("servicebus") && arguments.Contains("namespace") && arguments.Contains("show"))
                {
                    return existingServiceBus ?? string.Empty;
                }

                if (arguments.Contains("provider") && arguments.Contains("show"))
                {
                    return "Registered";
                }

                // Private endpoints: the NIC id carries the endpoint name, and the NIC lists
                // the host names the endpoint serves with its address.
                if (arguments.Contains("private-endpoint") && arguments.Contains("show"))
                {
                    return $"/subscriptions/x/resourceGroups/rg-nimbus-dev/providers/Microsoft.Network/networkInterfaces/{ValueOf("--name")}.nic";
                }

                if (arguments.Contains("nic") && arguments.Contains("show"))
                {
                    var nic = ValueOf("--ids") ?? string.Empty;
                    var (fqdns, ip) = nic.Contains("pe-sb-", StringComparison.Ordinal)
                        ? (new[] { "sb-nimbus-dev.servicebus.windows.net" }, "10.20.0.4")
                        : nic.Contains("pe-func-", StringComparison.Ordinal)
                            ? (new[] { "func-nimbus-dev-resolver.azurewebsites.net", "func-nimbus-dev-resolver.scm.azurewebsites.net" }, "10.20.0.5")
                            : (new[] { "webapp-nimbus-dev-management.azurewebsites.net", "webapp-nimbus-dev-management.scm.azurewebsites.net" }, "10.20.0.6");
                    return System.Text.Json.JsonSerializer.Serialize(new[] { new { ip, fqdns } });
                }

                return null;
            },
        };
}
