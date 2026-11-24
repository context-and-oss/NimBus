using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is updated. Carries the editable fields plus the linkage ids.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerUpdated : Event
{
    public static readonly ErpCustomerUpdated Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        CrmAccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        ErpCustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        CustomerNumber = "C-100245",
        LegalName = "Contoso Nordics A/S",
        TaxId = "DK12345678",
        CountryCode = "DK",
    };

    [Required]
    [Description("Session key. CRM account id when the customer is linked to a CRM account; falls back to ErpCustomerId so the field is always populated.")]
    public Guid AccountId { get; set; }

    [Description("CRM account id when the customer is linked to CRM; null only for an unlinked ERP-originated customer.")]
    public Guid? CrmAccountId { get; set; }

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
