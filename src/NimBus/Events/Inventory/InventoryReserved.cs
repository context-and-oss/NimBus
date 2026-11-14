using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Inventory;

[Description("Published when inventory has been reserved for an order.")]
[SessionKey(nameof(OrderId))]
public class InventoryReserved : Event
{
    public static readonly InventoryReserved Example = new InventoryReserved
    {
        OrderId = Guid.Parse("2bb7d0b3-840f-4e54-a0d4-fb31a7cabf82"),
        ReservationId = Guid.Parse("4e6fa2aa-b74d-4f0f-a7f1-748277e24f85"),
        WarehouseCode = "EU-01",
        ReservedLines = 3,
    };

    [Required]
    [Description("The order that inventory was reserved for.")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("The reservation identifier assigned by the warehouse system.")]
    public Guid ReservationId { get; set; }

    [Required]
    [Description("The warehouse that reserved the stock.")]
    public string WarehouseCode { get; set; } = null!;

    [Range(1, int.MaxValue)]
    [Description("The number of order lines successfully reserved.")]
    public int ReservedLines { get; set; }

}
