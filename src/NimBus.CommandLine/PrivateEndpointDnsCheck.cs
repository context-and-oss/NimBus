using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace NimBus.CommandLine;

/// <summary>How one host name resolved, compared with its private endpoint's address.</summary>
internal enum DnsCheckOutcome
{
    Match,

    /// <summary>No record yet, e.g. a customer policy has not written it.</summary>
    NoRecord,

    /// <summary>A public address: this machine does not use the privatelink zone.</summary>
    PublicAddress,

    /// <summary>A private address other than the endpoint's: a stale record.</summary>
    OtherPrivateAddress,
}

/// <summary>A host name a private endpoint serves, and the address it must resolve to.</summary>
internal sealed record ExpectedEndpoint(string Fqdn, IPAddress Address, string PrivateEndpointName);

internal sealed record DnsCheckFailure(string Fqdn, IPAddress Expected, DnsCheckOutcome Outcome, string? Resolved);

internal sealed record DnsCheckResult(IReadOnlyList<DnsCheckFailure> Failures)
{
    public bool Succeeded => Failures.Count == 0;

    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var failure in Failures)
        {
            text.Append("  ").Append(failure.Fqdn).Append(" (expected ").Append(failure.Expected).Append("): ");
            text.AppendLine(failure.Outcome switch
            {
                DnsCheckOutcome.NoRecord => "no DNS record. The private DNS records are not provisioned yet, or this machine's DNS cannot see the privatelink zone.",
                DnsCheckOutcome.PublicAddress => $"resolved to the public address {failure.Resolved}. This machine does not use the private DNS zone: run nb from a runner inside the network, or link its VNet to the zone.",
                DnsCheckOutcome.OtherPrivateAddress => $"resolved to {failure.Resolved}, another private address. A stale record points elsewhere; update or remove it.",
                _ => "resolved correctly.",
            });
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>Private endpoint names, as deploy.core.bicep and deploy.webapp.bicep create them.</summary>
internal static class PrivateEndpointNames
{
    public static string ServiceBus(string namespaceName) => $"pe-{namespaceName}-namespace";

    public static string Site(string siteName) => $"pe-{siteName}-sites";
}

/// <summary>
/// The pre-flight DNS check (spec 034 §5.7). Before a step that needs the private network,
/// the machine running nb must resolve each private endpoint's host names to the endpoint's
/// own address. Records can arrive late (a customer's DeployIfNotExists policy, forwarders
/// caching the old public answer), so the check retries with backoff until a deadline and
/// then names each host that still does not resolve, with the likely cause.
/// </summary>
internal sealed class PrivateEndpointDnsCheck
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly IAzureCliRunner _az;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PrivateEndpointDnsCheck(
        IAzureCliRunner az,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _az = az;
        _resolve = resolve ?? Dns.GetHostAddressesAsync;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>
    /// Reads, for each private endpoint, the host names it serves and its address from its
    /// network interface. That works whichever DNS mode is in use. Every endpoint must report
    /// at least one host name with a valid address: an endpoint without that metadata would
    /// otherwise contribute nothing to check, and the guard would pass without testing it.
    /// </summary>
    public async Task<IReadOnlyList<ExpectedEndpoint>> ReadExpectedAsync(
        string resourceGroupName,
        IEnumerable<string> privateEndpointNames,
        CancellationToken cancellationToken)
    {
        var expected = new List<ExpectedEndpoint>();
        foreach (var endpointName in privateEndpointNames)
        {
            var nicId = await _az.CaptureValueAsync(
                new[]
                {
                    "network", "private-endpoint", "show",
                    "--resource-group", resourceGroupName,
                    "--name", endpointName,
                    "--query", "networkInterfaces[0].id",
                    "--output", "tsv",
                },
                cancellationToken,
                $"Could not read the private endpoint '{endpointName}'. Has 'nb infra apply --network-mode private' completed for this deployment?").ConfigureAwait(false);

            using var nic = await _az.CaptureJsonAsync(
                new[]
                {
                    "network", "nic", "show",
                    "--ids", nicId,
                    "--query", "ipConfigurations[].{ip:privateIPAddress, fqdns:privateLinkConnectionProperties.fqdns}",
                    "--output", "json",
                },
                cancellationToken,
                $"Could not read the network interface of private endpoint '{endpointName}'.").ConfigureAwait(false);

            var found = 0;
            if (nic.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var configuration in nic.RootElement.EnumerateArray())
                {
                    if (!configuration.TryGetProperty("ip", out var ipElement)
                        || ipElement.ValueKind != JsonValueKind.String
                        || !IPAddress.TryParse(ipElement.GetString(), out var address)
                        || !configuration.TryGetProperty("fqdns", out var fqdns)
                        || fqdns.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var fqdn in fqdns.EnumerateArray()
                        .Where(f => f.ValueKind == JsonValueKind.String)
                        .Select(f => f.GetString())
                        .Where(f => !string.IsNullOrWhiteSpace(f)))
                    {
                        expected.Add(new ExpectedEndpoint(fqdn!, address, endpointName));
                        found++;
                    }
                }
            }

            if (found == 0)
            {
                throw new CommandException(
                    $"The private endpoint '{endpointName}' reports no host names with a private address on its network interface, so this machine's DNS cannot be checked against it. " +
                    "If the endpoint was just created, rerun in a few minutes; otherwise check its connection state in the portal.");
            }
        }

        return expected;
    }

    /// <summary>Checks every host name, retrying the ones that do not match until <paramref name="wait"/> has passed.</summary>
    public async Task<DnsCheckResult> VerifyAsync(IReadOnlyList<ExpectedEndpoint> expected, TimeSpan wait, CancellationToken cancellationToken)
    {
        var pending = expected.ToList();
        var failures = new List<DnsCheckFailure>();
        var elapsed = TimeSpan.Zero;
        var nextDelay = FirstRetryDelay;

        while (true)
        {
            failures.Clear();
            foreach (var endpoint in pending)
            {
                var resolved = await TryResolveAsync(endpoint.Fqdn, cancellationToken).ConfigureAwait(false);
                var outcome = Classify(endpoint.Address, resolved);
                if (outcome != DnsCheckOutcome.Match)
                {
                    failures.Add(new DnsCheckFailure(endpoint.Fqdn, endpoint.Address, outcome, resolved is { Length: > 0 } ? string.Join(", ", resolved.Select(a => a.ToString())) : null));
                }
            }

            if (failures.Count == 0 || elapsed >= wait)
            {
                return new DnsCheckResult(failures.ToList());
            }

            pending = pending.Where(endpoint => failures.Any(failure => failure.Fqdn == endpoint.Fqdn)).ToList();
            var delay = nextDelay < wait - elapsed ? nextDelay : wait - elapsed;
            CliOutput.WriteLine($"Waiting for {failures.Count} private endpoint name(s) to resolve privately ({(int)elapsed.TotalSeconds}s of {(int)wait.TotalSeconds}s)...");
            await _delay(delay, cancellationToken).ConfigureAwait(false);
            elapsed += delay;
            nextDelay = nextDelay * 2 < MaxRetryDelay ? nextDelay * 2 : MaxRetryDelay;
        }
    }

    public static DnsCheckOutcome Classify(IPAddress expected, IReadOnlyList<IPAddress>? resolved)
    {
        if (resolved is null || resolved.Count == 0) return DnsCheckOutcome.NoRecord;
        if (resolved.Any(address => address.Equals(expected))) return DnsCheckOutcome.Match;
        return resolved.Any(IsPrivate) ? DnsCheckOutcome.OtherPrivateAddress : DnsCheckOutcome.PublicAddress;
    }

    internal static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Unique local addresses (fc00::/7).
            return (address.GetAddressBytes()[0] & 0xFE) == 0xFC;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
            || (bytes[0] == 192 && bytes[1] == 168)
            || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127); // shared address space (RFC 6598)
    }

    private async Task<IPAddress[]?> TryResolveAsync(string fqdn, CancellationToken cancellationToken)
    {
        try
        {
            return await _resolve(fqdn, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

/// <summary>
/// Runs the DNS check where the deployment's recorded network state asks for it: a private
/// deployment fails the step on a mismatch, a deployment in transition only warns because
/// public access still works, and a public one skips the check.
/// </summary>
internal static class PrivateNetworkPreflight
{
    public static async Task RunIfPrivateAsync(
        IAzureCliRunner az,
        PrivateEndpointDnsCheck check,
        string resourceGroupName,
        IReadOnlyList<string> privateEndpointNames,
        TimeSpan? wait,
        string stepDescription,
        CancellationToken cancellationToken)
    {
        // A soft read: public deployments must behave exactly as before, so a resource group
        // that cannot be read or carries no record means "public, no check".
        var tags = await ResourceGroupTags.TryReadTagsAsync(az, resourceGroupName, cancellationToken).ConfigureAwait(false);
        var state = NetworkIntent.FromTags(tags)?.State ?? NetworkState.Public;
        if (state == NetworkState.Public)
        {
            return;
        }

        await RunAsync(check, resourceGroupName, privateEndpointNames, wait, failOnMismatch: state == NetworkState.Private, stepDescription, cancellationToken).ConfigureAwait(false);
    }

    public static async Task RunAsync(
        PrivateEndpointDnsCheck check,
        string resourceGroupName,
        IReadOnlyList<string> privateEndpointNames,
        TimeSpan? wait,
        bool failOnMismatch,
        string stepDescription,
        CancellationToken cancellationToken)
    {
        CliOutput.WriteLine($"Checking that this machine resolves the private endpoints before {stepDescription}...");
        IReadOnlyList<ExpectedEndpoint> expected;
        try
        {
            expected = await check.ReadExpectedAsync(resourceGroupName, privateEndpointNames, cancellationToken).ConfigureAwait(false);
        }
        catch (CommandException exception) when (!failOnMismatch)
        {
            // Private-transition: public access still works, so unreadable endpoint metadata
            // is worth a warning, not a stop. Locking treats it as an error (failOnMismatch).
            CliOutput.WriteLine($"Warning: the private endpoints could not be checked yet. Fix this before locking the deployment: {exception.Message}");
            return;
        }

        if (expected.Count == 0)
        {
            // Nothing to compare means nothing was verified; never let that pass as success.
            throw new CommandException($"Stopped before {stepDescription}: no private endpoint host names were found to check.");
        }
        var result = await check.VerifyAsync(expected, failOnMismatch ? wait ?? PrivateEndpointDnsCheck.DefaultWait : TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            return;
        }

        if (failOnMismatch)
        {
            throw new CommandException($"Stopped before {stepDescription}: this machine does not reach the private endpoints.{Environment.NewLine}{result.Describe()}");
        }

        CliOutput.WriteLine(
            $"Warning: these names do not resolve to their private endpoints yet. Public access still works in the private-transition state, " +
            $"but fix this before locking the deployment:{Environment.NewLine}{result.Describe()}");
    }
}

/// <summary>
/// A resource group's id and tags, where nb records the network setup (spec 034 §5.13), and its
/// region, where the templates place new resources unless --location says otherwise.
/// </summary>
internal sealed record ResourceGroupInfo(string Id, IReadOnlyDictionary<string, string> Tags, string? Location = null);

internal static class ResourceGroupTags
{
    /// <summary>The resource group's tags, or none when it cannot be read.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> TryReadTagsAsync(IAzureCliRunner az, string resourceGroupName, CancellationToken cancellationToken)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var result = await az.TryRunAsync(
            new[] { "group", "show", "--name", resourceGroupName, "--query", "tags", "--output", "json" },
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return tags;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in document.RootElement.EnumerateObject())
                {
                    tags[tag.Name] = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? string.Empty : tag.Value.ToString();
                }
            }
        }
        catch (JsonException)
        {
            // Unreadable output: treat as no record.
        }

        return tags;
    }

    public static async Task<ResourceGroupInfo> ReadAsync(IAzureCliRunner az, string resourceGroupName, CancellationToken cancellationToken)
    {
        using var document = await az.CaptureJsonAsync(
            new[] { "group", "show", "--name", resourceGroupName, "--query", "{id:id, location:location, tags:tags}", "--output", "json" },
            cancellationToken,
            $"Could not read the resource group '{resourceGroupName}'. Create it first and check that the deploying identity can read it.").ConfigureAwait(false);

        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()!
            : throw new CommandException($"Could not read the id of resource group '{resourceGroupName}'.");

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var tag in tagsElement.EnumerateObject())
            {
                tags[tag.Name] = tag.Value.ValueKind == JsonValueKind.String ? tag.Value.GetString() ?? string.Empty : tag.Value.ToString();
            }
        }

        var location = root.TryGetProperty("location", out var locationElement) && locationElement.ValueKind == JsonValueKind.String
            ? locationElement.GetString()
            : null;

        return new ResourceGroupInfo(id, tags, string.IsNullOrWhiteSpace(location) ? null : location);
    }
}
