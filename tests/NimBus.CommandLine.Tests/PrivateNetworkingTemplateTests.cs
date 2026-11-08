#pragma warning disable CA1707, CA2007
using System.Text.RegularExpressions;
using NimBus.CommandLine;
using Xunit;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// Pins the private networking rules of spec 034 in the embedded Bicep templates. CI has
/// no Bicep compiler, so these are text checks on the templates the CLI ships; compile
/// them with `az bicep build` when changing them.
/// </summary>
[Collection(ExtractedTemplatesCollection.Name)]
public class PrivateNetworkingTemplateTests
{
    private static readonly CommandContext Context = new(null);

    private static string Core => File.ReadAllText(Context.CoreBicepPath);

    private static string WebAppEntry => File.ReadAllText(Context.WebAppBicepPath);

    private static string Module(string fileName) => File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(Context.CoreBicepPath)!,
        "templates",
        fileName));

    [Fact]
    public void Public_mode_is_the_default_in_both_entry_templates()
    {
        Assert.Contains("param networkMode string = 'public'", Core, StringComparison.Ordinal);
        Assert.Contains("param networkMode string = 'public'", WebAppEntry, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the private state closes public access; public and private-transition keep it
    /// open (spec 034 §5.13), so an existing deployment can add private paths first.
    /// </summary>
    [Fact]
    public void Public_access_is_disabled_only_in_the_private_state()
    {
        const string state = "var publicAccess = isPrivate && !allowPublicAccess ? 'Disabled' : 'Enabled'";
        Assert.Contains(state, Core, StringComparison.Ordinal);
        Assert.Contains(state, WebAppEntry, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("servicebusNamespace.bicep")]
    [InlineData("cosmosDB.bicep")]
    [InlineData("azureSql.bicep")]
    [InlineData("storageaccount.bicep")]
    [InlineData("functionApp.bicep")]
    [InlineData("flexConsumptionFunctionApp.bicep")]
    [InlineData("webApp.bicep")]
    public void Every_reachable_resource_takes_its_public_access_from_the_network_state(string template)
    {
        Assert.Contains("param publicNetworkAccess string = 'Enabled'", Module(template), StringComparison.Ordinal);
    }

    [Fact]
    public void Core_template_passes_the_network_state_to_every_data_service_and_the_resolver()
    {
        // Service Bus, Cosmos, SQL, storage and both Resolver plan branches.
        var passes = Core.Split("publicNetworkAccess: publicAccess").Length - 1;
        Assert.Equal(6, passes);
    }

    /// <summary>
    /// Storage's firewall default decides access even while publicNetworkAccess is Enabled,
    /// so Deny must follow the locked state and never the transition (review finding 3).
    /// </summary>
    [Fact]
    public void Storage_firewall_denies_only_when_public_access_is_disabled()
    {
        var storage = Module("storageaccount.bicep");

        Assert.Contains("var isLocked = publicNetworkAccess == 'Disabled'", storage, StringComparison.Ordinal);
        Assert.Contains("defaultAction: isLocked ? 'Deny' : 'Allow'", storage, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_allow_all_azure_rule_exists_only_while_public_access_does()
    {
        Assert.Contains(
            "resource allowAzureRule 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = if (publicNetworkAccess == 'Enabled')",
            Module("azureSql.bicep"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The legacy vnetRouteAllEnabled flag routes application traffic only; allTraffic also
    /// routes configuration traffic such as managed-identity token requests (review finding 2).
    /// </summary>
    [Theory]
    [InlineData("functionApp.bicep")]
    [InlineData("webApp.bicep")]
    public void App_Service_apps_route_all_traffic_not_just_application_traffic(string template)
    {
        var text = Module(template);

        Assert.Contains("'Microsoft.Web/sites@2024-11-01'", text, StringComparison.Ordinal);
        Assert.Contains("allTraffic: true", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"^\s*vnetRouteAllEnabled\s*:", RegexOptions.Multiline), text);
    }

    [Fact]
    public void Elastic_Premium_resolver_monitors_its_own_scale_and_routes_its_content_share()
    {
        var elastic = Module("functionApp.bicep");

        Assert.Contains("functionsRuntimeScaleMonitoringEnabled: true", elastic, StringComparison.Ordinal);
        Assert.Contains("contentShareTraffic: true", elastic, StringComparison.Ordinal);
    }

    [Fact]
    public void Private_Service_Bus_is_Premium_and_non_partitioned()
    {
        var serviceBus = Module("servicebusNamespace.bicep");

        Assert.Contains("'Microsoft.ServiceBus/namespaces@2024-01-01'", serviceBus, StringComparison.Ordinal);
        Assert.Contains("premiumMessagingPartitions: 1", serviceBus, StringComparison.Ordinal);
        Assert.Contains("trustedServiceAccessEnabled: false", serviceBus, StringComparison.Ordinal);
        Assert.Contains(
            "var effectiveServiceBusSku = serviceBusSku == 'Premium' || (empty(serviceBusSku) && isPrivate) ? 'Premium' : 'Standard'",
            Core,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Invalid private-mode input must fail at template validation, before anything deploys.
    /// An unused variable is never evaluated, so each check is referenced from a module name.
    /// </summary>
    [Fact]
    public void Network_validation_is_evaluated_through_a_module_name()
    {
        Assert.Contains("name : 'ServicebusNamespaceDeploy${networkValidation}'", Core, StringComparison.Ordinal);
        Assert.Contains("name: 'webAppDeploy${networkValidation}'", WebAppEntry, StringComparison.Ordinal);
        Assert.Contains("fail('Private networking needs a Premium Service Bus namespace", Core, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Resolver joins the VNet only after every data-service endpoint exists, so it never
    /// starts in a network where its dependencies don't resolve (spec 034 §5.13).
    /// </summary>
    [Fact]
    public void Resolver_depends_on_every_data_service_private_endpoint()
    {
        const string dependsOn = """
              dependsOn: [
                peServiceBus
                peCosmos
                peSql
                peStorage
              ]
            """;
        var normalizedCore = Core.Replace("\r\n", "\n", StringComparison.Ordinal);
        var occurrences = normalizedCore.Split(dependsOn.Replace("\r\n", "\n", StringComparison.Ordinal)).Length - 1;

        Assert.Equal(2, occurrences);
    }

    [Fact]
    public void Nimbus_created_dns_links_fall_back_to_public_resolution()
    {
        var zones = Module("privateDnsZones.bicep");

        Assert.Contains("resolutionPolicy: 'NxDomainRedirect'", zones, StringComparison.Ordinal);
        Assert.Contains("registrationEnabled: false", zones, StringComparison.Ordinal);
    }

    /// <summary>The rollback cleanup deletes only resources carrying this tag (spec 034 §5.13).</summary>
    [Fact]
    public void Network_resources_carry_the_ownership_tag()
    {
        Assert.Contains("'nimbus-deployment': '${toLower(solutionId)}-${toLower(environment)}'", Core, StringComparison.Ordinal);
        Assert.Contains("'nimbus-deployment': '${toLower(solutionId)}-${toLower(environment)}'", WebAppEntry, StringComparison.Ordinal);
        Assert.Contains("tags: tags", Module("privateEndpoint.bicep"), StringComparison.Ordinal);
    }

    [Fact]
    public void External_dns_mode_creates_endpoints_without_zone_groups()
    {
        Assert.Contains("var registersDns = isPrivate && privateDnsMode != 'external'", Core, StringComparison.Ordinal);
        Assert.Contains("= if (!empty(privateDnsZoneIds))", Module("privateEndpoint.bicep"), StringComparison.Ordinal);
    }

    [Fact]
    public void Cosmos_template_no_longer_reads_account_keys()
    {
        Assert.DoesNotContain("listConnectionStrings", Module("cosmosDB.bicep"), StringComparison.Ordinal);
    }
}
