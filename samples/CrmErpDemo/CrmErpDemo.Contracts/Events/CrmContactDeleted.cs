using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NimBus.Core.Events;

namespace CrmErpDemo.Contracts.Events;

[Description("Published by CRM when a Contact is soft-deleted.")]
[SessionKey(nameof(ContactId))]
public class CrmContactDeleted : Event
{
    public static readonly CrmContactDeleted Example = new()
    {
        ContactId = Guid.Parse("9b8a7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c03"),
        DeletedAt = new DateTimeOffset(2026, 9, 21, 9, 30, 0, TimeSpan.Zero),
    };

    [Required]
    [Description("The CRM contact that was deleted. Session key for the contact's events.")]
    public Guid ContactId { get; set; }

    [Description("When the deletion occurred in CRM.")]
    public DateTimeOffset DeletedAt { get; set; }
}
