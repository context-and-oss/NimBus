using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by ERP when a Customer is soft-deleted. Receiving systems should mark their corresponding row as deleted.")]
[SessionKey(nameof(AccountId))]
public class ErpCustomerDeleted : Event
{
    public static readonly ErpCustomerDeleted Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        ErpCustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        DeletedAt = new DateTimeOffset(2026, 9, 21, 12, 5, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("Session key. CRM account id when the customer is linked; falls back to ErpCustomerId.")]
    public Guid AccountId { get; set; }

    [Required]
    [Description("The ERP-side identifier of the deleted customer.")]
    public Guid ErpCustomerId { get; set; }

    [Description("When the deletion occurred in ERP.")]
    public DateTimeOffset DeletedAt { get; set; }
}
