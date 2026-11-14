using CrmErpDemo.Contracts.Events;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Endpoints;

// Routing-keys showcase (Spec 045, docs/routing-keys.md): one source, several targets of the
// same kind. CRM publishes account events keyed by country, and each region runs its own hub.
// Every hub is its own endpoint, with its own subscription, sessions and failures, and all of
// them are served by the same adapter code (samples/CrmErpDemo/RegionalHub). ErpEndpoint
// declares no keys, so it keeps receiving every account.

/// <summary>A regional hub: takes created and updated accounts for its countries only.</summary>
public abstract class RegionalHubEndpoint : Endpoint
{
    /// <summary>Declares the hub as consumer of the account events for <paramref name="countries"/>.</summary>
    /// <param name="name">The name shown to people, such as "Nordic hub".</param>
    /// <param name="countries">The ISO 3166-1 alpha-2 country codes the hub claims.</param>
    protected RegionalHubEndpoint(string name, params string[] countries)
    {
        HubName = name;
        Consumes<CrmAccountCreated>(countries);
        Consumes<CrmAccountUpdated>(countries);
    }

    /// <summary>The name shown to people.</summary>
    public string HubName { get; }

    /// <inheritdoc />
    public override ISystem System => new RegionalHubSystem();
}

/// <summary>The hub for Denmark, Sweden, Norway and Finland.</summary>
public sealed class NordicHubEndpoint : RegionalHubEndpoint
{
    public NordicHubEndpoint() : base("Nordic hub", "DK", "SE", "NO", "FI") { }

    /// <inheritdoc />
    public override string Description =>
        "Regional hub for the Nordic countries. Takes CRM account events whose routing key (the country) is DK, SE, NO or FI.";
}

/// <summary>The hub for Germany, Austria and Switzerland.</summary>
public sealed class DachHubEndpoint : RegionalHubEndpoint
{
    public DachHubEndpoint() : base("DACH hub", "DE", "AT", "CH") { }

    /// <inheritdoc />
    public override string Description =>
        "Regional hub for Germany, Austria and Switzerland. Takes CRM account events whose routing key (the country) is DE, AT or CH.";
}

/// <summary>
/// The unmatched consumer: receives the account events whose country no hub claims, and those
/// with no country. Without it Service Bus would drop them silently, so platform validation
/// requires one. Its adapter dead-letters each message with a reason naming the key.
/// </summary>
public sealed class UnroutedHubEndpoint : Endpoint
{
    public UnroutedHubEndpoint()
    {
        ConsumesUnmatched<CrmAccountCreated>();
        ConsumesUnmatched<CrmAccountUpdated>();
    }

    /// <inheritdoc />
    public override ISystem System => new RegionalHubSystem();

    /// <inheritdoc />
    public override string Description =>
        "Unmatched consumer of the CRM account events: accounts in a country no regional hub claims arrive here and are dead-lettered with a reason naming the country.";
}

internal sealed class RegionalHubSystem : ISystem
{
    public string SystemId => "RegionalHub";
}

/// <summary>One regional hub as the demo apps need it: which endpoint, and which countries.</summary>
/// <param name="EndpointId">The endpoint id, which is also its topic and subscription name.</param>
/// <param name="Name">The name shown to people.</param>
/// <param name="Countries">The countries the hub claims, in ordinal order. Empty for the unrouted hub.</param>
/// <param name="IsUnrouted">True for the unmatched consumer.</param>
public sealed record RegionalHub(string EndpointId, string Name, IReadOnlyList<string> Countries, bool IsUnrouted);

/// <summary>
/// The regional hubs, read from the endpoint declarations above so the demo apps cannot
/// disagree with what the topology routes.
/// </summary>
public static class RegionalHubs
{
    private static readonly EventType<CrmAccountCreated> AccountCreated = new();

    /// <summary>Every hub: the regional ones, then the unrouted one.</summary>
    public static IReadOnlyList<RegionalHub> All { get; } =
    [
        Describe(new NordicHubEndpoint()),
        Describe(new DachHubEndpoint()),
        new RegionalHub(nameof(UnroutedHubEndpoint), "Unrouted", [], IsUnrouted: true),
    ];

    /// <summary>The hub with this endpoint id, or null.</summary>
    public static RegionalHub? Find(string? endpointId) =>
        All.FirstOrDefault(hub => string.Equals(hub.EndpointId, endpointId, StringComparison.Ordinal));

    /// <summary>
    /// The hub that receives an account in <paramref name="countryCode"/>. Keys are compared
    /// case-sensitively, as Service Bus compares them, so "dk" is unrouted.
    /// </summary>
    public static RegionalHub ForCountry(string? countryCode) =>
        All.FirstOrDefault(hub => hub.Countries.Contains(countryCode ?? string.Empty, StringComparer.Ordinal))
        ?? All.Single(hub => hub.IsUnrouted);

    private static RegionalHub Describe(RegionalHubEndpoint endpoint) =>
        new(endpoint.Id, endpoint.HubName, [.. endpoint.GetRoutingKeys(AccountCreated)], IsUnrouted: false);
}
