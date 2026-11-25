namespace Erp.Adapter.Functions.Clients;

public interface IErpApiClient
{
    Task<ErpCustomerResponse> UpsertCustomerAsync(Guid crmAccountId, CustomerUpsertPayload payload, CancellationToken ct);
    Task UpsertContactAsync(Guid contactId, ContactUpsertPayload payload, CancellationToken ct);
    Task MarkCustomerByCrmIdDeletedAsync(Guid crmAccountId, CancellationToken ct);
    Task MarkContactDeletedAsync(Guid contactId, CancellationToken ct);

    /// <summary>Looks up the ERP customer linked to a CRM account; null when none exists.</summary>
    Task<ErpCustomerStatusResponse?> GetCustomerByCrmAccountIdAsync(Guid crmAccountId, CancellationToken ct);

    /// <summary>The whole ERP customer linked to a CRM account, for the adapter MCP server; null when none exists.</summary>
    Task<ErpCustomerRecord?> GetCustomerByCrmIdAsync(Guid crmAccountId, CancellationToken ct);

    /// <summary>Places the ERP customer linked to a CRM account on credit hold; false when no customer exists.</summary>
    Task<bool> PlaceCreditHoldByCrmIdAsync(Guid crmAccountId, string? reason, CancellationToken ct);
}

public record CustomerUpsertPayload(Guid? ErpCustomerId, string LegalName, string? TaxId, string CountryCode);
// CrmAccountId carries the CRM account id from the inbound event. The ERP API
// resolves it to a local Customer.Id via Customers.CrmAccountId before storing
// the contact, so the contact ends up linked to the correct ERP customer.
public record ContactUpsertPayload(Guid? CrmAccountId, string FirstName, string LastName, string? Email, string? Phone);
public record ErpCustomerResponse(Guid Id, string CustomerNumber, string LegalName);
public record ErpCustomerStatusResponse(Guid Id, string CustomerNumber, string LegalName, bool CreditHold, bool IsDeleted);
