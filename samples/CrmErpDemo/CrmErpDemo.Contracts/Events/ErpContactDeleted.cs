using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Contact is soft-deleted.")]
[SessionKey(nameof(ContactId))]
public class ErpContactDeleted : Event
{
    public static readonly ErpContactDeleted Example = new()
    {
        ContactId = Guid.Parse("c1d2e3f4-a5b6-4c7d-8e9f-0a1b2c3d4e05"),
        DeletedAt = new DateTimeOffset(2026, 9, 21, 10, 15, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The identifier of the contact deleted in ERP, shared by CRM and ERP. Session key for the contact's events.")]
    public Guid ContactId { get; set; }

    [Description("When the deletion occurred in ERP.")]
    public DateTimeOffset DeletedAt { get; set; }
}
