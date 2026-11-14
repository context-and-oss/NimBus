using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Customers;

[Description("Published when a new customer signs up and is ready to participate in the platform workflow.")]
[SessionKey(nameof(CustomerId))]
public class CustomerRegistered : Event
{
    public static readonly CustomerRegistered Example = new CustomerRegistered
    {
        CustomerId = Guid.Parse("7b1f2765-3a3e-4ed6-9de1-e54fd6914aa5"),
        Email = "alex@example.com",
        FullName = "Alex Example",
        Segment = "Starter",
    };

    [Required]
    [Description("The unique customer identifier.")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("The primary email address for the customer.")]
    public string Email { get; set; } = null!;

    [Required]
    [Description("The display name used by connected systems.")]
    public string FullName { get; set; } = null!;

    [Description("The customer segment assigned at registration time.")]
    public string Segment { get; set; } = null!;

}
