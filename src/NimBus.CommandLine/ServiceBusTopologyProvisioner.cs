using Azure.Messaging.ServiceBus.Administration;
using NimBus.Core;
using CoreProvisioner = NimBus.ServiceBus.Provisioning.ServiceBusTopologyProvisioner;

namespace NimBus.CommandLine;

/// <summary>
/// CLI-side wrapper for <see cref="CoreProvisioner"/>: resolves the namespace connection
/// string via the Azure CLI for the built-in <see cref="PlatformConfiguration"/> and then
/// delegates the actual topology work to the shared provisioning library.
/// </summary>
internal sealed class ServiceBusTopologyProvisioner
{
    private readonly AzureCliRunner _az;
    private readonly Func<TopologyOptions, CancellationToken, Task<string>> _connectionStringProvider;
    private readonly Func<string, ServiceBusAdministrationClient> _clientFactory;
    private readonly Func<IPlatform> _platformFactory;

    /// <param name="platformFactory">
    /// Catalog to provision. Null keeps the built-in platform; `nb topology apply --assembly`
    /// supplies a customer's own catalog, which is the only way to provision endpoints that
    /// are not compiled into this CLI.
    /// </param>
    internal ServiceBusTopologyProvisioner(AzureCliRunner az, Func<IPlatform>? platformFactory = null)
        : this(
            az,
            static (options, cancellationToken, runner) => ReadConnectionStringAsync(runner, options, cancellationToken),
            static connectionString => new ServiceBusAdministrationClient(connectionString),
            platformFactory ?? (static () => new PlatformConfiguration()))
    {
    }

    internal ServiceBusTopologyProvisioner(AzureCliRunner az, string connectionString, Func<IPlatform>? platformFactory = null)
        : this(
            az,
            (_, _, _) => Task.FromResult(connectionString),
            static value => new ServiceBusAdministrationClient(value),
            platformFactory ?? (static () => new PlatformConfiguration()))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
    }

    internal ServiceBusTopologyProvisioner(
        AzureCliRunner az,
        Func<TopologyOptions, CancellationToken, AzureCliRunner, Task<string>> connectionStringProvider,
        Func<string, ServiceBusAdministrationClient> clientFactory,
        Func<IPlatform> platformFactory)
    {
        _az = az;
        _connectionStringProvider = (options, cancellationToken) => connectionStringProvider(options, cancellationToken, _az);
        _clientFactory = clientFactory;
        _platformFactory = platformFactory;
    }

    internal async Task ApplyAsync(TopologyOptions options, CancellationToken cancellationToken)
    {
        var connectionString = await _connectionStringProvider(options, cancellationToken).ConfigureAwait(false);

        var core = new CoreProvisioner(
            _clientFactory(connectionString),
            _platformFactory,
            CoreProvisioner.IsEmulator(connectionString),
            CliOutput.WriteLine);

        await core.ApplyAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ReadConnectionStringAsync(AzureCliRunner az, TopologyOptions options, CancellationToken cancellationToken)
    {
        await az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        var names = await ResolveNamesAsync(az, options, cancellationToken).ConfigureAwait(false);

        // Topology goes through the Service Bus data plane, which a private deployment only
        // exposes through its private endpoint (spec 034 §5.7).
        await PrivateNetworkPreflight.RunIfPrivateAsync(
            az,
            new PrivateEndpointDnsCheck(az),
            options.ResourceGroupName,
            new[] { PrivateEndpointNames.ServiceBus(names.ServiceBusNamespace) },
            options.DnsWait,
            "provisioning the Service Bus topology",
            cancellationToken).ConfigureAwait(false);

        return await az.CaptureValueAsync(
            new[]
            {
                "servicebus", "namespace", "authorization-rule", "keys", "list",
                "--resource-group", options.ResourceGroupName,
                "--namespace-name", names.ServiceBusNamespace,
                "--name", "RootManageSharedAccessKey",
                "--query", "primaryConnectionString",
                "--output", "tsv",
            },
            cancellationToken,
            $"Failed to read the Service Bus connection string for '{names.ServiceBusNamespace}'.").ConfigureAwait(false);
    }

    /// <summary>
    /// Without --service-bus-namespace-name or --resource-name-postfix, follows the values
    /// 'nb infra apply' recorded on the resource group (spec 034 §5.13), so both commands
    /// address the same namespace.
    /// </summary>
    internal static Task<DeploymentNames> ResolveNamesAsync(IAzureCliRunner az, TopologyOptions options, CancellationToken cancellationToken) =>
        ResourceNaming.ResolveDeployedNamesAsync(
            az,
            options.SolutionId,
            options.Environment,
            options.ResourceGroupName,
            options.ServiceBusNamespaceName,
            options.ResourceNamePostfix,
            cancellationToken);
}
