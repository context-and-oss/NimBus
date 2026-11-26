using NimBus.Core.Messages.PII;

namespace Erp.Adapter.Functions.Clients;

/// <summary>
/// An ERP customer as erp-api returns it from <c>GET /api/customers/by-crm/{crmAccountId}</c>,
/// served by the adapter MCP server's <c>adapter_get_record</c>. A class with properties, not a
/// positional record, so the <see cref="SensitiveAttribute"/> sits on the property the masker reads.
/// </summary>
public sealed class ErpCustomerRecord
{
    public Guid Id { get; set; }

    public string CustomerNumber { get; set; } = string.Empty;

    public string LegalName { get; set; } = string.Empty;

    // Masked as on the CRM account events it came from: the last four characters stay visible.
    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    public string? TaxId { get; set; }

    public string CountryCode { get; set; } = string.Empty;

    public Guid? CrmAccountId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary><c>Crm</c> for a customer created from a CRM account, <c>Erp</c> for one created in ERP.</summary>
    public string Origin { get; set; } = string.Empty;

    public bool IsDeleted { get; set; }

    public bool CreditHold { get; set; }
}
