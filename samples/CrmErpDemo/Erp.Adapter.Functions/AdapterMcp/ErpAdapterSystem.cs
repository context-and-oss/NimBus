using CrmErpDemo.Contracts.Commands;
using CrmErpDemo.Contracts.Events;
using Erp.Adapter.Functions.Clients;
using NimBus.Adapter.Mcp;

namespace Erp.Adapter.Functions.AdapterMcp;

/// <summary>
/// What the ERP adapter's MCP server (Spec 051 §7) shows of ERP: a customer by the CRM account it
/// came from, how each CRM event the adapter handles maps to ERP's fields, and whether ERP is
/// reachable. Everything is read through the adapter's own ERP clients.
/// </summary>
public sealed class ErpAdapterSystem(IErpApiClient erp, IServiceModeClient modes) : IAdapterSystem
{
    /// <summary>An ERP customer, keyed by the CRM account id it was created from.</summary>
    public const string Customer = "customer";

    private static readonly Dictionary<string, string> Settings = new()
    {
        ["entity"] = Customer,
        ["key"] = "the CRM account id (a guid); ERP stores it on the customer as CrmAccountId",
        ["customerUpsert"] = "PUT /api/customers/by-crm/{crmAccountId}: creates the customer once, then updates it",
        ["countryCodeRule"] = "ISO 3166-1 alpha-2; ERP refuses any other value with 422",
        ["retries"] = "NimBus retries a rate-limited pricing call (ErpPricingThrottledException) up to 3 times; any other failure is Failed on its first attempt",
        ["serviceMode"] = "while ERP is in service mode the adapter rejects every message, which dead-letters it",
    };

    private static readonly Dictionary<string, AdapterMapping> Mappings = new(StringComparer.Ordinal)
    {
        [nameof(CrmAccountCreated)] = Mapping(
            new AdapterFieldMapping(nameof(CrmAccountCreated.AccountId), "CrmAccountId", "The customer's key; a new customer gets the next CustomerNumber"),
            new AdapterFieldMapping(nameof(CrmAccountCreated.LegalName), "LegalName"),
            new AdapterFieldMapping(nameof(CrmAccountCreated.TaxId), "TaxId"),
            new AdapterFieldMapping(nameof(CrmAccountCreated.CountryCode), "CountryCode", "Must be ISO 3166-1 alpha-2")),
        [nameof(CrmAccountUpdated)] = Mapping(
            new AdapterFieldMapping(nameof(CrmAccountUpdated.AccountId), "CrmAccountId"),
            new AdapterFieldMapping(nameof(CrmAccountUpdated.ErpCustomerId), "Id", "Finds the customer when no customer carries the CRM account id yet"),
            new AdapterFieldMapping(nameof(CrmAccountUpdated.LegalName), "LegalName"),
            new AdapterFieldMapping(nameof(CrmAccountUpdated.TaxId), "TaxId"),
            new AdapterFieldMapping(nameof(CrmAccountUpdated.CountryCode), "CountryCode", "Must be ISO 3166-1 alpha-2")),
        [nameof(CrmAccountDeleted)] = Mapping(
            new AdapterFieldMapping(nameof(CrmAccountDeleted.AccountId), "CrmAccountId", "The customer is kept and marked IsDeleted")),
        [nameof(CrmContactCreated)] = ContactMapping(),
        [nameof(CrmContactUpdated)] = ContactMapping(),
        [nameof(CrmContactDeleted)] = Mapping(
            new AdapterFieldMapping(nameof(CrmContactDeleted.ContactId), "ErpContact.Id", "The contact is kept and marked IsDeleted")),
        [nameof(ErpCreditCheckRequested)] = Mapping(
            new AdapterFieldMapping(nameof(ErpCreditCheckRequested.AccountId), "CrmAccountId", "Read only: answers NotFound, Deleted, OnHold or Active")),
        [nameof(PlaceCustomerOnCreditHold)] = Mapping(
            new AdapterFieldMapping(nameof(PlaceCustomerOnCreditHold.AccountId), "CrmAccountId", "Sets CreditHold; a customer that does not exist yet is skipped"),
            new AdapterFieldMapping(nameof(PlaceCustomerOnCreditHold.Reason), "(not stored)")),
    };

    /// <inheritdoc />
    public IReadOnlyCollection<string> Entities { get; } = [Customer];

    /// <inheritdoc />
    public async Task<object?> GetRecordAsync(string entity, string key, CancellationToken cancellationToken)
        => Guid.TryParse(key, out var crmAccountId)
            ? await erp.GetCustomerByCrmIdAsync(crmAccountId, cancellationToken)
            : null;

    /// <inheritdoc />
    public AdapterMapping? GetMapping(string eventTypeId) => Mappings.GetValueOrDefault(eventTypeId);

    /// <inheritdoc />
    public async Task<AdapterHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        var probe = await modes.ProbeAsync(cancellationToken);
        if (!probe.Reachable)
            return new AdapterHealth(false, probe.Problem ?? "erp-api did not answer");
        return probe.ServiceMode
            ? new AdapterHealth(false, "ERP is in service mode: the adapter rejects every message until it ends")
            : new AdapterHealth(true, "erp-api answered and ERP is not in service mode");
    }

    private static AdapterMapping Mapping(params AdapterFieldMapping[] fields) => new() { Fields = fields, Settings = Settings };

    private static AdapterMapping ContactMapping() => Mapping(
        new AdapterFieldMapping(nameof(CrmContactCreated.ContactId), "ErpContact.Id"),
        new AdapterFieldMapping(nameof(CrmContactCreated.AccountId), "ErpContact.CustomerId", "ERP resolves the CRM account id to its customer's Id"),
        new AdapterFieldMapping(nameof(CrmContactCreated.FirstName), "ErpContact.FirstName"),
        new AdapterFieldMapping(nameof(CrmContactCreated.LastName), "ErpContact.LastName"),
        new AdapterFieldMapping(nameof(CrmContactCreated.Email), "ErpContact.Email"),
        new AdapterFieldMapping(nameof(CrmContactCreated.Phone), "ErpContact.Phone"));
}
