using System.Globalization;
using System.Text.Json;
using NimBus.Core;
using NimBus.MessageStore;

namespace NimBus.CommandLine;

/// <summary>
/// Provisions the per-endpoint Cosmos tracking containers through the control plane as
/// part of topology provisioning. The runtime otherwise creates these lazily via the
/// SDK, which only works with account keys — Entra data-plane RBAC (what the deployed
/// apps use) cannot create containers, so under managed identity the first message on
/// an endpoint with a missing container fails with 403/5300. Containers that already
/// exist are left untouched (an operator may have customized their TTL).
/// </summary>
internal sealed class EndpointContainerProvisioner
{
    private const string DatabaseId = "MessageDatabase";

    private readonly IAzureCliRunner _az;
    private readonly Func<IPlatform> _platformFactory;

    internal EndpointContainerProvisioner(IAzureCliRunner az)
        : this(az, static () => new PlatformConfiguration())
    {
    }

    internal EndpointContainerProvisioner(IAzureCliRunner az, Func<IPlatform> platformFactory)
    {
        _az = az;
        _platformFactory = platformFactory;
    }

    internal async Task ApplyAsync(TopologyOptions options, CancellationToken cancellationToken)
    {
        var endpointIds = _platformFactory().Endpoints.Select(endpoint => endpoint.Id).ToList();

        // Validate the whole catalog before touching Azure so a reserved id fails
        // the run without leaving a half-provisioned container set behind.
        foreach (var endpointId in endpointIds)
        {
            CosmosContainerDefaults.EnsureNotReservedEndpointId(endpointId);
        }

        await _az.EnsureLoggedInAsync(cancellationToken).ConfigureAwait(false);

        var names = await ResourceNaming.ResolveDeployedNamesAsync(
            _az,
            options.SolutionId,
            options.Environment,
            options.ResourceGroupName,
            options.ServiceBusNamespaceName,
            options.ResourceNamePostfix,
            cancellationToken).ConfigureAwait(false);

        var existingJson = await _az.CaptureValueAsync(
            new[]
            {
                "cosmosdb", "sql", "container", "list",
                "--resource-group", options.ResourceGroupName,
                "--account-name", names.CosmosAccountName,
                "--database-name", DatabaseId,
                "--query", "[].name",
                "--output", "json",
            },
            cancellationToken,
            $"Failed to list Cosmos containers in '{names.CosmosAccountName}/{DatabaseId}'.").ConfigureAwait(false);

        // Ordinal: Cosmos container ids are case-sensitive, so 'billingendpoint'
        // does not satisfy the endpoint id 'BillingEndpoint'.
        var existing = new HashSet<string>(
            JsonSerializer.Deserialize<string[]>(existingJson) ?? Array.Empty<string>(),
            StringComparer.Ordinal);

        foreach (var endpointId in endpointIds)
        {
            if (existing.Contains(endpointId))
            {
                CliOutput.WriteLine($"Cosmos container '{endpointId}' already exists; leaving it untouched.");
                continue;
            }

            CliOutput.WriteLine($"Creating Cosmos container '{endpointId}'...");
            await _az.EnsureSuccessAsync(
                new[]
                {
                    "cosmosdb", "sql", "container", "create",
                    "--resource-group", options.ResourceGroupName,
                    "--account-name", names.CosmosAccountName,
                    "--database-name", DatabaseId,
                    "--name", endpointId,
                    "--partition-key-path", CosmosContainerDefaults.EndpointPartitionKeyPath,
                    "--ttl", CosmosContainerDefaults.EndpointContainerDefaultTimeToLive.ToString(CultureInfo.InvariantCulture),
                    "--output", "none",
                },
                cancellationToken,
                $"Failed to create Cosmos container '{endpointId}' in '{names.CosmosAccountName}/{DatabaseId}'.").ConfigureAwait(false);
        }
    }
}
