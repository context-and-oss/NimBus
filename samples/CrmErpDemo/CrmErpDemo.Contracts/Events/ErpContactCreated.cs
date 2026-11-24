using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;
using NimBus.Core.Messages.PII;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Contact is created. CustomerId is the ERP customer id the contact belongs to; the receiving CRM resolves it to its local account id via Account.ErpCustomerId.")]
[SessionKey(nameof(ContactId))]
public class ErpContactCreated : Event
{
    public static readonly ErpContactCreated Example = new()
    {
        ContactId = Guid.Parse("c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e05"),
        CustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        FirstName = "Mikkel",
        LastName = "Sørensen",
        Email = "mikkel.sorensen@contoso.example",
        Phone = "+45 40 98 76 54",
    };

    [Required]
    [Description("The contact identifier, shared by CRM and ERP. Session key for the contact's events.")]
    public Guid ContactId { get; set; }

    [Description("The ERP customer this contact belongs to (ERP-side id). Receivers must resolve to their local FK if needed.")]
    public Guid? CustomerId { get; set; }

    [Required]
    [Description("The contact's first name.")]
    public string FirstName { get; set; } = string.Empty;

    [Required]
    [Description("The contact's last name.")]
    public string LastName { get; set; } = string.Empty;

    [EmailAddress]
    [Sensitive(Mode = MaskMode.Hash)]
    [Description("The contact's email address.")]
    public string? Email { get; set; }

    [Sensitive(Mode = MaskMode.PartialReveal, Reveal = 3)]
    [Description("The contact's phone number.")]
    public string? Phone { get; set; }
}
