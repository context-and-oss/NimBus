using System.Text.Json;

namespace NimBus.CommandLine;

/// <summary>
/// Leaving private mode (spec 034 §5.13). Incremental deployments keep resources that drop
/// out of the template, so after the public deployment has reopened access, nb removes the
/// apps' VNet integration and deletes the network resources it owns: private endpoints and,
/// with privateDnsMode 'create', the VNet links and zones it created. Ownership is the
/// nimbus-deployment tag every such resource carries; nothing without it is touched, so
/// customer VNets, subnets and 'existing' or 'external' DNS zones stay as they are.
/// Every step is idempotent: an interrupted cleanup continues on the next run.
/// </summary>
internal sealed class PrivateNetworkCleanup
{
    public const string OwnershipTag = "nimbus-deployment";

    private readonly IAzureCliRunner _az;

    public PrivateNetworkCleanup(IAzureCliRunner az)
    {
        _az = az;
    }

    /// <summary>Matches the templates' nimbus-deployment tag, including any resource-name postfix.</summary>
    public static string OwnerValue(DeploymentNames names) =>
        names.ResourceNamePostfix is null
            ? $"{names.SolutionId}-{names.Environment}"
            : $"{names.SolutionId}-{names.Environment}-{names.ResourceNamePostfix}";

    public async Task RunAsync(
        string resourceGroupName,
        string owner,
        DeploymentNames names,
        PrivateDnsModeChoice? recordedDnsMode,
        CancellationToken cancellationToken)
    {
        // 1. Apps leave the VNet first; public access is already back, so they reach
        //    Service Bus, the store and storage on the public path from here on.
        await RemoveVnetIntegrationAsync("webapp", resourceGroupName, names.WebAppName, cancellationToken).ConfigureAwait(false);
        await RemoveVnetIntegrationAsync("functionapp", resourceGroupName, names.ResolverFunctionAppName, cancellationToken).ConfigureAwait(false);

        // 2. Private endpoints. Their zone groups, and the A records those wrote, go with them.
        var endpoints = await ListAsync(
            new[]
            {
                "network", "private-endpoint", "list",
                "--resource-group", resourceGroupName,
                "--query", OwnedFilter(owner, "id"),
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        if (endpoints.Count > 0 && recordedDnsMode == PrivateDnsModeChoice.External)
        {
            CliOutput.WriteLine(
                "Deleting the NimBus private endpoints. With external DNS, remove any records your own DNS servers hold for them; " +
                "records written by an Azure Policy zone group are removed with the endpoints.");
        }

        foreach (var endpointId in endpoints)
        {
            CliOutput.WriteLine($"Deleting private endpoint '{endpointId[(endpointId.LastIndexOf('/') + 1)..]}'...");
            await _az.EnsureSuccessAsync(
                new[] { "network", "private-endpoint", "delete", "--ids", endpointId },
                cancellationToken,
                $"Could not delete the private endpoint '{endpointId}'. Rerun the same command to continue the cleanup.").ConfigureAwait(false);
        }

        // 3. DNS NimBus created ('create' mode). Decided by the ownership tag, not the recorded
        //    mode, which a completed switch to public no longer carries: an interrupted cleanup
        //    must still find the zones. Zones in 'existing' or 'external' mode live in the
        //    customer's resource groups and carry no NimBus tag.
        await DeleteOwnedDnsAsync(resourceGroupName, owner, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveVnetIntegrationAsync(string appKind, string resourceGroupName, string appName, CancellationToken cancellationToken)
    {
        var integrations = await ListAsync(
            new[] { appKind, "vnet-integration", "list", "--resource-group", resourceGroupName, "--name", appName, "--output", "json" },
            cancellationToken).ConfigureAwait(false);
        if (integrations.Count == 0)
        {
            return;
        }

        CliOutput.WriteLine($"Removing VNet integration from '{appName}'...");
        await _az.EnsureSuccessAsync(
            new[] { appKind, "vnet-integration", "remove", "--resource-group", resourceGroupName, "--name", appName },
            cancellationToken,
            $"Could not remove the VNet integration of '{appName}'. Rerun the same command to continue the cleanup.").ConfigureAwait(false);
    }

    private async Task DeleteOwnedDnsAsync(string resourceGroupName, string owner, CancellationToken cancellationToken)
    {
        var zones = await ListAsync(
            new[]
            {
                "network", "private-dns", "zone", "list",
                "--resource-group", resourceGroupName,
                "--query", OwnedFilter(owner, "name"),
                "--output", "json",
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var zone in zones)
        {
            var ownedLinks = await ListAsync(
                new[]
                {
                    "network", "private-dns", "link", "vnet", "list",
                    "--resource-group", resourceGroupName,
                    "--zone-name", zone,
                    "--query", OwnedFilter(owner, "name"),
                    "--output", "json",
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var link in ownedLinks)
            {
                await _az.EnsureSuccessAsync(
                    new[] { "network", "private-dns", "link", "vnet", "delete", "--resource-group", resourceGroupName, "--zone-name", zone, "--name", link, "--yes" },
                    cancellationToken,
                    $"Could not delete the DNS link '{link}' of zone '{zone}'. Rerun the same command to continue the cleanup.").ConfigureAwait(false);
            }

            // Someone else linked the zone to their network: keep it rather than break their resolution.
            var remainingLinks = await ListAsync(
                new[]
                {
                    "network", "private-dns", "link", "vnet", "list",
                    "--resource-group", resourceGroupName,
                    "--zone-name", zone,
                    "--query", "[].name",
                    "--output", "json",
                },
                cancellationToken).ConfigureAwait(false);
            if (remainingLinks.Count > 0)
            {
                CliOutput.WriteLine($"Keeping the private DNS zone '{zone}': it is still linked to {string.Join(", ", remainingLinks)}, which NimBus did not create.");
                continue;
            }

            CliOutput.WriteLine($"Deleting private DNS zone '{zone}'...");
            await _az.EnsureSuccessAsync(
                new[] { "network", "private-dns", "zone", "delete", "--resource-group", resourceGroupName, "--name", zone, "--yes" },
                cancellationToken,
                $"Could not delete the private DNS zone '{zone}'. Rerun the same command to continue the cleanup.").ConfigureAwait(false);
        }
    }

    private static string OwnedFilter(string owner, string field) =>
        $"[?tags.\"{OwnershipTag}\"=='{owner}'].{field}";

    /// <summary>
    /// Runs a list query. Only a successful answer that is a JSON array counts; a failed or
    /// unreadable one stops the cleanup, because treating it as "nothing there" could skip
    /// removing VNet integration and then delete the endpoints the apps still route through.
    /// Everything done so far is idempotent, so rerunning the command continues from here.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await _az.TryRunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new CommandException(
                $"The private network cleanup stopped: 'az {string.Join(' ', arguments.Take(4))}' failed, so it cannot tell what is left. " +
                $"Rerun the same command to continue.{Environment.NewLine}{result.StandardError}".TrimEnd());
        }

        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new CommandException(
                    $"The private network cleanup stopped: 'az {string.Join(' ', arguments.Take(4))}' returned an unexpected answer. Rerun the same command to continue.");
            }

            return document.RootElement.EnumerateArray()
                .Select(element => element.ValueKind == JsonValueKind.String
                    ? element.GetString()
                    : element.TryGetProperty("name", out var name) ? name.GetString() : element.ToString())
                .OfType<string>()
                .ToList();
        }
        catch (JsonException)
        {
            throw new CommandException(
                $"The private network cleanup stopped: 'az {string.Join(' ', arguments.Take(4))}' returned output that is not JSON. Rerun the same command to continue.");
        }
    }
}
