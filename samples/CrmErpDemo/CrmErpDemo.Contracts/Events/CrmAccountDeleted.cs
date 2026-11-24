using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when an Account is soft-deleted. Receiving systems should mark their corresponding row as deleted.")]
[SessionKey(nameof(AccountId))]
public class CrmAccountDeleted : Event
{
    public static readonly CrmAccountDeleted Example = new()
    {
        AccountId = Guid.Parse("6f1c0a5e-4b2d-4e8a-9c3f-1a2b3c4d5e01"),
        ErpCustomerId = Guid.Parse("e2a7c9d1-5b3f-4a6e-8d2c-7f1e0b9a6c02"),
        DeletedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM account that was deleted. Session key for the end-to-end flow.")]
    public Guid AccountId { get; set; }

    [Description("ERP customer id if the account was previously linked; null otherwise.")]
    public Guid? ErpCustomerId { get; set; }

    [Description("When the deletion occurred in CRM.")]
    public DateTimeOffset DeletedAt { get; set; }
}
