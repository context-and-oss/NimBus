using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is created.")]
[SessionKey(nameof(AccountId))]
// Routing-keys showcase (Spec 045): the country routes the account to its regional hub.
[RoutingKey(nameof(CountryCode))]
public class CrmAccountCreated : Event
{
    public static readonly CrmAccountCreated Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        LegalName = "Contoso Nordics ApS",
        TaxId = "DK12345678",
        CountryCode = "DK",
        CreatedAt = new DateTimeOffset(2026, 9, 21, 9, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account identifier. Session key for the end-to-end flow.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The legal name of the account.")]
    public string LegalName { get; set; } = string.Empty;

    // A tax id identifies a natural person for sole traders, so only the last four
    // characters stay visible: enough to match against what a caller reads out.
    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 4)]
    [Description("Tax identifier (VAT, EIN, etc).")]
    public string? TaxId { get; set; }

    [Required]
    [Description("ISO 3166-1 alpha-2 country code.")]
    public string CountryCode { get; set; } = string.Empty;

    [Description("When the account was created in CRM.")]
    public DateTimeOffset CreatedAt { get; set; }
}
