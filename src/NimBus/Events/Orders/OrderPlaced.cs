using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Orders;

[Description("Published when a customer places a new order.")]
[SessionKey(nameof(OrderId))]
public class OrderPlaced : Event
{
    public static readonly OrderPlaced Example = new OrderPlaced
    {
        OrderId = Guid.Parse("2bb7d0b3-840f-4e54-a0d4-fb31a7cabf82"),
        CustomerId = Guid.Parse("7b1f2765-3a3e-4ed6-9de1-e54fd6914aa5"),
        CurrencyCode = "EUR",
        TotalAmount = 249.95m,
        SalesChannel = "web",
    };

    [Required]
    [Description("The unique order identifier.")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("The customer that placed the order.")]
    public Guid CustomerId { get; set; }

    [Required]
    [Description("The ISO currency code for the order total.")]
    public string CurrencyCode { get; set; } = null!;

    [Range(0.01, 1000000)]
    [Description("The total amount of the order.")]
    public decimal TotalAmount { get; set; }

    [Description("The sales channel where the order was placed.")]
    public string SalesChannel { get; set; } = null!;

    [Description("When true, the subscriber handler will simulate a failure for testing error flows.")]
    public bool SimulateFailure { get; set; }

}
