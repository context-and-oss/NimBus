using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is updated.")]
[SessionKey(nameof(AccountId))]
// Routing-keys showcase (Spec 045): the country routes the account to its regional hub.
[RoutingKey(nameof(CountryCode))]
public class CrmAccountUpdated : Event
{
    public static readonly CrmAccountUpdated Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        ErpCustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        LegalName = "Contoso Nordics A/S",
        TaxId = "DK12345678",
        CountryCode = "DK",
        UpdatedAt = new DateTimeOffset(2026, 9, 21, 11, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account identifier. Session key for the end-to-end flow.")]
    public Guid AccountId { get; set; }

    [Description("ERP customer id if this CRM account was previously linked to an ERP-originated or mirrored customer.")]
    public Guid? ErpCustomerId { get; set; }

    [Required]
    [Description("The legal name of the account.")]
    public string LegalName { get; set; } = string.Empty;

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("Tax identifier (VAT, EIN, etc).")]
    public string? TaxId { get; set; }

    [Required]
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = string.Empty;

    [Description("When the update occurred in CRM.")]
    public DateTimeOffset UpdatedAt { get; set; }
}
