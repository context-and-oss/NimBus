using NimBus.Core.Events;
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;

namespace NimBus.Events.Shipping;

[Description("Published when a shipment leaves the warehouse.")]
[SessionKey(nameof(OrderId))]
public class ShipmentDispatched : Event
{
    public static readonly ShipmentDispatched Example = new ShipmentDispatched
    {
        OrderId = Guid.Parse("2bb7d0b3-840f-4e54-a0d4-fb31a7cabf82"),
        ShipmentId = Guid.Parse("b154664f-e6a3-477d-bf12-8bd4b7a274f0"),
        Carrier = "Contoso Logistics",
        TrackingNumber = "TRACK-123456",
        DispatchedAt = DateTime.UtcNow,
    };

    [Required]
    [Description("The order included in the shipment.")]
    public Guid OrderId { get; set; }

    [Required]
    [Description("The shipment identifier assigned by the warehouse.")]
    public Guid ShipmentId { get; set; }

    [Required]
    [Description("The delivery carrier.")]
    public string Carrier { get; set; } = null!;

    [Required]
    [Description("The carrier tracking number.")]
    public string TrackingNumber { get; set; } = null!;

    [Description("The UTC timestamp when the shipment left the warehouse.")]
    public DateTime DispatchedAt { get; set; }

}
