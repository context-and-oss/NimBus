using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is created. Origin distinguishes the CRM-originated round-trip ack from a customer that originated directly in ERP. Session-keyed on AccountId so per-customer ordering is preserved.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerCreated : Event
{
    public static readonly ErpCustomerCreated Example = new()
    {
        Origin = CustomerOrigin.Crm,
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        ErpCustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        CustomerNumber = "C-100245",
        LegalName = "Contoso Nordics ApS",
        TaxId = "DK12345678",
        CountryCode = "DK",
    };

    [Required]
    [Description("Where the customer originated. Crm = ack of a CRM-originated CrmAccountCreated round-trip; Erp = customer created directly in ERP.")]
    public CustomerOrigin Origin { get; set; }

    [Required]
    [Description("Session key for per-customer ordering. When Origin = Crm this is the CRM account id; when Origin = Erp it falls back to ErpCustomerId so the field is still a stable key.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The ERP-side customer identifier.")]
    public Guid ErpCustomerId { get; set; }

    [Required]
    [Description("The ERP customer number (human-readable reference).")]
    public string CustomerNumber { get; set; } = string.Empty;

    [Required]
    [Description("The legal name of the customer.")]
    public string LegalName { get; set; } = string.Empty;

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("Tax identifier (VAT, EIN, etc).")]
    public string? TaxId { get; set; }

    [Required]
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = string.Empty;
}

public enum CustomerOrigin
{
    Erp = 0,
    Crm = 1,
}
